# Janus stage 4: Pico injects HID directly.
#
# Stage 3 was a byte-blind pass-through: the Pico forwarded UART bytes to
# CDC and CDC bytes to UART, both directions, without inspecting them.
#
# Stage 4 adds line-level routing on the UART->CDC direction. Lines that
# represent input events (MOUSE MOVE, MOUSE BUTTON, MOUSE WHEEL, KEY,
# etc.) are PARSED and translated into HID reports that the PC sees as
# coming from a real keyboard/mouse. Everything else (TARGET, CURSOR
# SET, CLIPBOARD ..., CURSOR sync) still passes through to CDC for the
# agent to handle. The CDC->UART direction stays a pure byte pipe -- the
# agent only sends data the controller cares about, so there's nothing
# to interpret here.
#
# Why this matters:
#   - HID input bypasses Win32 entirely, so UAC prompts, login screens,
#     and fullscreen apps that capture raw input all accept keystrokes.
#   - If the agent crashes, mouse and keyboard still work (no agent on
#     the input path).
#   - Reduces the surface area of the agent. The agent is now just
#     clipboard sync + display reporting + cursor sync + CURSOR SET.
#
# Wiring is unchanged from stage 3.

import board
import busio
import supervisor
import sys
import time
import usb_cdc
import usb_hid

from adafruit_hid.keyboard import Keyboard
from adafruit_hid.keycode import Keycode
from adafruit_hid.mouse import Mouse


UART_BAUD = 921_600

# Reads pull whatever's available, up to this size. Bigger is better for
# bursty traffic (less per-iteration overhead). 4 KB comfortably handles
# typical CDC and UART burst sizes.
UART_READ_SIZE = 4096
CDC_READ_SIZE = 4096

# Max bytes per individual cdc.write() when forwarding a long line. Keeps
# each write small enough that it flushes quickly and the loop stays
# responsive (HID injection, UART reads) even while a big clipboard
# payload is streaming out. 256 is comfortably under the USB CDC TX
# buffer size, so each slice typically goes out in one shot.
_CDC_WRITE_CHUNK = 256

# Guards a pathological case where the controller sends a line with no
# trailing newline. We keep accumulating UART bytes into a line buffer
# until we see "\n", but a runaway sender could grow it without bound.
#
# This MUST be larger than the longest legitimate line, which is a
# clipboard payload, NOT an input message. The agent's clipboard hard cap
# is 256 KB raw -> ~350 KB base64 + the "CLIPBOARD SET TEXT=" prefix. We
# set the cap at 400 KB so a real max-size clipboard line never trips the
# runaway guard, while still catching a truly unbounded (newline-less)
# stream. (Earlier this was 4096, sized for input lines only -- which
# silently destroyed any clipboard line larger than ~4 KB mid-transfer.)
UART_LINE_MAX = 400_000


# -------------------------------------------------------------------------
# evdev KEY_* -> adafruit_hid Keycode mapping.
#
# The controller emits keys by their evdev name (KEY_A, KEY_LEFTSHIFT,
# KEY_F5, etc.). We translate to the Keycode constants from the
# Adafruit HID library before calling Keyboard.press / .release.
#
# Keys that don't appear here are silently ignored. To add a missing
# key: find its evdev name (`evtest` on the Pi 5, or check
# /usr/include/linux/input-event-codes.h) and pair it with the matching
# Keycode attribute. The full Keycode list is at:
#   https://docs.circuitpython.org/projects/hid/en/latest/api.html
# -------------------------------------------------------------------------

