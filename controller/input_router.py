#!/usr/bin/env python3
import argparse
import os
import queue
import select
import signal
import sys
import termios
import threading
import time
from pathlib import Path

import serial
import yaml
from evdev import InputDevice, ecodes


# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------
#
# Settings come from config.yaml next to this script. Missing fields fall
# back to the defaults below (which match the historical hardcoded values).
# CLI flags can override individual fields at startup.

DEFAULT_CONFIG = {
    # Filename (in profiles/) of a YAML file whose `devices:` section
    # supplies all device paths. If missing/unreadable, we fall back to
    # the `devices:` block below with a loud warning. See config.yaml.
    "device_config_file": None,
    "devices": {
        # mouse / keyboard are LISTS of paths. The controller opens every
        # unique path once and dispatches each event by its TYPE: a fd's
        # REL_*, BTN_LEFT/RIGHT/MIDDLE, and SYN events go through the mouse
        # handler; its KEY_* (non-BTN) events go through the keyboard
        # handler. There's no per-fd role flag -- the same fd can yield
        # both kinds of events and they're routed by what they are, not by
        # which list its path appeared in.
        #
        # Implications:
        # * A combined device (e.g., Logitech K400 receiver, which reports
        #   mouse + keyboard on one node) is configured by listing the
        #   same path under both keys. The controller dedupes, opens it
        #   once, and both event kinds flow correctly.
        # * Listing additional interfaces (e.g., a Razer mouse's
        #   "if02-event-kbd" node where Synapse-mapped buttons emit
        #   keystrokes) under `keyboard` is how we capture programmable
        #   side-buttons that come over a separate evdev node.
        "mouse": ["/dev/input/event5"],
        "keyboard": ["/dev/input/event5"],
        "personal_uart": "/dev/ttyAMA0",
        "work_uart": "/dev/ttyAMA2",
    },
    "serial": {
        "baud": 921600,
    },
    "switching": {
        "edge_arm_pixels": 2,
        "switch_push_pixels": 12,
        "switch_entry_margin_y": 32,
        "switch_cooldown_seconds": 0.15,
        "target_announce_interval_seconds": 2.0,
    },
    "commands": {
        "personal": "p",
        "work": "w",
        "clipboard": "c",
        "quit": "q",
    },
    "logging": {
        "verbose": False,
    },
}


def _deep_merge(base: dict, override: dict) -> dict:
    """Recursively merge `override` into `base`, returning a new dict."""
    result = dict(base)
    for key, value in override.items():
        if (
            key in result
            and isinstance(result[key], dict)
            and isinstance(value, dict)
        ):
            result[key] = _deep_merge(result[key], value)
        else:
            result[key] = value
    return result


def load_config() -> dict:
    """Load config.yaml from this script's directory, layered over defaults.

    A missing file or missing fields fall through to DEFAULT_CONFIG, so
    the controller still starts with sensible behavior even on a fresh
    deployment.
    """
    config_path = Path(__file__).parent / "config.yaml"
    if not config_path.exists():
        print(f"config.yaml not found at {config_path}; using defaults.")
        return DEFAULT_CONFIG

    try:
        with open(config_path, "r", encoding="utf-8") as f:
            loaded = yaml.safe_load(f) or {}
        return _deep_merge(DEFAULT_CONFIG, loaded)
    except Exception as ex:
        print(f"Failed to load config.yaml: {ex}. Using defaults.")
        return DEFAULT_CONFIG


