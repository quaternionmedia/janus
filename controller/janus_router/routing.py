"""Routing state and edge-detection logic for the Janus controller.

Owns the "which side is currently receiving input" state machine. Three
ways to switch:

  * Manual: stdin 'p' / 'w' in the Pi console, or a remote SWITCH PEER
    request from an agent. Both go through `perform_manual_switch`,
    which lands the cursor at the target's last-known position.

  * Auto edge-switch: mouse cursor pushes against the configured edge
    of the current target's monitor. After accumulating
    `switch_push_pixels` of push, `handle_mouse_motion` flips the
    active target and lands the cursor near the destination edge with
    the source X offset preserved.

State exposed to inbound.py for direct mutation:
  * personal_cursor / work_cursor: last-known cursor per side
  * personal_display / work_display: monitor geometry per side

All cursor / display values are in ABSOLUTE virtual-screen coordinates,
matching the frame the agent reports via CURSOR and DISPLAY lines.
Never mix with per-monitor local coordinates.

The `_verbose` flag is set by `set_verbose()` at startup from
main.py's distribution of the config-level verbose setting; used
only by `handle_mouse_motion` for per-event MOUSE MOVE logging.
"""
import time

import serial

from janus_router.input_events import release_inputs_for_switch
from janus_router.serial_io import write_line


# ---- Exposed state (inbound mutates these dicts directly) -------------------

personal_cursor: dict[str, int] = {"X": 0, "Y": 0}
work_cursor: dict[str, int] = {"X": 0, "Y": 0}
personal_display: dict[str, int] = {"L": 0, "T": 0, "W": 0, "H": 0}
work_display: dict[str, int] = {"L": 0, "T": 0, "W": 0, "H": 0}


# ---- Private state ----------------------------------------------------------

_active_target: str = "P"
_personal_serial: serial.Serial | None = None
_work_serial: serial.Serial | None = None

_p_to_w_push: int = 0
_w_to_p_push: int = 0
_last_switch_time: float = 0.0


# ---- Config (populated by configure()) --------------------------------------

_auto_edge_switch_enabled: bool = True
_edge_arm_pixels: int = 2
_switch_push_pixels: int = 12
_switch_entry_margin_y: int = 32
_switch_cooldown_seconds: float = 0.15
_verbose: bool = False


def init(personal_serial: serial.Serial, work_serial: serial.Serial) -> None:
    """Stash the two serial ports. Must be called before the main loop
    drives any routing actions."""
    global _personal_serial, _work_serial
    _personal_serial = personal_serial
    _work_serial = work_serial


def configure(switching_config: dict) -> None:
    """Apply the `switching:` section of the loaded config. Call once
    after load_config()."""
    global _auto_edge_switch_enabled, _edge_arm_pixels, _switch_push_pixels
    global _switch_entry_margin_y, _switch_cooldown_seconds
    _auto_edge_switch_enabled = bool(switching_config["auto_edge_switch_enabled"])
    _edge_arm_pixels = switching_config["edge_arm_pixels"]
    _switch_push_pixels = switching_config["switch_push_pixels"]
    _switch_entry_margin_y = switching_config["switch_entry_margin_y"]
    _switch_cooldown_seconds = switching_config["switch_cooldown_seconds"]


def set_verbose(value: bool) -> None:
    """Enable / disable per-event verbose logging for the routing path."""
    global _verbose
    _verbose = bool(value)


# ---- Accessors --------------------------------------------------------------

def active_target() -> str:
    return _active_target


def active_serial() -> serial.Serial:
    return _personal_serial if _active_target == "P" else _work_serial


def last_switch_time() -> float:
    return _last_switch_time


def auto_edge_switch_enabled() -> bool:
    return _auto_edge_switch_enabled


def cooling_down() -> bool:
    """True if we're inside the post-switch cooldown window."""
    return time.monotonic() - _last_switch_time < _switch_cooldown_seconds


# ---- Pure utilities (used by perform_manual_switch + handle_mouse_motion) ---

def clamp(value: int, minimum: int, maximum: int) -> int:
    return max(minimum, min(value, maximum))


def send_cursor_set(ser: serial.Serial, x: int, y: int) -> None:
    write_line(ser, f"CURSOR SET X={x} Y={y}")


# ---- Switch actions ---------------------------------------------------------