_KEYCODE_MAP = {
    # Letters
    "KEY_A": Keycode.A, "KEY_B": Keycode.B, "KEY_C": Keycode.C,
    "KEY_D": Keycode.D, "KEY_E": Keycode.E, "KEY_F": Keycode.F,
    "KEY_G": Keycode.G, "KEY_H": Keycode.H, "KEY_I": Keycode.I,
    "KEY_J": Keycode.J, "KEY_K": Keycode.K, "KEY_L": Keycode.L,
    "KEY_M": Keycode.M, "KEY_N": Keycode.N, "KEY_O": Keycode.O,
    "KEY_P": Keycode.P, "KEY_Q": Keycode.Q, "KEY_R": Keycode.R,
    "KEY_S": Keycode.S, "KEY_T": Keycode.T, "KEY_U": Keycode.U,
    "KEY_V": Keycode.V, "KEY_W": Keycode.W, "KEY_X": Keycode.X,
    "KEY_Y": Keycode.Y, "KEY_Z": Keycode.Z,

    # Top-row digits
    "KEY_0": Keycode.ZERO, "KEY_1": Keycode.ONE, "KEY_2": Keycode.TWO,
    "KEY_3": Keycode.THREE, "KEY_4": Keycode.FOUR, "KEY_5": Keycode.FIVE,
    "KEY_6": Keycode.SIX, "KEY_7": Keycode.SEVEN, "KEY_8": Keycode.EIGHT,
    "KEY_9": Keycode.NINE,

    # Whitespace / control
    "KEY_SPACE": Keycode.SPACEBAR,
    "KEY_ENTER": Keycode.ENTER,
    "KEY_ESC": Keycode.ESCAPE,
    "KEY_TAB": Keycode.TAB,
    "KEY_BACKSPACE": Keycode.BACKSPACE,

    # Modifiers
    "KEY_LEFTSHIFT": Keycode.LEFT_SHIFT,
    "KEY_RIGHTSHIFT": Keycode.RIGHT_SHIFT,
    "KEY_LEFTCTRL": Keycode.LEFT_CONTROL,
    "KEY_RIGHTCTRL": Keycode.RIGHT_CONTROL,
    "KEY_LEFTALT": Keycode.LEFT_ALT,
    "KEY_RIGHTALT": Keycode.RIGHT_ALT,
    "KEY_LEFTMETA": Keycode.LEFT_GUI,
    "KEY_RIGHTMETA": Keycode.RIGHT_GUI,

    # Arrows
    "KEY_UP": Keycode.UP_ARROW,
    "KEY_DOWN": Keycode.DOWN_ARROW,
    "KEY_LEFT": Keycode.LEFT_ARROW,
    "KEY_RIGHT": Keycode.RIGHT_ARROW,

    # Navigation cluster
    "KEY_INSERT": Keycode.INSERT,
    "KEY_DELETE": Keycode.DELETE,
    "KEY_HOME": Keycode.HOME,
    "KEY_END": Keycode.END,
    "KEY_PAGEUP": Keycode.PAGE_UP,
    "KEY_PAGEDOWN": Keycode.PAGE_DOWN,

    # Lock/system keys
    "KEY_CAPSLOCK": Keycode.CAPS_LOCK,
    "KEY_NUMLOCK": Keycode.KEYPAD_NUMLOCK,
    "KEY_SCROLLLOCK": Keycode.SCROLL_LOCK,
    "KEY_SYSRQ": Keycode.PRINT_SCREEN,    # PrtScn
    "KEY_PAUSE": Keycode.PAUSE,            # Pause/Break
    "KEY_COMPOSE": Keycode.APPLICATION,    # Menu key on Windows keyboards

    # Function keys
    "KEY_F1": Keycode.F1, "KEY_F2": Keycode.F2, "KEY_F3": Keycode.F3,
    "KEY_F4": Keycode.F4, "KEY_F5": Keycode.F5, "KEY_F6": Keycode.F6,
    "KEY_F7": Keycode.F7, "KEY_F8": Keycode.F8, "KEY_F9": Keycode.F9,
    "KEY_F10": Keycode.F10, "KEY_F11": Keycode.F11, "KEY_F12": Keycode.F12,
    "KEY_F13": Keycode.F13, "KEY_F14": Keycode.F14, "KEY_F15": Keycode.F15,
    "KEY_F16": Keycode.F16, "KEY_F17": Keycode.F17, "KEY_F18": Keycode.F18,
    "KEY_F19": Keycode.F19, "KEY_F20": Keycode.F20, "KEY_F21": Keycode.F21,
    "KEY_F22": Keycode.F22, "KEY_F23": Keycode.F23, "KEY_F24": Keycode.F24,

    # Punctuation (US QWERTY positions)
    "KEY_MINUS": Keycode.MINUS,
    "KEY_EQUAL": Keycode.EQUALS,
    "KEY_LEFTBRACE": Keycode.LEFT_BRACKET,
    "KEY_RIGHTBRACE": Keycode.RIGHT_BRACKET,
    "KEY_BACKSLASH": Keycode.BACKSLASH,
    "KEY_SEMICOLON": Keycode.SEMICOLON,
    "KEY_APOSTROPHE": Keycode.QUOTE,
    "KEY_GRAVE": Keycode.GRAVE_ACCENT,
    "KEY_COMMA": Keycode.COMMA,
    "KEY_DOT": Keycode.PERIOD,
    "KEY_SLASH": Keycode.FORWARD_SLASH,

    # Numpad
    "KEY_KP0": Keycode.KEYPAD_ZERO,
    "KEY_KP1": Keycode.KEYPAD_ONE,
    "KEY_KP2": Keycode.KEYPAD_TWO,
    "KEY_KP3": Keycode.KEYPAD_THREE,
    "KEY_KP4": Keycode.KEYPAD_FOUR,
    "KEY_KP5": Keycode.KEYPAD_FIVE,
    "KEY_KP6": Keycode.KEYPAD_SIX,
    "KEY_KP7": Keycode.KEYPAD_SEVEN,
    "KEY_KP8": Keycode.KEYPAD_EIGHT,
    "KEY_KP9": Keycode.KEYPAD_NINE,
    "KEY_KPDOT": Keycode.KEYPAD_PERIOD,
    "KEY_KPENTER": Keycode.KEYPAD_ENTER,
    "KEY_KPSLASH": Keycode.KEYPAD_FORWARD_SLASH,
    "KEY_KPASTERISK": Keycode.KEYPAD_ASTERISK,
    "KEY_KPMINUS": Keycode.KEYPAD_MINUS,
    "KEY_KPPLUS": Keycode.KEYPAD_PLUS,
    "KEY_KPEQUAL": Keycode.KEYPAD_EQUALS,
}


