# Janus stage 3: UART <-> CDC bridge.
#
# The Pico now functions as a transparent byte pipe, replacing the
# Pi Zero's bridge.py role:
#
#     PC <-> [USB CDC data] <-> [Pico] <-> [UART0] <-> Pi 5 controller
#
# Bytes that arrive on either side are forwarded to the other as soon
# as they're seen. There is NO protocol parsing here -- this code is
# byte-blind. That keeps stage 3 simple and lets us swap the Pico in
# for a Pi Zero with zero changes to the controller or agent.
#
# Stage 4 will add HID interpretation: when a "MOUSE MOVE" line comes
# in from UART, the Pico will both forward it to the PC's CDC (so the
# agent can ignore it) AND emit the corresponding HID report. That's
# next; not yet.
#
# Wiring (see boot.py for context):
#   Pico GP0 (UART0 TX) -> Pi 5 GPIO 15 (RXD0), physical pin 10
#   Pico GP1 (UART0 RX) -> Pi 5 GPIO 14 (TXD0), physical pin 8
#   Pico GND            -> Pi 5 GND, any GND pin

import board
import busio
import usb_cdc


# Must match the controller's setting. Anything else and you get garbage.
UART_BAUD = 921_600

# Read sizes. Bigger reduces per-iteration overhead; we just need enough
# to drain whichever side has bursty traffic. 4 KB is comfortably more
# than a typical clipboard chunk arrives in via USB, and a comfortable
# fraction of UART throughput per loop iteration.
UART_READ_SIZE = 4096
CDC_READ_SIZE = 4096


def main():
    if usb_cdc.data is None:
        # boot.py didn't enable the second CDC; without it we have
        # nothing to bridge to. Bail loudly.
        print("ERROR: usb_cdc.data is None; check boot.py")
        while True:
            pass

    cdc = usb_cdc.data
    # Match agent and controller behavior: never block on read indefinitely;
    # we want the loop to flip between sides quickly.
    cdc.timeout = 0
    cdc.write_timeout = 0

    # UART0 on GP0/GP1. timeout=0 makes uart.read() non-blocking;
    # combined with in_waiting checks below, we never sit waiting.
    uart = busio.UART(
        tx=board.GP0,
        rx=board.GP1,
        baudrate=UART_BAUD,
        timeout=0,
        # Receive buffer big enough to hold a full clipboard line plus
        # plenty of margin. Prevents UART overrun when the controller
        # ships a large payload faster than the loop drains it.
        # CircuitPython caps this at 65535 (one less than 64 KB).
        receiver_buffer_size=65535,
    )

    print(f"bridge ready: UART0 @ {UART_BAUD} <-> CDC data")

    while True:
        # PC -> Pi 5: drain CDC data endpoint into UART
        cdc_pending = cdc.in_waiting
        if cdc_pending > 0:
            chunk = cdc.read(min(cdc_pending, CDC_READ_SIZE))
            if chunk:
                uart.write(chunk)

        # Pi 5 -> PC: drain UART into CDC data endpoint
        uart_pending = uart.in_waiting
        if uart_pending > 0:
            chunk = uart.read(min(uart_pending, UART_READ_SIZE))
            if chunk:
                cdc.write(chunk)

        # No sleep here. CircuitPython yields to USB and other internals
        # implicitly between iterations. Adding sleep(0) or similar would
        # only add latency. The two in_waiting checks are fast enough
        # that this loop runs thousands of times per second when idle.


main()