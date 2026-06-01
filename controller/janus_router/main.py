"""Janus controller entry point.

Owns the composition root and the main event loop: opens devices and
serials, wires up routing/inbound/input_events modules, starts the
serial reader threads, then runs the select() / event-dispatch loop
until SIGINT/SIGTERM or the configured quit command.

Invoked via the shim `input_router.py` so the existing
`uv run input_router.py` systemd unit keeps working unchanged.
"""
import os
import queue
import select
import signal
import sys
import threading
import time

import serial
from evdev import InputDevice, ecodes

from janus_router.config import (
    load_config,
    load_device_config_file,
    normalize_path_list,
    parse_args,
    validate_command_keys,
)
from janus_router.inbound import handle_serial_line, set_verbose as inbound_set_verbose
from janus_router.input_events import (
    InputState,
    process_event,
    release_all_inputs,
    release_inputs_for_switch,
    set_verbose as input_events_set_verbose,
)
from janus_router import routing
from janus_router.serial_io import configure_uart, open_serial, serial_reader, write_line


running = True


def handle_signal(signum, frame):
    global running
    running = False


def run() -> int:
    global running

    # Load config; CLI args (--mouse, --personal-uart, --verbose, etc.)
    # override individual fields. With no flags and no config.yaml,
    # everything falls back to DEFAULT_CONFIG above.
    config = load_config()
    config = load_device_config_file(config)
    try:
        config = parse_args(config)
        validate_command_keys(config["commands"])
    except ValueError as ex:
        print(f"Config error: {ex}", file=sys.stderr)
        return 1

    mouse_paths = normalize_path_list(config["devices"]["mouse"], "devices.mouse")
    keyboard_paths = normalize_path_list(config["devices"]["keyboard"], "devices.keyboard")
    personal_uart_path = config["devices"]["personal_uart"]
    work_uart_path = config["devices"]["work_uart"]

    if not mouse_paths:
        print("Config error: devices.mouse is empty.", file=sys.stderr)
        return 1
    if not keyboard_paths:
        print("Config error: devices.keyboard is empty.", file=sys.stderr)
        return 1

    # Read the verbose flag once and distribute to the three modules that
    # care about it (inbound, input_events, routing). Each owns its own
    # module-level _verbose copy mutated through set_verbose() -- avoids
    # a global import dance and lets each module evolve its verbose
    # behavior independently.
    verbose = bool(config["logging"]["verbose"])
    inbound_set_verbose(verbose)
    input_events_set_verbose(verbose)
    routing.configure(config["switching"])
    routing.set_verbose(verbose)

    cmd_personal = config["commands"]["personal"].lower()
    cmd_work = config["commands"]["work"].lower()
    cmd_clipboard = config["commands"]["clipboard"].lower()
    cmd_quit = config["commands"]["quit"].lower()

    signal.signal(signal.SIGINT, handle_signal)
    signal.signal(signal.SIGTERM, handle_signal)

    # Tell the kernel not to interrupt blocking syscalls when SIGINT or
    # SIGTERM arrives -- let the syscall complete, then deliver the
    # signal. Without this, a SIGTERM during termios.tcdrain() (inside
    # pyserial's flush()) raises termios.error: (4, 'Interrupted system
    # call') and the main loop exits with a traceback instead of cleanly.
    # The signal handler still fires after the syscall returns, sets
    # running = False, and the loop notices at the next iteration.
    signal.siginterrupt(signal.SIGINT, False)
    signal.siginterrupt(signal.SIGTERM, False)

    # Open every unique path once. We dedupe by the canonical (resolved)
    # path so two by-id symlinks pointing at the same /dev/input/eventN
    # are not opened twice. For each opened device, build sets of which
    # ROLES (mouse / keyboard) the config asked for. Dispatch is then by
    # EVENT TYPE in the read loop, not by these sets -- so the sets are
    # only used here for the startup banner and to confirm the config
    # actually requested each device.
    #
    # Note on dispatch: REL_* and BTN_LEFT/RIGHT/MIDDLE go to the mouse
    # handler regardless of which role list a fd appeared in. KEY_* (non
    # BTN) goes to the keyboard handler. The role lists are essentially a
    # promise -- "this fd's events are wanted" -- not a per-event filter.
    def _canonical(p: str) -> str:
        try:
            return os.path.realpath(p)
        except Exception:
            return p

    open_devices: dict[str, InputDevice] = {}   # canonical path -> device
    path_to_canonical: dict[str, str] = {}      # configured path -> canonical
    role_of_canonical: dict[str, set[str]] = {} # canonical -> {"mouse"|"keyboard"}

    def _open_role(path: str, role: str) -> bool:
        canon = _canonical(path)
        path_to_canonical[path] = canon
        if canon not in open_devices:
            try:
                device = InputDevice(path)
                
                # Grab the device for exclusive access. Without this, every
                # keystroke fans out to systemd-logind, the kernel's VT
                # layer, and any other listener -- so a forwarded KEY_POWER
                # (which the Razer mouse can emit via Synapse mappings) or
                # Ctrl+Alt+Del would trigger system actions ON THE PI in
                # addition to being forwarded. grab() makes the Pi deaf to
                # these devices except through us, which is what we want:
                # our job is to forward, not to react. The grab releases
                # automatically when this process exits.
                device.grab() # If you do not want to use .grab(), replace with: (nothing — just open and assign)
                
                open_devices[canon] = device
            except OSError as ex:
                print(
                    f"Failed to open {role} device {path}: {ex}",
                    file=sys.stderr,
                )
                return False
        role_of_canonical.setdefault(canon, set()).add(role)
        return True

    open_failed = False
    for p in mouse_paths:
        if not _open_role(p, "mouse"):
            open_failed = True
    for p in keyboard_paths:
        if not _open_role(p, "keyboard"):
            open_failed = True

    if open_failed:
        for d in open_devices.values():
            try:
                d.close()
            except Exception:
                pass
        return 1

    try:
        configure_uart(personal_uart_path)
        configure_uart(work_uart_path)
    except Exception as ex:
        print(f"Failed to configure UART: {ex}", file=sys.stderr)
        for d in open_devices.values():
            try:
                d.close()
            except Exception:
                pass
        return 1

    try:
        personal_serial = open_serial(personal_uart_path)
        print(f"personal uart connected: {personal_uart_path}")
    except Exception as ex:
        print(f"Failed to open personal uart {personal_uart_path}: {ex}", file=sys.stderr)
        for d in open_devices.values():
            try:
                d.close()
            except Exception:
                pass
        return 1

    try:
        work_serial = open_serial(work_uart_path)
        print(f"work uart connected: {work_uart_path}")
    except Exception as ex:
        print(f"Failed to open work uart {work_uart_path}: {ex}", file=sys.stderr)
        personal_serial.close()
        for d in open_devices.values():
            try:
                d.close()
            except Exception:
                pass
        return 1

    routing.init(personal_serial, work_serial)

    serial_lines: queue.Queue[str] = queue.Queue()
    stop_event = threading.Event()

    readers = [
        threading.Thread(
            target=serial_reader,
            args=("P", personal_serial, serial_lines, stop_event),
            daemon=True,
        ),
        threading.Thread(
            target=serial_reader,
            args=("W", work_serial, serial_lines, stop_event),
            daemon=True,
        ),
    ]

    for reader in readers:
        reader.start()

    # Periodic-announce interval -- the only switching constant the
    # main loop itself reads. The rest live inside routing.py.
    TARGET_ANNOUNCE_INTERVAL_SECONDS = config["switching"][
        "target_announce_interval_seconds"
    ]

    last_target_announce_time = 0.0

    # Per-tick mutable state -- mouse REL accumulators, button-down
    # bools, pressed keys. Lives for the whole event loop; process_event
    # and the SYN_REPORT branch below mutate it in place.
    state = InputState()

    print("Janus.InputRouter started. Press Ctrl+C to stop.")
    print("input devices:")
    for canon, dev in open_devices.items():
        roles = ",".join(sorted(role_of_canonical[canon]))
        print(f"  [{roles:<14}] {dev.path}  ({dev.name})")
    print(f"personal: {personal_uart_path}")
    print(f"work:     {work_uart_path}")
    print("commands: p=personal, w=work, c=clipboard, q=quit")
    print(f"auto edge-switch: {'enabled' if routing.auto_edge_switch_enabled() else 'DISABLED'}")
    print(f"active target: {routing.active_target()}")

    try:
        while running:
            # Periodic TARGET announcement. Fires on the first iteration
            # (last_target_announce_time == 0.0) and every interval after,
            # so any agent that comes up later catches its state.
            now = time.monotonic()
            if now - last_target_announce_time >= TARGET_ANNOUNCE_INTERVAL_SECONDS:
                write_line(personal_serial, f"TARGET {routing.active_target()}")
                write_line(work_serial, f"TARGET {routing.active_target()}")
                last_target_announce_time = now

            # Process any lines read from the serial ports
            while True:
                try:
                    queued = serial_lines.get_nowait()
                except queue.Empty:
                    break

                if queued.startswith("ERROR "):
                    print(queued, file=sys.stderr)
                    continue

                source_name, line = queued.split("|", 1)
                switch_target = handle_serial_line(
                    source_name,
                    line,
                    personal_serial,
                    work_serial,
                    routing.personal_display,
                    routing.work_display,
                    routing.personal_cursor,
                    routing.work_cursor,
                )

                if switch_target is not None:
                    # Remote-triggered manual switch (agent's 's' key or global
                    # hotkey). Same code path as the stdin handler below, just
                    # sourced from the wire instead. Honor the cooldown (defense
                    # against held-hotkey ping-pong: each side's agent has the
                    # hotkey registered, so a key held across the switch can fire
                    # the destination's hotkey and bounce right back). Also ignore
                    # if we're already on that target.
                    if routing.cooling_down():
                        print(f"remote switch from {source_name} ignored (cooldown)")
                    elif switch_target == routing.active_target():
                        print(f"remote switch from {source_name} ignored (already on {switch_target})")
                    else:
                        (
                            state.left_button_down,
                            state.right_button_down,
                            state.middle_button_down,
                        ) = release_inputs_for_switch(
                            routing.active_target(), personal_serial, work_serial,
                            state.left_button_down, state.right_button_down, state.middle_button_down,
                            state.pressed_keys,
                        )
                        routing.perform_manual_switch(
                            switch_target,
                            log_suffix=f"(REMOTE from {source_name})",
                        )
                        last_target_announce_time = routing.last_switch_time()

            # Wait for input events or serial lines
            input_fds = [d.fd for d in open_devices.values()]
            fds = input_fds + [sys.stdin]
            ready, _, _ = select.select(fds, [], [], 0.2)

            # Check for user commands
            if sys.stdin in ready:
                command = sys.stdin.readline().strip().lower()

                if command == cmd_personal:
                    if routing.active_target() != "P":
                        (
                            state.left_button_down,
                            state.right_button_down,
                            state.middle_button_down,
                        ) = release_inputs_for_switch(
                            routing.active_target(), personal_serial, work_serial,
                            state.left_button_down, state.right_button_down, state.middle_button_down,
                            state.pressed_keys,
                        )
                    routing.perform_manual_switch("P")
                    last_target_announce_time = routing.last_switch_time()

                elif command == cmd_work:
                    if routing.active_target() != "W":
                        (
                            state.left_button_down,
                            state.right_button_down,
                            state.middle_button_down,
                        ) = release_inputs_for_switch(
                            routing.active_target(), personal_serial, work_serial,
                            state.left_button_down, state.right_button_down, state.middle_button_down,
                            state.pressed_keys,
                        )
                    routing.perform_manual_switch("W")
                    last_target_announce_time = routing.last_switch_time()

                elif command == cmd_clipboard:
                    write_line(routing.active_serial(), "CLIPBOARD REQUEST")
                    print(f"clipboard request sent to: {routing.active_target()}")

                elif command == cmd_quit:
                    running = False
                    continue

            target_serial = routing.active_serial()

            # Handle input events from every ready device. Each fd has
            # its events drained ONCE per iteration -- we then route each
            # event by its type within this single block. The mouse-side
            # handlers (REL_*, BTN_LEFT/RIGHT/MIDDLE, SYN) and the
            # keyboard-side handler (KEY_* that is not BTN_*) consume
            # disjoint events, so a single fd whose path appears in both
            # role lists (e.g., a Logitech K400 combo receiver) just
            # works -- each event lands in the right place by type.
            for dev in open_devices.values():
                if dev.fd not in ready:
                    continue
                for event in dev.read():
                    # SYN_REPORT: end of a logical input event. Flush
                    # accumulated motion via routing (which may trigger
                    # an auto edge-switch) and then any wheel deltas to
                    # the active target. Inline here -- not in
                    # input_events.process_event -- because the flush
                    # has to coordinate with routing and main's
                    # last_target_announce_time.
                    if event.type == ecodes.EV_SYN and event.code == ecodes.SYN_REPORT:
                        if state.mouse_dx != 0 or state.mouse_dy != 0:
                            (
                                switched,
                                state.left_button_down,
                                state.right_button_down,
                                state.middle_button_down,
                            ) = routing.handle_mouse_motion(
                                state.mouse_dx, state.mouse_dy,
                                state.left_button_down,
                                state.right_button_down,
                                state.middle_button_down,
                                state.pressed_keys,
                            )
                            state.mouse_dx = 0
                            state.mouse_dy = 0
                            if switched:
                                # Auto edge-switch fired. Re-arm the announce
                                # timer to broadcast TARGET immediately, and
                                # drop any wheel events that were in the same
                                # SYN cycle -- they belong to the old target.
                                last_target_announce_time = routing.last_switch_time()
                                continue

                        if state.mouse_wheel != 0:
                            write_line(routing.active_serial(), f"MOUSE WHEEL DELTA={state.mouse_wheel}")
                            state.mouse_wheel = 0

                        if state.mouse_hwheel != 0:
                            write_line(routing.active_serial(), f"MOUSE HWHEEL DELTA={state.mouse_hwheel}")
                            state.mouse_hwheel = 0

                        continue

                    # Everything else (EV_REL accumulation, mouse
                    # buttons with DOWN/UP dedup, keyboard fan-out)
                    # lives in input_events.process_event. State
                    # mutations land in `state`; wire writes go to
                    # target_serial directly.
                    process_event(event, state, target_serial)

    except KeyboardInterrupt:
        pass
    finally:
        try:
            release_all_inputs(
                routing.active_serial(),
                state.left_button_down,
                state.right_button_down,
                state.middle_button_down,
                state.pressed_keys,
            )
        except Exception as ex:
            print(f"release_all_inputs error: {ex}")

        state.left_button_down = False
        state.right_button_down = False
        state.middle_button_down = False
        state.pressed_keys.clear()

        stop_event.set()
        personal_serial.close()
        work_serial.close()
        for dev in open_devices.values():
            try:
                dev.close()
            except Exception:
                pass
        print("Janus.InputRouter stopping.")

    return 0