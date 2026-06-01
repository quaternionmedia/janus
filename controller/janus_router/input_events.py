"""Input-event handlers for the Janus controller.

These functions consume evdev events read off the local input devices
(mouse / keyboard) and forward the meaningful ones to the active target
agent over its serial port. Per-event dispatch (EV_REL + EV_KEY) lives
here behind `process_event`; SYN_REPORT flushing stays in main because
it has to coordinate with routing's auto-edge-switch logic.

  * `log_unhandled_*`        — observability for events we don't forward
  * `release_all_inputs`     — emit UP for every currently-held button/key
  * `release_inputs_for_switch` — release held inputs on the OLD target
                                  before flipping; prevents Windows
                                  auto-repeat from spamming SWITCH PEER
                                  after a held-key switch trigger
  * `handle_keyboard_event`  — normalize an EV_KEY event into a wire
                               KEY NAME=... STATE=... line
  * `InputState`             — dataclass grouping per-tick mutable state
                               (REL accumulators + button-down bools +
                               pressed_keys), held for the lifetime of
                               the main event loop
  * `process_event`          — dispatcher for one EV_REL or EV_KEY event;
                               mutates InputState in place
"""
import serial

from dataclasses import dataclass, field

from evdev import ecodes

from janus_router.serial_io import write_line


_verbose = False


def set_verbose(value: bool) -> None:
    """Enable / disable per-event verbose logging for the input-event
    handlers. Currently affects mouse-button DOWN/UP printing only --
    handle_keyboard_event was never verbose."""
    global _verbose
    _verbose = bool(value)


def log_unhandled_mouse_key(event):
    code_name = ecodes.bytype.get(ecodes.EV_KEY, {}).get(event.code, f"UNKNOWN_{event.code}")
    print(f"UNHANDLED MOUSE KEY: code={event.code} name={code_name} value={event.value}")


def log_unhandled_mouse_rel(event):
    code_name = ecodes.bytype.get(ecodes.EV_REL, {}).get(event.code, f"UNKNOWN_{event.code}")
    print(f"UNHANDLED MOUSE REL: code={event.code} name={code_name} value={event.value}")


def log_unhandled_keyboard_key(event):
    key_name = ecodes.KEY.get(event.code, f"UNKNOWN_{event.code}")
    print(f"UNHANDLED KEYBOARD KEY: code={event.code} name={key_name} value={event.value}")


def release_all_inputs(
    active_serial: serial.Serial,
    left_button_down: bool,
    right_button_down: bool,
    middle_button_down: bool,
    pressed_keys: set[str],
) -> None:
    if left_button_down:
        write_line(active_serial, "MOUSE BUTTON LEFT=UP")
    if right_button_down:
        write_line(active_serial, "MOUSE BUTTON RIGHT=UP")
    if middle_button_down:
        write_line(active_serial, "MOUSE BUTTON MIDDLE=UP")

    for key_name in pressed_keys:
        write_line(active_serial, f"KEY NAME={key_name} STATE=UP")


def release_inputs_for_switch(
    old_active_target: str,
    personal_serial: serial.Serial,
    work_serial: serial.Serial,
    left_button_down: bool,
    right_button_down: bool,
    middle_button_down: bool,
    pressed_keys: set[str],
) -> tuple[bool, bool, bool]:
    """Release every held HID input on the OLD active target before a switch.

    Without this, pressing 's' (or any other key) in an agent's console to
    trigger a switch leaves that key held on the source Pico's HID -- so
    Windows auto-repeats it indefinitely, which can re-fire the switch
    trigger in a spam loop.

    Returns (False, False, False) so callers can reset the three mouse-
    button booleans inline. pressed_keys is cleared in place.

    Call BEFORE flipping active_target to its new value, so "old active
    target" is still resolvable to personal_serial vs work_serial.
    """
    old_serial = personal_serial if old_active_target == "P" else work_serial
    release_all_inputs(
        old_serial,
        left_button_down,
        right_button_down,
        middle_button_down,
        pressed_keys,
    )
    pressed_keys.clear()
    return False, False, False


def handle_keyboard_event(
    event,
    target_serial: serial.Serial,
    pressed_keys: set[str],
) -> bool:
    key_name = ecodes.KEY.get(event.code)

    if isinstance(key_name, (list, tuple)):
        resolved = None
        for n in key_name:
            if isinstance(n, str) and n.startswith("KEY_"):
                resolved = n
                break
        if resolved is None:
            log_unhandled_keyboard_key(event)
            return False
        key_name = resolved

    if not isinstance(key_name, str):
        log_unhandled_keyboard_key(event)
        return False

    if not key_name.startswith("KEY_"):
        log_unhandled_keyboard_key(event)
        return False

    if event.value == 1:
        state = "DOWN"
    elif event.value == 0:
        state = "UP"
    elif event.value == 2:
        state = "DOWN"
    else:
        log_unhandled_keyboard_key(event)
        return False

    if state == "DOWN":
        pressed_keys.add(key_name)
    elif state == "UP":
        pressed_keys.discard(key_name)

    write_line(target_serial, f"KEY NAME={key_name} STATE={state}")
    return True


