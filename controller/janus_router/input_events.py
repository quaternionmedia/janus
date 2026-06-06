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
                               pressed_keys + suppressed_keys), held for
                               the lifetime of the main event loop
  * `process_event`          — dispatcher for one EV_REL or EV_KEY event;
                               mutates InputState in place. Returns the
                               target letter ("P"/"W") when a force-
                               switch hotkey combo is detected so the
                               caller can perform_manual_switch.
  * `set_force_switch_combos` — register Pi-side hotkey combos that
                                bypass the active target and switch
                                routing directly. Trigger keys are
                                suppressed (never forwarded).
"""
import serial

from dataclasses import dataclass, field

from evdev import ecodes

from janus_router.serial_io import write_line


_verbose = False

# Pi-side force-switch hotkey combos. Maps each TRIGGER key (e.g.
# "KEY_P") to a (required_modifiers, target_letter) tuple. When
# process_event sees a KEY DOWN whose name matches a configured
# trigger AND state.pressed_keys is a superset of required_modifiers,
# the event is suppressed (not forwarded) and the target letter is
# bubbled up to main so it can call routing.perform_manual_switch.
#
# Populated by set_force_switch_combos() at startup. Empty by default
# means "no Pi-side force-switch hotkeys configured."
_force_switch_combos: dict[str, tuple[frozenset[str], str]] = {}


def set_verbose(value: bool) -> None:
    """Enable / disable per-event verbose logging for the input-event
    handlers. Currently affects mouse-button DOWN/UP printing only --
    handle_keyboard_event was never verbose."""
    global _verbose
    _verbose = bool(value)


def set_force_switch_combos(
    combos: dict[str, tuple[frozenset[str], str]],
) -> None:
    """Configure Pi-side force-switch hotkey combos. Each entry maps a
    trigger key name (e.g. "KEY_P") to a tuple of:
      * required modifiers as a frozenset of key names (e.g.
        frozenset({"KEY_RIGHTCTRL", "KEY_RIGHTALT"}))
      * target letter ("P" or "W")

    When process_event sees the trigger key's initial DOWN with every
    required modifier currently held, it suppresses the event AND
    returns the target letter so main can perform the switch.
    Subsequent repeat/UP events for the same trigger are also
    suppressed (tracked via InputState.suppressed_keys) so auto-repeat
    after the switch doesn't spam the new target with the trigger key.

    Pass an empty dict to disable force-switch entirely.
    """
    global _force_switch_combos
    _force_switch_combos = dict(combos)


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

    suppressed_keys tracks keys whose initial DOWN was consumed by a
    Pi-side force-switch hotkey. While a key is in this set, its
    auto-repeat (value=2) and final UP (value=0) are also suppressed
    so the trigger keystroke (e.g. KEY_P / KEY_W) never reaches any
    agent. Names enter the set inside _check_force_switch on combo
    match, leave it when the UP arrives.

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
    suppressed_keys: set[str] = field(default_factory=set)


def process_event(
    event,
    state: InputState,
    target_serial: serial.Serial,
) -> str | None:
    """Dispatch one evdev event by type. EV_REL events update the
    motion / wheel accumulators on `state`; EV_KEY events translate to
    a wire MOUSE BUTTON or KEY message.

    Returns the target letter ("P" or "W") when a Pi-side force-switch
    hotkey combo (e.g. Right Ctrl + Right Alt + P) was detected on the
    initial DOWN of the trigger key. The trigger event is suppressed
    (not forwarded) and the caller must invoke
    routing.perform_manual_switch with the returned target. Returns
    None for all other events, including suppressed repeat/UP events
    of a previously-triggered combo.

    EV_SYN/SYN_REPORT events are NOT handled here -- they belong in
    main's loop because the flush coordinates with routing's
    auto-edge-switch logic and the announce-timer reset on switch.
    """
    if event.type == ecodes.EV_REL:
        _accumulate_motion(event, state)
        return None
    elif event.type == ecodes.EV_KEY:
        return _handle_key_event(event, state, target_serial)
    return None


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


def _handle_key_event(
    event,
    state: InputState,
    target_serial: serial.Serial,
) -> str | None:
    """Mouse buttons are dispatched through `_emit_button` with DOWN/UP
    dedup. Anything else (any EV_KEY that isn't a BTN_LEFT/RIGHT/MIDDLE)
    is treated as a keyboard-shape event, regardless of which device
    emitted it -- this is how Razer Synapse-programmed mouse buttons
    that come over a separate "if02-event-kbd" node, or combo
    receivers like the Logitech K400 reporting keystrokes on the mouse
    node, get forwarded as keystrokes.

    Returns the force-switch target letter when a configured combo
    triggers on this event; None otherwise.
    """
    code = event.code
    if code == ecodes.BTN_LEFT:
        _emit_button(event, target_serial, state, "left_button_down", "LEFT")
        return None
    elif code == ecodes.BTN_RIGHT:
        _emit_button(event, target_serial, state, "right_button_down", "RIGHT")
        return None
    elif code == ecodes.BTN_MIDDLE:
        _emit_button(event, target_serial, state, "middle_button_down", "MIDDLE")
        return None

    # Keyboard-shape event. Check force-switch hotkey first. If the
    # event is part of a combo (trigger DOWN, or repeat/UP of a
    # previously-triggered key), suppress it -- don't forward to any
    # agent. Only the initial DOWN bubbles a non-None target up to
    # main; suppressed repeat/UP events return None but the suppress
    # flag still keeps them from reaching handle_keyboard_event.
    suppress, switch_target = _check_force_switch(event, state)
    if suppress:
        return switch_target

    if not handle_keyboard_event(event, target_serial, state.pressed_keys):
        log_unhandled_mouse_key(event)
    return None


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


def _check_force_switch(
    event,
    state: InputState,
) -> tuple[bool, str | None]:
    """Determine whether `event` is part of a Pi-side force-switch
    hotkey combo and should be suppressed from forwarding.

    Returns (suppress, switch_target):
      * (False, None)         -- not a combo event; caller forwards
                                  normally via handle_keyboard_event
      * (True,  "P" | "W")    -- initial DOWN of a trigger key with
                                  all required modifiers currently
                                  held; suppress the event AND
                                  perform the switch
      * (True,  None)         -- repeat (value=2) or UP (value=0) of
                                  a previously-triggered key; suppress
                                  only. UP also removes the key from
                                  state.suppressed_keys.

    The trigger key is NEVER added to state.pressed_keys -- it's
    intercepted before handle_keyboard_event sees it.
    """
    # Skip non-trigger combos entirely if none are configured.
    if not _force_switch_combos and not state.suppressed_keys:
        return (False, None)

    # Resolve the key name (matches handle_keyboard_event's logic for
    # multi-name codes). If the name can't be resolved or doesn't start
    # with KEY_, this isn't a candidate for force-switch handling.
    key_name = ecodes.KEY.get(event.code)
    if isinstance(key_name, (list, tuple)):
        resolved = None
        for n in key_name:
            if isinstance(n, str) and n.startswith("KEY_"):
                resolved = n
                break
        key_name = resolved
    if not isinstance(key_name, str) or not key_name.startswith("KEY_"):
        return (False, None)

    if event.value == 1:
        # Initial DOWN. Check whether this key is a configured trigger
        # AND every required modifier is currently held.
        combo = _force_switch_combos.get(key_name)
        if combo is None:
            return (False, None)
        required_modifiers, target = combo
        if not required_modifiers.issubset(state.pressed_keys):
            return (False, None)
        # Match: mark this key as suppressed so its repeat and UP
        # events are also dropped.
        state.suppressed_keys.add(key_name)
        return (True, target)

    if event.value == 2:
        # Auto-repeat. Suppress if this key was the trigger of an
        # active force-switch.
        if key_name in state.suppressed_keys:
            return (True, None)
        return (False, None)

    if event.value == 0:
        # UP. If previously suppressed, drop the UP and forget the
        # key. Otherwise pass through to normal handling.
        if key_name in state.suppressed_keys:
            state.suppressed_keys.discard(key_name)
            return (True, None)
        return (False, None)

    return (False, None)