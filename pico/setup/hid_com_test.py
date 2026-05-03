# Janus stage 2 verification.
#
# Tests the three things boot.py just configured:
#
#   1. HID keyboard still works (types "stage2 ok" once at startup).
#   2. HID mouse works (nudges the cursor 10 pixels right then back, once).
#   3. CDC data endpoint echoes anything received back with "ECHO: " prefix.
#
# After running this you should be able to:
#   - See "stage2 ok" appear typed in a focused text field a few seconds
#     after the Pico enumerates.
#   - See the mouse jitter once.
#   - Open the second COM port (the one labeled "USB Serial Device" or
#     similar in Device Manager, NOT the CircuitPython console one) in
#     PuTTY/serial terminal at any baud rate, type characters, and see
#     them echoed back prefixed with "ECHO: ".
#
# If all three work, USB descriptors are correct and we're ready for
# stage 3 (UART pass-through to the Pi 5).

import time
import usb_cdc
import usb_hid
from adafruit_hid.keyboard import Keyboard
from adafruit_hid.keyboard_layout_us import KeyboardLayoutUS
from adafruit_hid.mouse import Mouse


STARTUP_DELAY_SECONDS = 5


def smoke_test_keyboard():
    keyboard = Keyboard(usb_hid.devices)
    layout = KeyboardLayoutUS(keyboard)

    print(f"keyboard test: waiting {STARTUP_DELAY_SECONDS}s before typing")
    time.sleep(STARTUP_DELAY_SECONDS)

    print("keyboard test: typing 'stage2 ok'")
    layout.write("stage2 ok")


def smoke_test_mouse():
    mouse = Mouse(usb_hid.devices)
    print("mouse test: nudging cursor right 10px, then back")
    mouse.move(x=10)
    time.sleep(0.2)
    mouse.move(x=-10)


def cdc_echo_loop():
    """Echo bytes received on the CDC data endpoint back to the host.

    `usb_cdc.data` is the second CDC endpoint we enabled in boot.py.
    This is the channel the agent will eventually use; for now it just
    echoes so we can confirm the host can talk to it.
    """
    data = usb_cdc.data
    if data is None:
        print("cdc test: usb_cdc.data is None -- boot.py did not enable it")
        return

    print("cdc test: ready -- send characters to the data COM port")

    while True:
        if data.in_waiting > 0:
            chunk = data.read(data.in_waiting)
            response = b"ECHO: " + chunk + b"\n"
            data.write(response)
        time.sleep(0.01)


def main():
    smoke_test_keyboard()
    smoke_test_mouse()
    cdc_echo_loop()


main()