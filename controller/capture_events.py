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

Press Ctrl-C to stop. Restart the controller afterwards:

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
"""
import select
import sys
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


def main(paths):
    if not paths:
        print("usage: uv run capture_events.py <event-path> [<event-path> ...]")
        sys.exit(2)

    print(f"Opening {len(paths)} device(s)...")
    devices = open_devices(paths)
    if not devices:
        print("No devices opened; nothing to capture.")
        sys.exit(1)

    print()
    print("Reading. Press Ctrl-C to stop.")
    print("-" * 70)

    fd_to_device = {d.fd: d for d in devices}
    prefix_each_line = len(devices) > 1

    # Pad the device-path prefix to the longest path so the columns align,
    # which makes scanning multi-device output much easier.
    max_path_len = max(len(d.path) for d in devices)

    try:
        while True:
            ready, _, _ = select.select(fd_to_device.keys(), [], [])
            for fd in ready:
                dev = fd_to_device[fd]
                for event in dev.read():
                    prefix = (
                        f"[{dev.path:<{max_path_len}}] "
                        if prefix_each_line
                        else ""
                    )

                    if event.type == ecodes.EV_SYN:
                        print(f"{prefix}--- syn ---")
                        continue

                    t_name = type_name(event.type)
                    c_name = code_name(event.type, event.code)
                    print(
                        f"{prefix}{t_name:10} {c_name:24} "
                        f"code={event.code:>5} value={event.value}"
                    )
    except KeyboardInterrupt:
        print()
        print("stopped.")


if __name__ == "__main__":
    main(sys.argv[1:])