def perform_manual_switch(target: str, log_suffix: str = "") -> float:
    """Switch the active target to P or W using the target's last known
    cursor position. Returns the new last_switch_time.

    Used by manual triggers: stdin 'p' / 'w' in the controller, and the
    "SWITCH PEER" message from an agent. The auto edge-switch path uses
    a different entry-point computation (preserve x offset, land near
    the destination edge) and is NOT routed through here.

    Mutates the target cursor dict in place (records the entry point that
    was actually sent to the agent), and emits the broadcast. Caller is
    responsible for releasing held inputs on the OLD target before
    calling this -- see release_inputs_for_switch in input_events.
    """
    global _active_target, _p_to_w_push, _w_to_p_push, _last_switch_time

    if target == "P":
        display = personal_display
        cursor = personal_cursor
        switch_serial = _personal_serial
        label = "PERSONAL"
    elif target == "W":
        display = work_display
        cursor = work_cursor
        switch_serial = _work_serial
        label = "WORK"
    else:
        raise ValueError(f"target must be 'P' or 'W' (got {target!r})")

    if display["W"] > 0 and display["H"] > 0:
        entry_x = clamp(
            cursor["X"],
            display["L"],
            display["L"] + display["W"] - 1,
        )
        entry_y = clamp(
            cursor["Y"],
            display["T"],
            display["T"] + display["H"] - 1,
        )
    else:
        entry_x = 0
        entry_y = 0

    write_line(_personal_serial, f"TARGET {target}")
    write_line(_work_serial, f"TARGET {target}")
    send_cursor_set(switch_serial, entry_x, entry_y)

    cursor["X"] = entry_x
    cursor["Y"] = entry_y

    _active_target = target
    _p_to_w_push = 0
    _w_to_p_push = 0
    _last_switch_time = time.monotonic()

    full_label = f"{label} {log_suffix}".strip()
    print(f"\n=== ACTIVE: {full_label} ===\n")
    return _last_switch_time