def load_device_config_file(config: dict) -> dict:
    """Apply the active device file's `devices:` section on top of config.

    Reads the filename from config["device_config_file"], opens
    profiles/<filename>, merges its `devices:` over the config-level
    `devices:`. The device file is authoritative for input/UART paths.

    If `device_config_file` is unset, returns config unchanged (the
    config-level `devices:` is used directly).

    If the file is missing, unreadable, or has no `devices:` section, logs
    a loud warning and returns config unchanged -- the controller will
    start with whatever `devices:` config.yaml has, which is most likely
    the built-in DEFAULT_CONFIG paths from input_router.py. Those are
    Logitech-K400-style defaults and probably DON'T match the actual
    hardware on this Pi, so check the log if input doesn't work.
    """
    filename = config.get("device_config_file")
    if not filename:
        return config

    device_path = Path(__file__).parent / "profiles" / filename

    def _warn(detail: str) -> None:
        bar = "!" * 70
        print(bar)
        print(f"!!! DEVICE CONFIG FAILED: {detail}")
        print(f"!!! file: {device_path}")
        print("!!! Falling back to built-in DEFAULT_CONFIG device paths,")
        print("!!! which probably don't match this Pi's hardware. Fix the")
        print("!!! device file or the device_config_file name in config.yaml.")
        print(bar)

    if not device_path.exists():
        _warn(f"file not found: {filename}")
        return config

    try:
        with open(device_path, "r", encoding="utf-8") as f:
            loaded = yaml.safe_load(f) or {}
    except Exception as ex:
        _warn(f"could not parse YAML: {ex}")
        return config

    if not isinstance(loaded, dict) or "devices" not in loaded:
        _warn(f"file has no `devices:` section: {filename}")
        return config

    merged = _deep_merge(config, {"devices": loaded["devices"]})
    print(f"loaded device config: {filename}")
    return merged


def _normalize_path_list(value, label: str) -> list[str]:
    """Coerce a `devices.mouse` / `devices.keyboard` config value to a list.

    Accepts either a single string (legacy single-device form) or a list
    of strings (new multi-device form). Always returns a fresh list.
    Strips empty / whitespace-only entries. Prints a warning and returns
    an empty list if the value is some unexpected shape -- the caller
    will treat empty as a config error.

    `label` is included in any warning so the operator knows which field
    was malformed.
    """
    if value is None:
        return []
    if isinstance(value, str):
        v = value.strip()
        return [v] if v else []
    if isinstance(value, (list, tuple)):
        out = []
        for entry in value:
            if isinstance(entry, str):
                entry = entry.strip()
                if entry:
                    out.append(entry)
            else:
                print(
                    f"warning: {label} contains a non-string entry "
                    f"({entry!r}); skipping."
                )
        return out
    print(
        f"warning: {label} has unexpected type {type(value).__name__}; "
        "treating as empty."
    )
    return []


def validate_command_keys(commands: dict) -> None:
    """Sanity-check the command keys before the main loop accepts them.

    Each command must be exactly one character. All four must be unique.
    Raises ValueError with a clear message if either rule is violated.
    """
    required = ("personal", "work", "clipboard", "quit")
    keys = {}
    for name in required:
        value = commands.get(name)
        if not isinstance(value, str) or len(value) != 1:
            raise ValueError(
                f"commands.{name} must be a single character (got: {value!r})"
            )
        keys[name] = value.lower()

    if len(set(keys.values())) != len(keys):
        raise ValueError(
            f"command keys must be unique; got: {keys}"
        )


def parse_args(config: dict) -> dict:
    """Parse CLI overrides on top of an already-loaded config."""
    parser = argparse.ArgumentParser(
        description="Janus controller (mouse/keyboard router)."
    )
    parser.add_argument("--mouse", help="Path to the mouse evdev device.")
    parser.add_argument("--keyboard", help="Path to the keyboard evdev device.")
    parser.add_argument(
        "--personal-uart", help="UART path to the Personal bridge."
    )
    parser.add_argument(
        "--work-uart", help="UART path to the Work bridge."
    )
    parser.add_argument(
        "--baud", type=int, help="UART baud rate (must match agents)."
    )
    parser.add_argument(
        "--verbose",
        action="store_true",
        default=None,
        help="Enable verbose per-event logging.",
    )

    args = parser.parse_args()

    # Apply overrides only where the CLI supplied a value. Copy nested
    # dicts before mutating so we don't tamper with the loaded config or
    # the DEFAULT_CONFIG constant.
    result = dict(config)
    result["devices"] = dict(result["devices"])
    if args.mouse is not None:
        # CLI override replaces the whole list. Multi-device setups should
        # use the device file; the CLI flag is for one-off debugging.
        result["devices"]["mouse"] = [args.mouse]
    if args.keyboard is not None:
        result["devices"]["keyboard"] = [args.keyboard]
    if args.personal_uart is not None:
        result["devices"]["personal_uart"] = args.personal_uart
    if args.work_uart is not None:
        result["devices"]["work_uart"] = args.work_uart

    if args.baud is not None:
        result["serial"] = dict(result["serial"])
        result["serial"]["baud"] = args.baud

    if args.verbose is not None:
        result["logging"] = dict(result["logging"])
        result["logging"]["verbose"] = args.verbose

    return result