# -------------------------------------------------------------------------
# Mouse button mapping. Matches the wire protocol's tokens.
# -------------------------------------------------------------------------

_MOUSE_BUTTON_BITS = {
    "LEFT": Mouse.LEFT_BUTTON,
    "RIGHT": Mouse.RIGHT_BUTTON,
    "MIDDLE": Mouse.MIDDLE_BUTTON,
}


# -------------------------------------------------------------------------
# Lines that the Pico INTERPRETS as HID. Anything not in this set passes
# through to CDC unchanged. Match is by prefix; the line splitter handles
# the trailing tokens.
# -------------------------------------------------------------------------

_HID_PREFIXES = (
    b"MOUSE MOVE ",
    b"MOUSE BUTTON ",
    b"MOUSE WHEEL ",
    b"MOUSE HWHEEL ",
    b"KEY ",
)


# =========================================================================
# Parsing / token helpers
# =========================================================================

def _parse_kv_int(token, key):
    """Parse a `KEY=value` ASCII token, returning the int value or None."""
    eq = token.find(b"=")
    if eq < 0:
        return None
    if token[:eq] != key:
        return None
    try:
        return int(token[eq + 1:])
    except (ValueError, TypeError):
        return None


def _parse_kv_str(token, key):
    """Parse a `KEY=value` ASCII token, returning the str value or None."""
    eq = token.find(b"=")
    if eq < 0:
        return None
    if token[:eq] != key:
        return None
    try:
        return token[eq + 1:].decode("ascii")
    except (UnicodeDecodeError, AttributeError):
        return None


# =========================================================================
# HID handlers. Each takes the raw line bytes (without trailing newline).
# =========================================================================

def _handle_mouse_move(line, mouse):
    # "MOUSE MOVE DX=3 DY=-2"
    parts = line.split(b" ")
    dx = 0
    dy = 0
    for p in parts:
        v = _parse_kv_int(p, b"DX")
        if v is not None:
            dx = v
            continue
        v = _parse_kv_int(p, b"DY")
        if v is not None:
            dy = v
    if dx or dy:
        # adafruit_hid clamps to -127..127 internally, which matches the
        # standard HID boot mouse report. Large deltas from the controller
        # (rare; only on very fast Logitech motion) get truncated. The
        # controller already accumulates per-SYN deltas, so values rarely
        # exceed +/-50 in practice.
        mouse.move(x=dx, y=dy)


def _handle_mouse_button(line, mouse):
    # "MOUSE BUTTON LEFT=DOWN"
    parts = line.split(b" ")
    if len(parts) < 3:
        return
    token = parts[2]  # "LEFT=DOWN" etc.
    eq = token.find(b"=")
    if eq < 0:
        return
    button_name = token[:eq].decode("ascii", "ignore")
    state = token[eq + 1:].decode("ascii", "ignore")

    bit = _MOUSE_BUTTON_BITS.get(button_name)
    if bit is None:
        return
    if state == "DOWN":
        mouse.press(bit)
    elif state == "UP":
        mouse.release(bit)


