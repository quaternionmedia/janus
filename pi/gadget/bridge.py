#!/usr/bin/env python3
import os
import select
import signal
import sys
import termios
import time

GADGET_PATH = "/dev/ttyGS0" # this is the default path for the USB gadget  
UART_PATH = "/dev/serial0" # with disabled-bt in config.txt, it frees serial0 to be ttyAMA0 instead of hciuart, which is what we want for this use case

RETRY_DELAY_SECONDS = 1.0
SELECT_TIMEOUT_SECONDS = 0.2
READ_SIZE = 65536

running = True


def handle_signal(signum, frame):
    global running
    running = False


def configure_uart(path: str) -> None:
    fd = os.open(path, os.O_RDWR | os.O_NOCTTY | os.O_NONBLOCK)
    try:
        attrs = termios.tcgetattr(fd)

        attrs[0] = 0  # iflag
        attrs[1] = 0  # oflag
        attrs[2] = attrs[2] | termios.CLOCAL | termios.CREAD
        attrs[2] = attrs[2] & ~termios.PARENB
        attrs[2] = attrs[2] & ~termios.CSTOPB
        attrs[2] = attrs[2] & ~termios.CSIZE
        attrs[2] = attrs[2] | termios.CS8
        attrs[3] = 0  # lflag

        attrs[4] = termios.B921600  # ispeed
        attrs[5] = termios.B921600  # ospeed

        attrs[6][termios.VMIN] = 0
        attrs[6][termios.VTIME] = 1

        termios.tcflush(fd, termios.TCIOFLUSH)
        termios.tcsetattr(fd, termios.TCSANOW, attrs)
    finally:
        os.close(fd)


def open_read_write(path: str):
    return os.open(path, os.O_RDWR | os.O_NOCTTY | os.O_NONBLOCK)


def configure_raw(fd: int) -> None:
    """Force the TTY at `fd` into raw (non-canonical) mode.

    This is critical for the USB gadget endpoint /dev/ttyGS0. When the
    kernel TTY layer is in canonical mode, it line-buffers input and
    caps each line at N_TTY_BUF_SIZE-1 (4095 bytes). Anything longer
    from the USB host gets silently truncated at the first 4095 bytes
    of a line -- devastating for our clipboard protocol which routinely
    sends multi-KB base64 payloads as a single line.

    configure_uart already puts /dev/serial0 into raw mode, so this is
    primarily for the gadget side, but it's safe to call on either.
    """
    attrs = termios.tcgetattr(fd)
    attrs[0] = 0  # iflag
    attrs[1] = 0  # oflag
    attrs[3] = 0  # lflag: NOT canonical, NOT echo, NOT signal processing
    attrs[6][termios.VMIN] = 0
    attrs[6][termios.VTIME] = 1
    termios.tcsetattr(fd, termios.TCSANOW, attrs)


def write_all(fd: int, data: bytes, writable_timeout: float = 0.5) -> None:
    """Write every byte of `data` to `fd`, waiting for writability as needed.

    os.write on a non-blocking fd may write fewer bytes than requested or
    raise BlockingIOError when the downstream buffer is full. Dropping the
    remainder silently corrupts the line stream and has historically crashed
    consumers that parse numeric fields. This loops until the whole buffer
    has been handed off.
    """
    view = memoryview(data)
    while len(view) > 0:
        try:
            written = os.write(fd, view)
        except BlockingIOError:
            written = 0

        if written > 0:
            view = view[written:]
            continue

        # Wait for fd to become writable, then retry.
        _, wfds, _ = select.select([], [fd], [], writable_timeout)
        if not wfds:
            # Still not writable after the timeout. Let the caller decide
            # what to do by raising; the main loop will reopen endpoints.
            raise OSError("write_all timed out waiting for writability")


def close_quietly(fd):
    if fd is None:
        return

    try:
        os.close(fd)
    except Exception:
        pass


def main() -> int:
    signal.signal(signal.SIGINT, handle_signal)
    signal.signal(signal.SIGTERM, handle_signal)

    print("bridge starting")
    print(f"gadget: {GADGET_PATH}")
    print(f"uart:   {UART_PATH}")

    try:
        configure_uart(UART_PATH)
        print(f"configured uart: {UART_PATH} @ 921600")
    except Exception as ex:
        print(f"failed to configure uart {UART_PATH}: {ex}", file=sys.stderr)
        return 1

    gadget_fd = None
    uart_fd = None

    gadget_waiting_logged = False
    uart_waiting_logged = False

    try:
        while running:
            if gadget_fd is None:
                try:
                    gadget_fd = open_read_write(GADGET_PATH)
                    configure_raw(gadget_fd)
                    print(f"gadget connected: {GADGET_PATH}")
                    gadget_waiting_logged = False
                except OSError:
                    if not gadget_waiting_logged:
                        print(f"gadget unavailable, waiting for reconnect: {GADGET_PATH}")
                        gadget_waiting_logged = True
                    time.sleep(RETRY_DELAY_SECONDS)
                    continue

            if uart_fd is None:
                try:
                    uart_fd = open_read_write(UART_PATH)
                    configure_raw(uart_fd)
                    print(f"uart connected: {UART_PATH}")
                    uart_waiting_logged = False
                except OSError:
                    if not uart_waiting_logged:
                        print(f"uart unavailable, waiting for reconnect: {UART_PATH}")
                        uart_waiting_logged = True
                    time.sleep(RETRY_DELAY_SECONDS)
                    continue

            try:
                ready, _, _ = select.select([gadget_fd, uart_fd], [], [], SELECT_TIMEOUT_SECONDS)

                if gadget_fd in ready:
                    chunk = os.read(gadget_fd, READ_SIZE)
                    if chunk:
                        print(f"G->U {len(chunk)} bytes: {chunk!r}")
                        write_all(uart_fd, chunk)

                if uart_fd in ready:
                    chunk = os.read(uart_fd, READ_SIZE)
                    if chunk:
                        print(f"U->G {len(chunk)} bytes: {chunk!r}")
                        write_all(gadget_fd, chunk)

            except OSError as ex:
                print(f"bridge i/o error, reopening endpoints: {ex}")
                close_quietly(gadget_fd)
                close_quietly(uart_fd)
                gadget_fd = None
                uart_fd = None
                time.sleep(RETRY_DELAY_SECONDS)

    finally:
        close_quietly(gadget_fd)
        close_quietly(uart_fd)
        print("bridge stopping")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())