running = True

# Module-level verbose flag. Populated from config["logging"]["verbose"]
# during main() before anything else reads it. Module-level so the
# non-main helper functions (handle_cursor_line, etc.) can reference it
# without having to thread `verbose` through every signature.
_verbose = False


def handle_signal(signum, frame):
    global running
    running = False


def configure_uart(path: str) -> None:
    fd = os.open(path, os.O_RDWR | os.O_NOCTTY | os.O_NONBLOCK)
    try:
        attrs = termios.tcgetattr(fd)

        attrs[0] = 0
        attrs[1] = 0
        attrs[2] = attrs[2] | termios.CLOCAL | termios.CREAD
        attrs[2] = attrs[2] & ~termios.PARENB
        attrs[2] = attrs[2] & ~termios.CSTOPB
        attrs[2] = attrs[2] & ~termios.CSIZE
        attrs[2] = attrs[2] | termios.CS8
        attrs[3] = 0

        attrs[4] = termios.B921600
        attrs[5] = termios.B921600

        attrs[6][termios.VMIN] = 0
        attrs[6][termios.VTIME] = 1

        termios.tcflush(fd, termios.TCIOFLUSH)
        termios.tcsetattr(fd, termios.TCSANOW, attrs)
    finally:
        os.close(fd)


def write_line(ser: serial.Serial, text: str) -> None:
    ser.write((text + "\n").encode("utf-8"))
    ser.flush()


def open_serial(path: str) -> serial.Serial:
    # Short timeout so the reader loop wakes frequently and can react to
    # stop_event. The actual line-completion logic lives in serial_reader,
    # which accumulates bytes across reads and doesn't depend on a single
    # read() returning a full line.
    ser = serial.Serial(path, 921600, timeout=0.05)

    # CRITICAL: force the TTY out of canonical (line-buffered) mode.
    # pyserial's Serial() constructor can leave the kernel TTY layer in
    # canonical mode on Linux, where a single line is capped at ~4095
    # bytes -- anything longer gets truncated silently. Our clipboard
    # payloads (up to ~341 KB base64) absolutely need raw mode. Applying
    # termios here, AFTER pyserial has finished its own setup, ensures
    # our settings win regardless of pyserial version or platform quirks.
    fd = ser.fileno()
    attrs = termios.tcgetattr(fd)
    attrs[0] = 0  # iflag: no input processing
    attrs[1] = 0  # oflag: no output processing
    attrs[3] = 0  # lflag: NOT canonical, NOT echo, NOT signal processing
    attrs[6][termios.VMIN] = 0
    attrs[6][termios.VTIME] = 1
    termios.tcsetattr(fd, termios.TCSANOW, attrs)

    return ser


