"""Routing state and edge-detection logic for the Janus controller.

Owns the "which side is currently receiving input" state machine. Four
ways to switch:

  * Manual: stdin 'p' / 'w' in the Pi console, or a remote SWITCH PEER
    request from an agent. Both go through `perform_manual_switch`,
    which lands the cursor at the target's last-known position.

  * Auto edge-switch: mouse cursor pushes against the configured edge
    of the current target's monitor. After accumulating
    `switch_push_pixels` of push, `handle_mouse_motion` flips the
    active target and lands the cursor near the destination edge with
    the source X offset preserved.

  * Pi-side force-switch hotkey (Right Ctrl + Right Alt + P/W,
    detected in input_events): caller invokes `perform_manual_switch`
    directly with a (FORCE Right Ctrl+Right Alt) log suffix.

  * Dead-peer auto-switch: when the active target's agent has gone
    silent past `dead_peer_threshold_seconds` AND the active is NOT
    the configured `dead_peer_home_base` AND the home base itself is
    alive, `check_dead_peer_switch` returns the home base so main can
    flip routing there. Heartbeats are any inbound line from the agent
    (TARGET echoes every ~2s, CURSOR keepalives, DISPLAY refreshes).
    Refreshed via `record_message_from`, called from inbound.py's
    `handle_serial_line` on every line received.

    The home_base asymmetry is intentional: if you're on home base
    and home base dies, "switching to the other side" doesn't help --
    home base dying is a "fix home base" event, not a "use the other
    side" event. Dead-peer auto-switch is a one-way fallback toward
    home base only. The other switch paths (manual, force, edge-auto)
    are unaffected by home_base and can still move you off home base
    normally.

    Covers failure modes that bypass the agent's OnShutdown signal:
    BSOD / kernel panic, hard power loss, USB disconnect between
    Pico and host, agent process force-killed without the OS getting
    a SessionEnding notification. After firing, the next auto-switch
    is locked out for `dead_peer_switch_cooldown_seconds` to prevent
    rapid flipping.

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

# Dead-peer tracking. _last_seen records the monotonic time we last
# received ANY line from each agent. record_message_from() updates
# these; check_dead_peer_switch() reads them. Default 0.0 means "never
# seen" -- at startup, init_dead_peer_tracking() sets them to
# (now + grace) so the detector doesn't fire before agents have had
# a chance to connect.
_last_seen: dict[str, float] = {"P": 0.0, "W": 0.0}
_last_dead_peer_switch_time: float = 0.0


# ---- Config (populated by configure()) --------------------------------------

_auto_edge_switch_enabled: bool = True
_edge_arm_pixels: int = 2
_switch_push_pixels: int = 12
_switch_entry_margin_y: int = 32
_switch_cooldown_seconds: float = 0.15
_dead_peer_threshold_seconds: float = 10.0
_dead_peer_switch_cooldown_seconds: float = 30.0

# Home-base side for dead-peer auto-switch. Asymmetric by design:
# auto-switch never moves AWAY from home base. None disables dead-peer
# auto-switch entirely. Validated to "P" / "W" / None in configure();
# other values warn and disable.
_dead_peer_home_base: str | None = "P"
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
    global _dead_peer_threshold_seconds, _dead_peer_switch_cooldown_seconds
    global _dead_peer_home_base
    _auto_edge_switch_enabled = bool(switching_config["auto_edge_switch_enabled"])
    _edge_arm_pixels = switching_config["edge_arm_pixels"]
    _switch_push_pixels = switching_config["switch_push_pixels"]
    _switch_entry_margin_y = switching_config["switch_entry_margin_y"]
    _switch_cooldown_seconds = switching_config["switch_cooldown_seconds"]
    _dead_peer_threshold_seconds = switching_config["dead_peer_threshold_seconds"]
    _dead_peer_switch_cooldown_seconds = switching_config[
        "dead_peer_switch_cooldown_seconds"
    ]

    # Validate dead_peer_home_base. Accept "P", "W", or None; reject
    # anything else with a loud warning and disable the feature so the
    # controller doesn't silently behave unexpectedly.
    raw = switching_config.get("dead_peer_home_base")
    if raw in ("P", "W"):
        _dead_peer_home_base = raw
    elif raw is None or raw == "":
        _dead_peer_home_base = None
    else:
        print(
            f"warning: switching.dead_peer_home_base must be 'P', 'W', or null "
            f"(got {raw!r}); disabling dead-peer auto-switch."
        )
        _dead_peer_home_base = None


def init_dead_peer_tracking(grace_seconds: float = 30.0) -> None:
    """Seed the last-seen timestamps with (now + grace_seconds) so the
    dead-peer detector treats both peers as alive for the first
    `grace_seconds` even if no agent messages have arrived yet.

    Without this, the module-level default of 0.0 combined with a
    typical CLOCK_MONOTONIC value in the millions makes both peers
    look "dead since the dawn of time" at startup. The grace window
    gives the agents time to connect and emit their first DISPLAY /
    CURSOR / TARGET echo before the detector starts judging silence.

    Call once at startup, after routing.init() and routing.configure().
    """
    future = time.monotonic() + grace_seconds
    _last_seen["P"] = future
    _last_seen["W"] = future


def record_message_from(source: str) -> None:
    """Refresh the last-seen timestamp for an agent. Called from
    inbound.handle_serial_line on EVERY line that arrived from an
    agent, including lines we don't recognize -- any byte stream
    coming through proves the agent process is alive."""
    if source in _last_seen:
        _last_seen[source] = time.monotonic()


def check_dead_peer_switch() -> str | None:
    """Decide whether to auto-switch routing because the active peer
    has gone silent. Returns the home-base letter ("P" or "W") if main
    should switch there; None otherwise.

    Behavior is ASYMMETRIC by design. The rule is:

      "If the active target dies AND active is not the home base AND
       the home base is itself alive, fall back to home base."

    Practical reading with home_base = "P":
      * On Work, Work dies, Personal alive -> auto-switch to Personal.
      * On Personal, Personal dies, Work alive -> do NOTHING. Personal
        dying is a "fix Personal" event, not "use Work." Stay put.
      * On Work, Work dies, Personal also dead -> nothing useful to do.
      * On Personal, Personal is alive -> active is alive, no action.

    Important scope: ONLY this function applies the home_base rule.
    Manual switches (stdin p/w, agent SWITCH PEER, Pi-side force-switch
    hotkey) and auto-edge-switch are unaffected -- the user can move
    OFF home base by any normal means; this just won't auto-bounce
    them off it when home base dies.

    Returns None if:
      * dead-peer auto-switch is disabled (home_base is None)
      * active IS home_base (asymmetric guard: never auto-switch away)
      * active has been seen within the threshold window
      * home_base itself is dead (no live destination)
      * we're inside the dead-peer-switch cooldown window

    Mutates `_last_dead_peer_switch_time` when returning a target so
    the cooldown begins immediately.
    """
    global _last_dead_peer_switch_time

    if _dead_peer_home_base is None:
        return None

    now = time.monotonic()

    if now - _last_dead_peer_switch_time < _dead_peer_switch_cooldown_seconds:
        return None

    active = _active_target

    # Asymmetric guard: never auto-switch AWAY from home base. If you're
    # on home base and it dies, the dead peer IS the home base and
    # there's nowhere safer to fall back to -- bouncing to the other
    # side wouldn't help. The user fixes home base manually.
    if active == _dead_peer_home_base:
        return None

    home = _dead_peer_home_base
    active_age = now - _last_seen.get(active, 0.0)
    home_age = now - _last_seen.get(home, 0.0)

    if active_age <= _dead_peer_threshold_seconds:
        return None  # active is alive; nothing to do

    if home_age > _dead_peer_threshold_seconds:
        # Home base also dead. Nowhere safe to fall back to.
        return None

    # Diagnostic. Lands once per auto-switch decision; the cooldown
    # ensures we don't spam the log.
    print(
        f"dead peer detected: active={active} silent for {active_age:.1f}s "
        f"(threshold {_dead_peer_threshold_seconds:.0f}s); "
        f"home_base={home} alive ({home_age:.1f}s since last message); "
        f"auto-switching to {home}"
    )
    _last_dead_peer_switch_time = now
    return home


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

    Used by manual triggers: stdin 'p' / 'w' in the controller, the
    "SWITCH PEER" message from an agent, the Pi-side force-switch
    hotkey, and dead-peer auto-switch. The auto edge-switch path uses
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