def handle_mouse_motion(
    mouse_dx: int,
    mouse_dy: int,
    left_button_down: bool,
    right_button_down: bool,
    middle_button_down: bool,
    pressed_keys: set[str],
) -> tuple[bool, bool, bool, bool]:
    """Process accumulated mouse motion. Updates the active side's virtual
    cursor, runs edge detection, performs an auto-switch if armed,
    otherwise emits MOUSE MOVE to the active target.

    Returns (switched, left_button_down, right_button_down,
    middle_button_down). On switch, held mouse buttons are released on
    the OLD target before the flip; the returned bools are False so the
    caller can replace its own state. On no-switch, the bools are
    returned unchanged so the call site can use the same destructuring
    pattern in either case.
    """
    global _active_target, _p_to_w_push, _w_to_p_push, _last_switch_time

    cool = cooling_down()

    if _active_target == "P":
        # Everything below is in absolute virtual-screen coordinates.
        p_l = personal_display["L"]
        p_t = personal_display["T"]
        p_w = personal_display["W"]
        p_h = personal_display["H"]

        if p_w > 0 and p_h > 0:
            personal_cursor["X"] = clamp(
                personal_cursor["X"] + mouse_dx,
                p_l,
                p_l + p_w - 1,
            )
            personal_cursor["Y"] = clamp(
                personal_cursor["Y"] + mouse_dy,
                p_t,
                p_t + p_h - 1,
            )

        cursor_x = personal_cursor["X"]
        cursor_y = personal_cursor["Y"]

        # Edge arming + auto-switch are GATED on the config flag. When
        # disabled, the cursor still tracks (needed for manual switch
        # entry-point preservation), but no automatic edge switching
        # occurs.
        if _auto_edge_switch_enabled:
            # Arm on Personal's bottom edge. Suppress arming entirely
            # during the post-switch cooldown.
            #
            # Reset only when the user either leaves the edge zone or
            # actively pushes back (mouse_dy < 0). A SYN cycle with
            # horizontal jitter (mouse_dy == 0) must NOT reset the
            # counter; otherwise slow diagonal motion never reaches
            # threshold.
            if cool:
                _p_to_w_push = 0
            elif p_w > 0 and p_h > 0:
                bottom_arm = p_t + p_h - 1 - _edge_arm_pixels
                was_armed = _p_to_w_push > 0
                in_zone = cursor_y >= bottom_arm

                if not in_zone or mouse_dy < 0:
                    _p_to_w_push = 0
                elif mouse_dy > 0:
                    _p_to_w_push += mouse_dy
                # mouse_dy == 0: keep current push as-is

                # Only log the transition into armed state, not every
                # event that keeps it armed.
                if not was_armed and _p_to_w_push > 0:
                    print(
                        f"AUTO P->W arming cursor_y={cursor_y} "
                        f"threshold={bottom_arm}"
                    )

            if (_p_to_w_push >= _switch_push_pixels
                    and work_display["W"] > 0 and work_display["H"] > 0):
                left_button_down, right_button_down, middle_button_down = release_inputs_for_switch(
                    _active_target, _personal_serial, _work_serial,
                    left_button_down, right_button_down, middle_button_down,
                    pressed_keys,
                )
                _active_target = "W"
                _p_to_w_push = 0
                _w_to_p_push = 0

                w_l = work_display["L"]
                w_t = work_display["T"]
                w_w = work_display["W"]

                # Preserve X offset from the source monitor's left edge;
                # clamp into the target monitor's horizontal bounds.
                source_x_offset = cursor_x - p_l
                entry_x = clamp(
                    w_l + source_x_offset,
                    w_l,
                    w_l + w_w - 1,
                )
                entry_y = w_t + _switch_entry_margin_y

                write_line(_personal_serial, "TARGET W")
                write_line(_work_serial, "TARGET W")
                send_cursor_set(_work_serial, entry_x, entry_y)

                work_cursor["X"] = entry_x
                work_cursor["Y"] = entry_y

                _last_switch_time = time.monotonic()
                print("\n=== ACTIVE: WORK (AUTO) ===\n")
                return (True, left_button_down, right_button_down, middle_button_down)

    elif _active_target == "W":
        w_l = work_display["L"]
        w_t = work_display["T"]
        w_w = work_display["W"]
        w_h = work_display["H"]

        if w_w > 0 and w_h > 0:
            work_cursor["X"] = clamp(
                work_cursor["X"] + mouse_dx,
                w_l,
                w_l + w_w - 1,
            )
            work_cursor["Y"] = clamp(
                work_cursor["Y"] + mouse_dy,
                w_t,
                w_t + w_h - 1,
            )

        cursor_x = work_cursor["X"]
        cursor_y = work_cursor["Y"]

        if _auto_edge_switch_enabled:
            # Arm on Work's top edge. Same reset discipline as P->W:
            # only reset when leaving the edge zone or pushing back
            # downward. A SYN cycle with x-only motion (mouse_dy == 0)
            # must preserve the counter.
            if cool:
                _w_to_p_push = 0
            elif w_w > 0 and w_h > 0:
                top_arm = w_t + _edge_arm_pixels
                was_armed = _w_to_p_push > 0
                in_zone = cursor_y <= top_arm

                if not in_zone or mouse_dy > 0:
                    _w_to_p_push = 0
                elif mouse_dy < 0:
                    _w_to_p_push += -mouse_dy
                # mouse_dy == 0: keep current push as-is

                if not was_armed and _w_to_p_push > 0:
                    print(
                        f"AUTO W->P arming cursor_y={cursor_y} "
                        f"threshold={top_arm}"
                    )

            if (_w_to_p_push >= _switch_push_pixels
                    and personal_display["W"] > 0 and personal_display["H"] > 0):
                left_button_down, right_button_down, middle_button_down = release_inputs_for_switch(
                    _active_target, _personal_serial, _work_serial,
                    left_button_down, right_button_down, middle_button_down,
                    pressed_keys,
                )
                _active_target = "P"
                _p_to_w_push = 0
                _w_to_p_push = 0

                p_l = personal_display["L"]
                p_t = personal_display["T"]
                p_w = personal_display["W"]
                p_h = personal_display["H"]

                source_x_offset = cursor_x - w_l
                entry_x = clamp(
                    p_l + source_x_offset,
                    p_l,
                    p_l + p_w - 1,
                )
                entry_y = p_t + p_h - 1 - _switch_entry_margin_y

                write_line(_personal_serial, "TARGET P")
                write_line(_work_serial, "TARGET P")
                send_cursor_set(_personal_serial, entry_x, entry_y)

                personal_cursor["X"] = entry_x
                personal_cursor["Y"] = entry_y

                _last_switch_time = time.monotonic()
                print("\n=== ACTIVE: PERSONAL (AUTO) ===\n")
                return (True, left_button_down, right_button_down, middle_button_down)

    # No switch fired -- emit MOUSE MOVE to the active target.
    target_serial = _personal_serial if _active_target == "P" else _work_serial
    if _verbose:
        print(f"MOUSE MOVE target={_active_target} dx={mouse_dx} dy={mouse_dy}")
    write_line(target_serial, f"MOUSE MOVE DX={mouse_dx} DY={mouse_dy}")
    return (False, left_button_down, right_button_down, middle_button_down)