def _handle_mouse_wheel(line, mouse, horizontal=False):
    # "MOUSE WHEEL DELTA=1" or "MOUSE HWHEEL DELTA=-1"
    parts = line.split(b" ")
    delta = 0
    for p in parts:
        v = _parse_kv_int(p, b"DELTA")
        if v is not None:
            delta = v
            break
    if delta == 0:
        return
    # adafruit_hid's Mouse.move signature: move(x=0, y=0, wheel=0).
    # There's no separate "horizontal wheel" parameter on the standard
    # boot-mouse HID descriptor, so HWHEEL falls back to vertical scroll.
    # If we ever care, we'd swap to a custom HID descriptor with hwheel.
    # Until then, treat both as wheel and accept the limitation.
    if horizontal:
        # No-op rather than misleading. Most apps don't use HWHEEL anyway.
        return
    mouse.move(wheel=delta)


def _handle_key(line, keyboard):
    # "KEY NAME=KEY_A STATE=DOWN"
    parts = line.split(b" ")
    name = None
    state = None
    for p in parts:
        v = _parse_kv_str(p, b"NAME")
        if v is not None:
            name = v
            continue
        v = _parse_kv_str(p, b"STATE")
        if v is not None:
            state = v
    if not name or not state:
        return
    keycode = _KEYCODE_MAP.get(name)
    if keycode is None:
        # Unmapped key: silently ignore. Could log via print() to REPL
        # for debugging but we don't want that in the hot path.
        return
    if state == "DOWN":
        keyboard.press(keycode)
    elif state == "UP":
        keyboard.release(keycode)


# =========================================================================
# Routing: decide whether a complete line is HID or passthrough.
# =========================================================================

def _cdc_write_all(cdc, data):
    """Write all of `data` to CDC in bounded chunks.

    A single cdc.write() of a large payload (e.g. a 22 KB base64 clipboard
    line) is dangerous: the USB TX buffer is only a few hundred bytes, so
    one big write either truncates (write_timeout=0) or blocks the entire
    main loop for a long time (write_timeout>0), freezing HID injection and
    UART reads while it drains.

    Instead we write in small slices. Between slices the loop is free to
    continue on the next iteration of the caller, and CircuitPython yields
    to the USB stack so the host can drain the endpoint. We also stop
    early if the host disconnects, so a vanished host can't wedge us.

    cdc.write_timeout is expected to be a small positive value (set in
    main), so each individual slice write is itself bounded.
    """
    mv = memoryview(data)
    total = len(mv)
    offset = 0
    while offset < total:
        if not cdc.connected:
            # Host went away mid-write; abandon the rest. The line is lost
            # but the bridge stays alive for the next connection.
            return
        end = offset + _CDC_WRITE_CHUNK
        if end > total:
            end = total
        written = cdc.write(mv[offset:end])
        # cdc.write returns the number of bytes actually written. With a
        # positive write_timeout it should write the whole slice, but if it
        # writes fewer (timeout expired), advance only by what went out and
        # retry the remainder next iteration.
        if written:
            offset += written
        # If written is 0/None, loop again; the connected check above
        # prevents an infinite spin against a dead host.


def _route_line(line, keyboard, mouse, cdc):
    """Dispatch one complete line (no trailing newline).

    Lines matching an HID prefix are handled locally. Everything else is
    forwarded to CDC verbatim, with the newline restored.
    """
    # Each of these has to be tried specifically because MOUSE WHEEL and
    # MOUSE HWHEEL share the same MOUSE prefix; we route on the more
    # specific prefix first.
    if line.startswith(b"MOUSE MOVE "):
        _handle_mouse_move(line, mouse)
        return
    if line.startswith(b"MOUSE BUTTON "):
        _handle_mouse_button(line, mouse)
        return
    if line.startswith(b"MOUSE HWHEEL "):
        _handle_mouse_wheel(line, mouse, horizontal=True)
        return
    if line.startswith(b"MOUSE WHEEL "):
        _handle_mouse_wheel(line, mouse, horizontal=False)
        return
    if line.startswith(b"KEY "):
        _handle_key(line, keyboard)
        return

    # Non-input message: passthrough to the agent on CDC, in bounded
    # chunks so a large payload can't wedge the loop. Restore the newline
    # that line splitting consumed.
    _cdc_write_all(cdc, line)
    _cdc_write_all(cdc, b"\n")


# =========================================================================
# Main loop
# =========================================================================

