"""Inbound wire-protocol handlers for the Janus controller.

These functions process lines received from the agents over UART. The
agents emit DISPLAY (monitor geometry), CURSOR (sync of current cursor
position), TARGET (echo of the active target -- ignored), CLIPBOARD
DATA / CLIPBOARD CLEAR (forwarded to the peer), and SWITCH PEER
(triggers a manual switch in the main loop).

`handle_serial_line` is the dispatcher; the others are its helpers.
State updates (cursor and display geometry) mutate the caller-owned
dicts in place.

The `_verbose` flag is set by `set_verbose()` at startup from
main.py's distribution of the config-level verbose setting; used
only by `handle_cursor_line` for per-event sync logging.
"""
import serial

from janus_router.serial_io import write_line


_verbose = False


def set_verbose(value: bool) -> None:
    """Enable / disable per-event verbose logging for the inbound path.
    Mirrors input_router.py's own `_verbose` flag; call once at startup."""
    global _verbose
    _verbose = bool(value)


def parse_keyed_int(token: str, expected_key: str) -> int | None:
    parts = token.split("=", 1)
    if len(parts) != 2:
        return None

    key, value = parts
    if key.upper() != expected_key.upper():
        return None

    try:
        return int(value)
    except ValueError:
        return None


def handle_display_line(
    line: str,
    personal_display: dict[str, int],
    work_display: dict[str, int],
) -> bool:
    parts = line.split()

    if len(parts) != 6 or parts[0] != "DISPLAY":
        return False

    device_id = parts[1]

    left = parse_keyed_int(parts[2], "L")
    top = parse_keyed_int(parts[3], "T")
    width = parse_keyed_int(parts[4], "W")
    height = parse_keyed_int(parts[5], "H")

    if None in (left, top, width, height):
        print(f"Ignoring invalid DISPLAY line: {line}")
        return True

    target = None
    if device_id == "P":
        target = personal_display
    elif device_id == "W":
        target = work_display
    else:
        print(f"Ignoring unknown display device id: {device_id}")
        return True

    changed = (
        target["L"] != left
        or target["T"] != top
        or target["W"] != width
        or target["H"] != height
    )

    target["L"] = left
    target["T"] = top
    target["W"] = width
    target["H"] = height

    if changed:
        print(
            "DISPLAY | "
            f'P: L={personal_display["L"]} T={personal_display["T"]} W={personal_display["W"]} H={personal_display["H"]} | '
            f'W: L={work_display["L"]} T={work_display["T"]} W={work_display["W"]} H={work_display["H"]}'
        )

    return True


def handle_cursor_line(
    line: str,
    personal_cursor: dict[str, int],
    work_cursor: dict[str, int],
) -> bool:
    parts = line.split()

    if len(parts) != 4 or parts[0] != "CURSOR":
        return False

    device_id = parts[1]

    x = parse_keyed_int(parts[2], "X")
    y = parse_keyed_int(parts[3], "Y")

    if None in (x, y):
        return True

    # CURSOR SYNC values from the agent are in absolute virtual-screen
    # coordinates, matching how we store personal_cursor / work_cursor.
    if device_id == "P":
        personal_cursor["X"] = x
        personal_cursor["Y"] = y
        if _verbose:
            print(f"CURSOR SYNC P X={x} Y={y}")
    elif device_id == "W":
        work_cursor["X"] = x
        work_cursor["Y"] = y
        if _verbose:
            print(f"CURSOR SYNC W X={x} Y={y}")

    return True


def handle_serial_line(
    source_name: str,
    line: str,
    personal_serial: serial.Serial,
    work_serial: serial.Serial,
    personal_display: dict[str, int],
    work_display: dict[str, int],
    personal_cursor: dict[str, int],
    work_cursor: dict[str, int],
) -> str | None:
    """Process one serial line from an agent.

    Returns "P" or "W" if the line was a SWITCH PEER request that should
    trigger a manual switch in the main loop; else None. Cursor/display
    state updates are applied here directly (mutate the dicts in place).
    """
    if handle_display_line(line, personal_display, work_display):
        return None

    if handle_cursor_line(line, personal_cursor, work_cursor):
        return None

    if line.startswith("TARGET "):
        return None

    if line == "SWITCH PEER":
        # source_name is "P" or "W"; switch to whichever side isn't the
        # source. The agent doesn't have to know its peer's id -- it just
        # asks for "the other one."
        target = "W" if source_name == "P" else "P"
        print(f"remote switch requested: {source_name} -> {target}")
        return target

    if line.startswith("CLIPBOARD DATA "):
        destination_serial = work_serial if source_name == "P" else personal_serial
        destination_name = "W" if source_name == "P" else "P"

        # Relay verbatim except for the verb rename. Do not parse the
        # payload on the router — base64 strings can be quite large and
        # parsing them here would just waste cycles.
        write_line(destination_serial, line.replace("CLIPBOARD DATA", "CLIPBOARD SET", 1))
        # Log byte count so it's easy to see whether truncation happened
        # anywhere downstream.
        print(f"clipboard forwarded: {source_name} -> {destination_name} ({len(line)} chars)")
        return None

    if line == "CLIPBOARD CLEAR":
        destination_serial = work_serial if source_name == "P" else personal_serial
        destination_name = "W" if source_name == "P" else "P"

        write_line(destination_serial, "CLIPBOARD CLEAR")
        print(f"clipboard CLEAR forwarded: {source_name} -> {destination_name}")
        return None

    print(f"unhandled serial line from {source_name}: {line}")
    return None