def serial_reader(name: str, ser: serial.Serial, out_queue: queue.Queue[str], stop_event: threading.Event) -> None:
    """Read bytes from `ser`, accumulate into lines on `\\n`, enqueue each.

    Do NOT rely on `ser.readline()` because pyserial's readline returns on
    timeout whether or not a newline has arrived, which corrupts long lines
    (e.g., clipboard payloads larger than a single kernel-buffer chunk).
    Instead, call `read()` in chunks and split on newlines ourselves.
    """
    # Upper bound on a single line. Has to comfortably exceed the largest
    # legitimate CLIPBOARD payload (256 KB raw -> ~341 KB base64 plus the
    # "CLIPBOARD DATA TEXT=" prefix). Anything longer gets discarded as
    # corrupt framing to keep a single bad transfer from unbounded growth.
    max_line_bytes = 512 * 1024
    buffer = bytearray()
    try:
        while not stop_event.is_set():
            try:
                # Read whatever is available, up to a generous chunk size.
                # in_waiting may be 0 if no bytes have arrived; fall back
                # to a blocking read (size=1) that respects the 50 ms
                # timeout so the loop can observe stop_event.
                pending = ser.in_waiting
                if pending > 0:
                    chunk = ser.read(min(pending, 65536))
                else:
                    chunk = ser.read(1)
            except Exception as ex:
                out_queue.put(f"ERROR {name} {ex}")
                return

            if not chunk:
                continue

            buffer.extend(chunk)

            # Pull out every complete line currently in the buffer.
            while True:
                newline_index = buffer.find(b"\n")
                if newline_index < 0:
                    break

                raw_line = bytes(buffer[:newline_index])
                del buffer[: newline_index + 1]

                line = raw_line.decode("utf-8", errors="ignore").strip()
                if line:
                    out_queue.put(f"{name}|{line}")

            # Guard against a runaway line (no newline ever arrives). If
            # the buffer balloons past the legitimate max, throw it away
            # rather than consuming memory forever.
            if len(buffer) > max_line_bytes:
                out_queue.put(
                    f"ERROR {name} discarding runaway line of {len(buffer)} bytes"
                )
                buffer.clear()
    except Exception as ex:
        out_queue.put(f"ERROR {name} {ex}")


def log_unhandled_mouse_key(event):
    code_name = ecodes.bytype.get(ecodes.EV_KEY, {}).get(event.code, f"UNKNOWN_{event.code}")
    print(f"UNHANDLED MOUSE KEY: code={event.code} name={code_name} value={event.value}")


def log_unhandled_mouse_rel(event):
    code_name = ecodes.bytype.get(ecodes.EV_REL, {}).get(event.code, f"UNKNOWN_{event.code}")
    print(f"UNHANDLED MOUSE REL: code={event.code} name={code_name} value={event.value}")


def log_unhandled_keyboard_key(event):
    key_name = ecodes.KEY.get(event.code, f"UNKNOWN_{event.code}")
    print(f"UNHANDLED KEYBOARD KEY: code={event.code} name={key_name} value={event.value}")


def release_all_inputs(active_serial: serial.Serial,
                       left_button_down: bool, right_button_down: bool, middle_button_down: bool,
                       pressed_keys: set[str]) -> None:
    if left_button_down:
        write_line(active_serial, "MOUSE BUTTON LEFT=UP")
    if right_button_down:
        write_line(active_serial, "MOUSE BUTTON RIGHT=UP")
    if middle_button_down:
        write_line(active_serial, "MOUSE BUTTON MIDDLE=UP")

    for key_name in pressed_keys:
        write_line(active_serial, f"KEY NAME={key_name} STATE=UP")


def handle_keyboard_event(event, target_serial: serial.Serial, pressed_keys: set[str]) -> bool:
    key_name = ecodes.KEY.get(event.code)

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
) -> None:
    if handle_display_line(line, personal_display, work_display):
        return

    if handle_cursor_line(line, personal_cursor, work_cursor):
        return

    if line.startswith("TARGET "):
        return

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
        return

    if line == "CLIPBOARD CLEAR":
        destination_serial = work_serial if source_name == "P" else personal_serial
        destination_name = "W" if source_name == "P" else "P"

        write_line(destination_serial, "CLIPBOARD CLEAR")
        print(f"clipboard CLEAR forwarded: {source_name} -> {destination_name}")
        return

    print(f"unhandled serial line from {source_name}: {line}")


def clamp(value: int, minimum: int, maximum: int) -> int:
    return max(minimum, min(value, maximum))


def send_cursor_set(ser: serial.Serial, x: int, y: int) -> None:
    write_line(ser, f"CURSOR SET X={x} Y={y}")