def main():
    if usb_cdc.data is None:
        print("ERROR: usb_cdc.data is None; check boot.py")
        while True:
            pass

    cdc = usb_cdc.data
    cdc.timeout = 0
    # write_timeout must NOT be 0 (that makes write() non-blocking and it
    # silently drops whatever doesn't fit the USB TX buffer -- truncating
    # long lines). We write in small chunks via _cdc_write_all, so each
    # individual write is tiny; a modest per-write timeout is plenty and
    # bounds how long any single slice can stall if the host hiccups.
    cdc.write_timeout = 1.0

    uart = busio.UART(
        tx=board.GP0,
        rx=board.GP1,
        baudrate=UART_BAUD,
        timeout=0,
        receiver_buffer_size=65535,
    )

    keyboard = Keyboard(usb_hid.devices)
    mouse = Mouse(usb_hid.devices)

    # Line accumulator for UART->CDC direction. UART bytes don't always
    # arrive line-aligned; we buffer until we see "\n" and then dispatch.
    uart_line = bytearray()

    print(f"stage 4 ready: UART0 @ {UART_BAUD}, HID + CDC passthrough")

    while True:
        # ---- PC -> Pi 5: pure passthrough -------------------------------
        # The agent only sends data the controller cares about (clipboard,
        # display, cursor sync). None of it is HID for us to interpret.
        cdc_pending = cdc.in_waiting
        if cdc_pending > 0:
            chunk = cdc.read(min(cdc_pending, CDC_READ_SIZE))
            if chunk:
                uart.write(chunk)

        # ---- Pi 5 -> PC: line-aware routing -----------------------------
        # Bytes from UART get accumulated, split on newline, and each
        # complete line is routed to either HID or CDC.
        uart_pending = uart.in_waiting
        if uart_pending > 0:
            chunk = uart.read(min(uart_pending, UART_READ_SIZE))
            if chunk:
                uart_line.extend(chunk)

                # Drain every complete line currently in the buffer.
                while True:
                    nl = uart_line.find(b"\n")
                    if nl < 0:
                        break
                    line_bytes = bytes(uart_line[:nl])
                    uart_line = uart_line[nl + 1:]

                    # An empty line (consecutive \n's) is meaningless; skip.
                    if not line_bytes:
                        continue

                    # Strip trailing \r if the controller ever sends CRLF
                    # by accident. Our controller uses LF only, but defensive.
                    if line_bytes.endswith(b"\r"):
                        line_bytes = line_bytes[:-1]
                        if not line_bytes:
                            continue

                    try:
                        _route_line(line_bytes, keyboard, mouse, cdc)
                    except Exception as ex:
                        # A malformed input line must not crash the bridge.
                        # Log to REPL and continue; the stream is still
                        # healthy.
                        print(f"route error: {ex} on line: {line_bytes!r}")

                # Discard pathological runaway (no newline ever arrives).
                if len(uart_line) > UART_LINE_MAX:
                    print(
                        f"discarding runaway uart line of {len(uart_line)} bytes"
                    )
                    # CircuitPython bytearray has no .clear(); rebind instead.
                    uart_line = bytearray()


# ---------------------------------------------------------------------------
# Entry point with crash auto-recovery.
#
# If main() ever raises an unhandled exception, CircuitPython would
# normally drop to the REPL and sit dead until a manual replug. That's bad
# for an unattended bridge: a single transient error takes the whole side
# offline. Instead we catch anything that escapes main(), print the
# traceback (so it's still visible on the REPL for debugging), wait a
# moment, and soft-reload to restart cleanly.
#
# The delay before reload matters: if the crash happens immediately at
# startup (e.g. a code error), we'd otherwise spin in a tight reload loop
# that makes the board hard to recover. A few seconds gives a window to
# drop into the REPL (Ctrl-C) and stop it for editing, and avoids
# hammering the USB stack.
#
# KeyboardInterrupt is deliberately NOT caught here, so Ctrl-C at the REPL
# still stops the program for development.

def _run_with_recovery():
    try:
        main()
    except KeyboardInterrupt:
        # Let Ctrl-C stop the program for development; don't auto-reload.
        raise
    except Exception as ex:
        print("=" * 60)
        print("Janus bridge crashed; auto-reloading in 5 seconds.")
        print("Press Ctrl-C now to stay in the REPL for debugging.")
        sys.print_exception(ex)
        print("=" * 60)
        try:
            time.sleep(5)
        except KeyboardInterrupt:
            # User chose to break in during the grace period; stay in REPL.
            raise
        supervisor.reload()


_run_with_recovery()