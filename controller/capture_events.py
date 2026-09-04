#!/usr/bin/env python3
"""Janus event capture tool.

Reads one or more /dev/input event nodes and pretty-prints every event,
so we can figure out what evdev codes a given device interface emits
when its various buttons/keys are pressed.

Run from the controller directory so uv can find the evdev dependency:

    cd ~/janus/controller
    sudo systemctl stop janus-controller   # free up the main mouse/kbd nodes
    uv run capture_events.py /dev/input/by-id/usb-Razer_Razer_Basilisk_V3-if01-event-kbd

Multiple paths can be passed; events from all of them are interleaved.
The per-line prefix shows the actual /dev/input/eventN path of the
emitting interface (NOT the friendly name -- multiple interfaces of the
same physical device often share a name, so name is ambiguous):

    uv run capture_events.py \
        /dev/input/by-id/usb-Razer_Razer_Basilisk_V3-event-mouse \
        /dev/input/by-id/usb-Razer_Razer_Basilisk_V3-if01-event-kbd

Three ways to stop, in order of preference:

  1. Escape on any captured keyboard device. Read directly off evdev,
     so it always works as long as one of the opened devices emits
     KEY_ESC. Requires that you actually opened a keyboard node --
     a mouse-only capture won't have Escape available.

  2. --timeout SECONDS. Auto-exits after N wall-clock seconds. Useful
     when the janus-controller service is stopped (so Ctrl-C in the
     SSH terminal has no way to land -- keys don't reach the PC) AND
     no keyboard device was opened (so in-band Escape isn't available
     either). Set it once, click furiously, wait it out.

  3. Ctrl-C in the terminal running the script. Only works if
     something is delivering keystrokes to that terminal -- normally
     via janus, or via a second keyboard plugged directly into the
     PC running the SSH session.

Restart the controller afterwards:

    sudo systemctl start janus-controller

What gets printed:
  * KEY events  -> code name (e.g. KEY_LEFTCTRL, KEY_S, BTN_LEFT) + value
                   (0=up, 1=down, 2=repeat).
  * REL events  -> axis name (REL_X / REL_Y / REL_WHEEL etc.) + delta.
  * MSC events  -> miscellaneous (most often MSC_SCAN, the raw HID scan
                   code -- useful when a "vendor-specific" key shows up
                   without a KEY_ name).
  * SYN events  -> reporting boundary (one tick = one logical action).
                   Printed as a blank-ish separator so it's clear where
                   each logical event ends.

Codes that evdev doesn't have a symbolic name for are shown as raw
numbers; that's still useful (we can map them in the controller later).

Each printed event line is prefixed with two timestamps in seconds:

    [t=  1.234 Δ= 0.005] EV_KEY BTN_MIDDLE ...

  * t   -- elapsed time since the first event, rebased to zero so the
           clock reads t=0.000 at the first line regardless of system
           uptime.
  * Δ   -- delta from the previous printed event of any type.

Both come from the kernel-provided event.timestamp(), not Python's
print time, so print-loop latency doesn't distort the delta. That
distinction matters when the whole point is to distinguish a bounced
switch (re-press < ~20ms after release) from a real double-click
(gap of 100ms+).
"""
import argparse
import select
import sys
import time
from evdev import InputDevice, ecodes


def open_devices(paths):
    devices = []
    for p in paths:
        try:
            dev = InputDevice(p)
        except Exception as ex:
            print(f"  ! failed to open {p}: {ex}")
            continue
        # Show both the friendly name and the underlying eventN path on
        # open. The path is what we'll use in per-event prefixes.
        print(f"  opened: {dev.path}  ({dev.name})")
        print(f"      symlink target: {p}")
        devices.append(dev)
    return devices


def code_name(type_, code):
    """Best-effort symbolic name for an event code."""
    try:
        name = ecodes.bytype[type_][code]
        if isinstance(name, (list, tuple)):
            return "/".join(name)
        return name
    except (KeyError, AttributeError):
        return f"<unknown code {code}>"


def type_name(type_):
    try:
        return ecodes.EV[type_]
    except (KeyError, AttributeError):
        return f"<type {type_}>"


def any_device_emits_esc(devices):
    """True if at least one opened device advertises KEY_ESC in its
    capability map. Used to warn at startup when the in-band Escape
    exit won't be available (e.g. mouse-only capture)."""
    for dev in devices:
        caps = dev.capabilities().get(ecodes.EV_KEY, [])
        if ecodes.KEY_ESC in caps:
            return True
    return False


