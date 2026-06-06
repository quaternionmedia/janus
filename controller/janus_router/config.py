"""Configuration for the Janus controller.

Settings come from `config.yaml` next to the entry script. Missing
fields fall back to the defaults below (which match the historical
hardcoded values). CLI flags can override individual fields at
startup. An optional `device_config_file` names a YAML in `profiles/`
whose `devices:` section overrides paths -- useful for switching
between Logitech-K400-style and Razer/SteelSeries-style setups
without editing `config.yaml`.

`load_config(config_dir=None)` and
`load_device_config_file(config, config_dir=None)` both accept an
optional directory. When omitted, they assume the project layout:
this module lives in `controller/janus_router/`, so the project root
is `Path(__file__).parent.parent`. That's where `config.yaml` and
the `profiles/` directory live alongside the entry script.
"""
import argparse
from pathlib import Path

import yaml


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
        "auto_edge_switch_enabled": True,
        "edge_arm_pixels": 2,
        "switch_push_pixels": 12,
        "switch_entry_margin_y": 32,
        "switch_cooldown_seconds": 0.15,
        "target_announce_interval_seconds": 2.0,
        "dead_peer_threshold_seconds": 10.0,
        "dead_peer_switch_cooldown_seconds": 30.0,
        "dead_peer_home_base": "P",
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


def _project_root() -> Path:
    """Resolve the directory holding config.yaml / profiles/.

    Assumes this module lives in `<root>/janus_router/`, which matches
    the deployed layout. Tests/callers can pass their own dir to the
    loader functions to bypass this.
    """
    return Path(__file__).parent.parent


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


def load_config(config_dir: Path | None = None) -> dict:
    """Load config.yaml from `config_dir` (default: project root),
    layered over defaults.

    A missing file or missing fields fall through to DEFAULT_CONFIG, so
    the controller still starts with sensible behavior even on a fresh
    deployment.
    """
    base = config_dir if config_dir is not None else _project_root()
    config_path = base / "config.yaml"
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


def load_device_config_file(config: dict, config_dir: Path | None = None) -> dict:
    """Apply the active device file's `devices:` section on top of config.

    Reads the filename from config["device_config_file"], opens
    `<config_dir>/profiles/<filename>`, merges its `devices:` over the
    config-level `devices:`. The device file is authoritative for
    input/UART paths.

    If `device_config_file` is unset, returns config unchanged (the
    config-level `devices:` is used directly).

    If the file is missing, unreadable, or has no `devices:` section, logs
    a loud warning and returns config unchanged -- the controller will
    start with whatever `devices:` config.yaml has, which is most likely
    the built-in DEFAULT_CONFIG paths. Those are Logitech-K400-style
    defaults and probably DON'T match the actual hardware on this Pi,
    so check the log if input doesn't work.
    """
    filename = config.get("device_config_file")
    if not filename:
        return config

    base = config_dir if config_dir is not None else _project_root()
    device_path = base / "profiles" / filename

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


def normalize_path_list(value, label: str) -> list[str]:
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