def main() -> int:
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

    mouse_paths = _normalize_path_list(config["devices"]["mouse"], "devices.mouse")
    keyboard_paths = _normalize_path_list(config["devices"]["keyboard"], "devices.keyboard")
    personal_uart_path = config["devices"]["personal_uart"]
    work_uart_path = config["devices"]["work_uart"]

    if not mouse_paths:
        print("Config error: devices.mouse is empty.", file=sys.stderr)
        return 1
    if not keyboard_paths:
        print("Config error: devices.keyboard is empty.", file=sys.stderr)
        return 1

    # Capture the verbose flag for use throughout the controller. We
    # promote it to a module-level variable so helper functions outside
    # main() (e.g., handle_cursor_line) can read it without taking it as
    # a parameter everywhere.
    global _verbose
    _verbose = bool(config["logging"]["verbose"])

    cmd_personal = config["commands"]["personal"].lower()
    cmd_work = config["commands"]["work"].lower()
    cmd_clipboard = config["commands"]["clipboard"].lower()
    cmd_quit = config["commands"]["quit"].lower()

    signal.signal(signal.SIGINT, handle_signal)
    signal.signal(signal.SIGTERM, handle_signal)

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
                open_devices[canon] = InputDevice(path)
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

    active_target = "P"

    # All cursor/display values below are kept in ABSOLUTE virtual-screen
    # coordinates: the same frame the agent reports via CURSOR and DISPLAY
    # lines and the same frame CURSOR SET consumes. Do not mix with per-
    # monitor local coordinates anywhere in this file.
    personal_display = {"L": 0, "T": 0, "W": 0, "H": 0}
    work_display = {"L": 0, "T": 0, "W": 0, "H": 0}
    personal_cursor = {"X": 0, "Y": 0}
    work_cursor = {"X": 0, "Y": 0}

    p_to_w_push = 0
    w_to_p_push = 0
    auto_switch_enabled = True

    # Switching parameters come from config.yaml (switching: section).
    # See controller/config.yaml for what each one does and recommended
    # bounds.
    EDGE_ARM_PIXELS = config["switching"]["edge_arm_pixels"]
    SWITCH_PUSH_PIXELS = config["switching"]["switch_push_pixels"]
    SWITCH_ENTRY_MARGIN_Y = config["switching"]["switch_entry_margin_y"]
    SWITCH_COOLDOWN_SECONDS = config["switching"]["switch_cooldown_seconds"]
    TARGET_ANNOUNCE_INTERVAL_SECONDS = config["switching"][
        "target_announce_interval_seconds"
    ]

    last_switch_time = 0.0
    last_target_announce_time = 0.0

    mouse_dx = 0
    mouse_dy = 0
    mouse_wheel = 0
    mouse_wheel_hi_res_accum = 0
    mouse_hwheel = 0
    mouse_hwheel_hi_res_accum = 0
    left_button_down = False
    right_button_down = False
    middle_button_down = False
    pressed_keys = set()

    print("Janus.InputRouter started. Press Ctrl+C to stop.")
    print("input devices:")
    for canon, dev in open_devices.items():
        roles = ",".join(sorted(role_of_canonical[canon]))
        print(f"  [{roles:<14}] {dev.path}  ({dev.name})")
    print(f"personal: {personal_uart_path}")
    print(f"work:     {work_uart_path}")
    print("commands: p=personal, w=work, c=clipboard, q=quit")
    print("active target: P")

    try:
        while running:
            # Periodic TARGET announcement. Fires on the first iteration
            # (last_target_announce_time == 0.0) and every interval after,
            # so any agent that comes up later catches its state.
            now = time.monotonic()
            if now - last_target_announce_time >= TARGET_ANNOUNCE_INTERVAL_SECONDS:
                write_line(personal_serial, f"TARGET {active_target}")
                write_line(work_serial, f"TARGET {active_target}")
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
                handle_serial_line(
                    source_name,
                    line,
                    personal_serial,
                    work_serial,
                    personal_display,
                    work_display,
                    personal_cursor,
                    work_cursor
                )

            # Wait for input events or serial lines
            input_fds = [d.fd for d in open_devices.values()]
            fds = input_fds + [sys.stdin]
            ready, _, _ = select.select(fds, [], [], 0.2)

            # Check for user commands
            if sys.stdin in ready:
                command = sys.stdin.readline().strip().lower()

                if command == cmd_personal:
                    active_target = "P"
                    p_to_w_push = 0
                    w_to_p_push = 0

                    if personal_display["W"] > 0 and personal_display["H"] > 0:
                        entry_x = clamp(
                            personal_cursor["X"],
                            personal_display["L"],
                            personal_display["L"] + personal_display["W"] - 1,
                        )
                        entry_y = clamp(
                            personal_cursor["Y"],
                            personal_display["T"],
                            personal_display["T"] + personal_display["H"] - 1,
                        )
                    else:
                        entry_x = 0
                        entry_y = 0

                    write_line(personal_serial, "TARGET P")
                    write_line(work_serial, "TARGET P")

                    send_cursor_set(personal_serial, entry_x, entry_y)

                    personal_cursor["X"] = entry_x
                    personal_cursor["Y"] = entry_y

                    last_switch_time = time.monotonic()
                    last_target_announce_time = last_switch_time
                    print("\n=== ACTIVE: PERSONAL ===\n")

                elif command == cmd_work:
                    active_target = "W"
                    p_to_w_push = 0
                    w_to_p_push = 0

                    if work_display["W"] > 0 and work_display["H"] > 0:
                        entry_x = clamp(
                            work_cursor["X"],
                            work_display["L"],
                            work_display["L"] + work_display["W"] - 1,
                        )
                        entry_y = clamp(
                            work_cursor["Y"],
                            work_display["T"],
                            work_display["T"] + work_display["H"] - 1,
                        )
                    else:
                        entry_x = 0
                        entry_y = 0

                    write_line(personal_serial, "TARGET W")
                    write_line(work_serial, "TARGET W")

                    send_cursor_set(work_serial, entry_x, entry_y)

                    work_cursor["X"] = entry_x
                    work_cursor["Y"] = entry_y

                    last_switch_time = time.monotonic()
                    last_target_announce_time = last_switch_time
                    print("\n=== ACTIVE: WORK ===\n")

                elif command == cmd_clipboard:
                    target_serial = personal_serial if active_target == "P" else work_serial
                    write_line(target_serial, "CLIPBOARD REQUEST")
                    print(f"clipboard request sent to: {active_target}")

                elif command == cmd_quit:
                    running = False
                    continue

            target_serial = personal_serial if active_target == "P" else work_serial

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
                    # Mouse Wheel Events
                    if event.type == ecodes.EV_REL:
                        if event.code == ecodes.REL_X:
                            mouse_dx += event.value

                        elif event.code == ecodes.REL_Y:
                            mouse_dy += event.value

                        elif event.code == ecodes.REL_WHEEL:
                            mouse_wheel += event.value

                        elif event.code == ecodes.REL_WHEEL_HI_RES:
                            mouse_wheel_hi_res_accum += event.value

                            while mouse_wheel_hi_res_accum >= 120:
                                mouse_wheel += 1
                                mouse_wheel_hi_res_accum -= 120

                            while mouse_wheel_hi_res_accum <= -120:
                                mouse_wheel -= 1
                                mouse_wheel_hi_res_accum += 120

                        elif event.code == ecodes.REL_HWHEEL:
                            mouse_hwheel += event.value

                        elif event.code == ecodes.REL_HWHEEL_HI_RES:
                            mouse_hwheel_hi_res_accum += event.value

                            while mouse_hwheel_hi_res_accum >= 120:
                                mouse_hwheel += 1
                                mouse_hwheel_hi_res_accum -= 120

                            while mouse_hwheel_hi_res_accum <= -120:
                                mouse_hwheel -= 1
                                mouse_hwheel_hi_res_accum += 120

                        else:
                            log_unhandled_mouse_rel(event)

                    # Mouse Button Events
                    elif event.type == ecodes.EV_KEY:
                        if event.code == ecodes.BTN_LEFT:
                            if event.value == 1:
                                if not left_button_down:
                                    left_button_down = True
                                    if _verbose: print("MOUSE BUTTON LEFT=DOWN")
                                    write_line(target_serial, "MOUSE BUTTON LEFT=DOWN")
                            elif event.value == 0:
                                if left_button_down:
                                    left_button_down = False
                                    if _verbose: print("MOUSE BUTTON LEFT=UP")
                                    write_line(target_serial, "MOUSE BUTTON LEFT=UP")
                            elif event.value == 2:
                                pass

                        elif event.code == ecodes.BTN_RIGHT:
                            if event.value == 1:
                                if not right_button_down:
                                    right_button_down = True
                                    if _verbose: print("MOUSE BUTTON RIGHT=DOWN")
                                    write_line(target_serial, "MOUSE BUTTON RIGHT=DOWN")
                            elif event.value == 0:
                                if right_button_down:
                                    right_button_down = False
                                    if _verbose: print("MOUSE BUTTON RIGHT=UP")
                                    write_line(target_serial, "MOUSE BUTTON RIGHT=UP")
                            elif event.value == 2:
                                pass

                        elif event.code == ecodes.BTN_MIDDLE:
                            if event.value == 1:
                                if not middle_button_down:
                                    middle_button_down = True
                                    if _verbose: print("MOUSE BUTTON MIDDLE=DOWN")
                                    write_line(target_serial, "MOUSE BUTTON MIDDLE=DOWN")
                            elif event.value == 0:
                                if middle_button_down:
                                    middle_button_down = False
                                    if _verbose: print("MOUSE BUTTON MIDDLE=UP")
                                    write_line(target_serial, "MOUSE BUTTON MIDDLE=UP")
                            elif event.value == 2:
                                pass

                        # Any other EV_KEY code (i.e., not BTN_LEFT/RIGHT/
                        # MIDDLE handled above) is a keyboard-shape event,
                        # regardless of which device emitted it. This is
                        # how Razer Synapse-programmed buttons that come
                        # over the Basilisk's "if02-event-kbd" node, or
                        # combo receivers like the Logitech K400 reporting
                        # keystrokes on the mouse node, get forwarded to
                        # the destination PC as keystrokes.
                        else:
                            if handle_keyboard_event(event, target_serial, pressed_keys):
                                continue
                            log_unhandled_mouse_key(event)

                    # Mouse Movement Events (sent on SYN_REPORT)
                    elif event.type == ecodes.EV_SYN and event.code == ecodes.SYN_REPORT:
                        if mouse_dx != 0 or mouse_dy != 0:
                            cooling_down = (
                                time.monotonic() - last_switch_time < SWITCH_COOLDOWN_SECONDS
                            )

                            if active_target == "P":
                                # Everything below is in absolute virtual-screen
                                # coordinates.
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

                                # Arm on Personal's bottom edge (absolute frame).
                                # Suppress arming entirely during the post-switch
                                # cooldown.
                                #
                                # Reset only when the user either leaves the edge
                                # zone or actively pushes back (mouse_dy < 0).
                                # A SYN cycle with horizontal jitter (mouse_dy == 0)
                                # must NOT reset the counter; otherwise slow
                                # diagonal motion never reaches threshold.
                                if cooling_down:
                                    p_to_w_push = 0
                                elif p_w > 0 and p_h > 0:
                                    bottom_arm = p_t + p_h - 1 - EDGE_ARM_PIXELS
                                    was_armed = p_to_w_push > 0
                                    in_zone = cursor_y >= bottom_arm

                                    if not in_zone or mouse_dy < 0:
                                        p_to_w_push = 0
                                    elif mouse_dy > 0:
                                        p_to_w_push += mouse_dy
                                    # mouse_dy == 0: keep current push as-is

                                    # Only log the transition into armed state,
                                    # not every event that keeps it armed.
                                    if not was_armed and p_to_w_push > 0:
                                        print(
                                            f"AUTO P->W arming cursor_y={cursor_y} "
                                            f"threshold={bottom_arm}"
                                        )

                                if (p_to_w_push >= SWITCH_PUSH_PIXELS
                                        and work_display["W"] > 0 and work_display["H"] > 0):
                                    active_target = "W"
                                    p_to_w_push = 0
                                    w_to_p_push = 0

                                    w_l = work_display["L"]
                                    w_t = work_display["T"]
                                    w_w = work_display["W"]

                                    # Preserve X offset from the source monitor's
                                    # left edge; clamp into the target monitor's
                                    # horizontal bounds.
                                    source_x_offset = cursor_x - p_l
                                    entry_x = clamp(
                                        w_l + source_x_offset,
                                        w_l,
                                        w_l + w_w - 1,
                                    )
                                    entry_y = w_t + SWITCH_ENTRY_MARGIN_Y

                                    write_line(personal_serial, "TARGET W")
                                    write_line(work_serial, "TARGET W")
                                    send_cursor_set(work_serial, entry_x, entry_y)

                                    work_cursor["X"] = entry_x
                                    work_cursor["Y"] = entry_y

                                    last_switch_time = time.monotonic()
                                    last_target_announce_time = last_switch_time
                                    print("\n=== ACTIVE: WORK (AUTO) ===\n")
                                    mouse_dx = 0
                                    mouse_dy = 0
                                    continue

                            elif active_target == "W":
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

                                # Arm on Work's top edge (absolute frame).
                                # Same reset discipline as P->W: only reset
                                # when leaving the edge zone or pushing back
                                # downward. A SYN cycle with x-only motion
                                # (mouse_dy == 0) must preserve the counter.
                                if cooling_down:
                                    w_to_p_push = 0
                                elif w_w > 0 and w_h > 0:
                                    top_arm = w_t + EDGE_ARM_PIXELS
                                    was_armed = w_to_p_push > 0
                                    in_zone = cursor_y <= top_arm

                                    if not in_zone or mouse_dy > 0:
                                        w_to_p_push = 0
                                    elif mouse_dy < 0:
                                        w_to_p_push += -mouse_dy
                                    # mouse_dy == 0: keep current push as-is

                                    if not was_armed and w_to_p_push > 0:
                                        print(
                                            f"AUTO W->P arming cursor_y={cursor_y} "
                                            f"threshold={top_arm}"
                                        )

                                if (w_to_p_push >= SWITCH_PUSH_PIXELS
                                        and personal_display["W"] > 0 and personal_display["H"] > 0):
                                    active_target = "P"
                                    p_to_w_push = 0
                                    w_to_p_push = 0

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
                                    entry_y = p_t + p_h - 1 - SWITCH_ENTRY_MARGIN_Y

                                    write_line(personal_serial, "TARGET P")
                                    write_line(work_serial, "TARGET P")
                                    send_cursor_set(personal_serial, entry_x, entry_y)

                                    personal_cursor["X"] = entry_x
                                    personal_cursor["Y"] = entry_y

                                    last_switch_time = time.monotonic()
                                    last_target_announce_time = last_switch_time
                                    print("\n=== ACTIVE: PERSONAL (AUTO) ===\n")
                                    mouse_dx = 0
                                    mouse_dy = 0
                                    continue

                            target_serial = personal_serial if active_target == "P" else work_serial
                            if _verbose:
                                print(f"MOUSE MOVE target={active_target} dx={mouse_dx} dy={mouse_dy}")
                            write_line(target_serial, f"MOUSE MOVE DX={mouse_dx} DY={mouse_dy}")

                            mouse_dx = 0
                            mouse_dy = 0

                        if mouse_wheel != 0:
                            target_serial = personal_serial if active_target == "P" else work_serial
                            write_line(target_serial, f"MOUSE WHEEL DELTA={mouse_wheel}")
                            mouse_wheel = 0

                        if mouse_hwheel != 0:
                            target_serial = personal_serial if active_target == "P" else work_serial
                            write_line(target_serial, f"MOUSE HWHEEL DELTA={mouse_hwheel}")
                            mouse_hwheel = 0

    except KeyboardInterrupt:
        pass
    finally:
        try:
            release_all_inputs(
                target_serial,
                left_button_down,
                right_button_down,
                middle_button_down,
                pressed_keys,
            )
        except Exception as ex:
            print(f"release_all_inputs error: {ex}")

        left_button_down = False
        right_button_down = False
        middle_button_down = False
        pressed_keys.clear()

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


if __name__ == "__main__":
    raise SystemExit(main())