def main(paths, timeout=None):
    print(f"Opening {len(paths)} device(s)...")
    devices = open_devices(paths)
    if not devices:
        print("No devices opened; nothing to capture.")
        sys.exit(1)

    esc_available = any_device_emits_esc(devices)

    # Compose the "how to stop" line from whichever exits are actually
    # available for this invocation. Ctrl-C is always listed since it
    # requires no capability check, though whether it can *reach* the
    # script depends on the SSH-input path outside this process.
    exits = []
    if esc_available:
        exits.append("Escape on a captured keyboard")
    if timeout is not None:
        exits.append(f"auto-exit after {timeout:g}s")
    exits.append("Ctrl-C in this terminal")

    print()
    print("Reading. To stop: " + ", or ".join(exits) + ".")
    if not esc_available and timeout is None:
        print("(No opened device emits KEY_ESC and no --timeout was set,")
        print(" so if Ctrl-C can't reach this terminal you'll be stuck.")
        print(" Add a keyboard event node or pass --timeout SECONDS.)")
    print("-" * 70)

    fd_to_device = {d.fd: d for d in devices}
    prefix_each_line = len(devices) > 1

    # Pad the device-path prefix to the longest path so the columns align,
    # which makes scanning multi-device output much easier.
    max_path_len = max(len(d.path) for d in devices)

    # Absolute deadline (monotonic clock, not wall-clock, so it's immune
    # to system time jumps). None means "run until Escape or Ctrl-C."
    deadline = time.monotonic() + timeout if timeout is not None else None

    # Timing anchors for the per-event prefix. Sourced from the kernel
    # event timestamp (event.timestamp()), NOT time.time() at print --
    # print latency would corrupt the delta between rapidly-adjacent
    # events, which is exactly what we're measuring when hunting for
    # switch chatter (bounce < ~20ms) vs a real double-click (>100ms).
    # first_ts is set on the very first event so the visible clock
    # starts at t=0.000 instead of at whatever wall-clock uptime the
    # kernel is at.
    first_ts = None
    prev_ts = None

    stop = False
    try:
        while not stop:
            # Compute per-iteration select() timeout from the deadline.
            # An empty ready-list means the timeout expired with no
            # events pending, which is our cue to exit.
            if deadline is not None:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    print()
                    print(f"Timeout ({timeout:g}s) reached; stopping.")
                    break
                ready, _, _ = select.select(fd_to_device.keys(), [], [], remaining)
                if not ready:
                    print()
                    print(f"Timeout ({timeout:g}s) reached; stopping.")
                    break
            else:
                ready, _, _ = select.select(fd_to_device.keys(), [], [])

            for fd in ready:
                dev = fd_to_device[fd]
                for event in dev.read():
                    # In-band escape hatch: pressing Escape (key-down)
                    # on any captured device exits cleanly. Checked
                    # before the SYN short-circuit below so it fires
                    # on the KEY event itself, not on the following
                    # SYN report.
                    if (
                        event.type == ecodes.EV_KEY
                        and event.code == ecodes.KEY_ESC
                        and event.value == 1
                    ):
                        print()
                        print("Escape pressed; stopping.")
                        stop = True
                        break

                    # Kernel timestamp for this event. Rebased so the
                    # first event we print reads as t=0.000; delta is
                    # from the previous printed event (any type),
                    # which is what makes switch-chatter obvious --
                    # a bounced BTN_MIDDLE re-press will appear a few
                    # ms after the release, while a real double-click
                    # is 100ms+ out.
                    ts = event.timestamp()
                    if first_ts is None:
                        first_ts = ts
                        prev_ts = ts
                    t_rel = ts - first_ts
                    t_delta = ts - prev_ts
                    prev_ts = ts
                    time_prefix = f"[t={t_rel:8.3f} Δ={t_delta:6.3f}] "

                    dev_prefix = (
                        f"[{dev.path:<{max_path_len}}] "
                        if prefix_each_line
                        else ""
                    )

                    if event.type == ecodes.EV_SYN:
                        print(f"{time_prefix}{dev_prefix}--- syn ---")
                        continue

                    t_name = type_name(event.type)
                    c_name = code_name(event.type, event.code)
                    print(
                        f"{time_prefix}{dev_prefix}{t_name:10} {c_name:24} "
                        f"code={event.code:>5} value={event.value}"
                    )
                if stop:
                    break
    except KeyboardInterrupt:
        print()
        print("stopped.")


def _parse_args(argv):
    parser = argparse.ArgumentParser(
        description=(
            "Capture and print evdev events from one or more input "
            "device nodes. See module docstring for exit options."
        ),
    )
    parser.add_argument(
        "paths",
        nargs="+",
        metavar="EVENT_PATH",
        help=(
            "/dev/input/... event node(s) to open. Multiple paths "
            "interleave; per-line prefixes show which device emitted."
        ),
    )
    parser.add_argument(
        "--timeout",
        type=float,
        default=None,
        metavar="SECONDS",
        help=(
            "Auto-exit after this many seconds. Fractional values "
            "allowed. Use when neither Ctrl-C nor in-band Escape can "
            "reach the script."
        ),
    )
    args = parser.parse_args(argv)
    if args.timeout is not None and args.timeout <= 0:
        parser.error("--timeout must be a positive number of seconds")
    return args


if __name__ == "__main__":
    args = _parse_args(sys.argv[1:])
    main(args.paths, timeout=args.timeout)