@dataclass
class InputState:
    """Per-tick mutable state for the main event loop.

    The four mouse_* deltas accumulate across an evdev SYN cycle (one
    physical input event from the user generates multiple REL_*
    sub-events, terminated by SYN_REPORT). Main's SYN_REPORT branch
    flushes them via routing.handle_mouse_motion (motion) and direct
    write_line (wheels), then resets to zero.

    The three button-down bools dedupe wire-level DOWN/UP emissions:
    Linux EV_KEY for mouse buttons can repeat (value=2) and we never
    want to forward a redundant DOWN. They're also read by
    release_inputs_for_switch when flipping targets.

    pressed_keys is the same dedup idea for keyboard keys, populated
    inside handle_keyboard_event.

    A single InputState lives for the whole event loop -- created
    before the while loop, mutated in place by process_event and by
    the SYN_REPORT branch.
    """
    mouse_dx: int = 0
    mouse_dy: int = 0
    mouse_wheel: int = 0
    mouse_hwheel: int = 0
    left_button_down: bool = False
    right_button_down: bool = False
    middle_button_down: bool = False
    pressed_keys: set[str] = field(default_factory=set)


def process_event(event, state: InputState, target_serial: serial.Serial) -> None:
    """Dispatch one evdev event by type. EV_REL events update the
    motion / wheel accumulators on `state`; EV_KEY events translate to
    a wire MOUSE BUTTON or KEY message.

    EV_SYN/SYN_REPORT events are NOT handled here -- they belong in
    main's loop because the flush coordinates with routing's
    auto-edge-switch logic and the announce-timer reset on switch.
    """
    if event.type == ecodes.EV_REL:
        _accumulate_motion(event, state)
    elif event.type == ecodes.EV_KEY:
        _handle_key_event(event, state, target_serial)


def _accumulate_motion(event, state: InputState) -> None:
    code = event.code
    if code == ecodes.REL_X:
        state.mouse_dx += event.value
    elif code == ecodes.REL_Y:
        state.mouse_dy += event.value
    elif code == ecodes.REL_WHEEL:
        state.mouse_wheel += event.value
    elif code == ecodes.REL_HWHEEL:
        state.mouse_hwheel += event.value
    elif code in (ecodes.REL_WHEEL_HI_RES, ecodes.REL_HWHEEL_HI_RES):
        # Handled via REL_WHEEL / REL_HWHEEL above; using both
        # double-counts the same physical scroll click.
        pass
    else:
        log_unhandled_mouse_rel(event)


def _handle_key_event(event, state: InputState, target_serial: serial.Serial) -> None:
    """Mouse buttons are dispatched through `_emit_button` with DOWN/UP
    dedup. Anything else (any EV_KEY that isn't a BTN_LEFT/RIGHT/MIDDLE)
    is treated as a keyboard-shape event, regardless of which device
    emitted it -- this is how Razer Synapse-programmed mouse buttons
    that come over a separate "if02-event-kbd" node, or combo
    receivers like the Logitech K400 reporting keystrokes on the mouse
    node, get forwarded as keystrokes.
    """
    code = event.code
    if code == ecodes.BTN_LEFT:
        _emit_button(event, target_serial, state, "left_button_down", "LEFT")
    elif code == ecodes.BTN_RIGHT:
        _emit_button(event, target_serial, state, "right_button_down", "RIGHT")
    elif code == ecodes.BTN_MIDDLE:
        _emit_button(event, target_serial, state, "middle_button_down", "MIDDLE")
    else:
        if not handle_keyboard_event(event, target_serial, state.pressed_keys):
            log_unhandled_mouse_key(event)


def _emit_button(
    event,
    target_serial: serial.Serial,
    state: InputState,
    attr: str,
    wire_name: str,
) -> None:
    """Emit a wire MOUSE BUTTON message with DOWN/UP dedup.

    BTN auto-repeat (event.value == 2) is silently ignored -- mouse
    buttons don't repeat in any UI we care about, and Linux only emits
    value=2 for held keyboard keys anyway, though some kernels have
    been observed to emit it on long-held mouse buttons. Either way:
    ignore.
    """
    if event.value == 1:
        if not getattr(state, attr):
            setattr(state, attr, True)
            if _verbose:
                print(f"MOUSE BUTTON {wire_name}=DOWN")
            write_line(target_serial, f"MOUSE BUTTON {wire_name}=DOWN")
    elif event.value == 0:
        if getattr(state, attr):
            setattr(state, attr, False)
            if _verbose:
                print(f"MOUSE BUTTON {wire_name}=UP")
            write_line(target_serial, f"MOUSE BUTTON {wire_name}=UP")
    # event.value == 2: BTN auto-repeat; ignore.