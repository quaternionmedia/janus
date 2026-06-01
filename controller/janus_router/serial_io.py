"""Serial / UART I/O for the Janus controller.

Owns four things:
  * configure_uart(path)        — kernel TTY setup before opening
  * open_serial(path)           — open a pyserial port and force raw mode
  * write_line(ser, text)       — newline-terminated write + flush
  * serial_reader(...)          — background thread that buffers bytes and
                                  enqueues complete lines

The reader thread accumulates bytes across reads and splits on '\\n'
itself; do NOT rely on pyserial's readline() because it returns on
timeout whether or not a newline has arrived, which corrupts long lines
(specifically the clipboard payloads, which can run to ~341 KB).
"""
import os
import queue
import termios
import threading

import serial


def configure_uart(path: str) -> None:
    """Force the kernel TTY out of canonical/echo mode before pyserial
    opens the port. Without this, lines over ~4095 bytes get truncated
    silently."""
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


def serial_reader(
    name: str,
    ser: serial.Serial,
    out_queue: queue.Queue[str],
    stop_event: threading.Event,
) -> None:
    """Read bytes from `ser`, accumulate into lines on '\\n', enqueue each.

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