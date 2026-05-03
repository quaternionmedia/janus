# Janus stage 1 smoke test.
#
# When the Pico boots, it waits a few seconds, then types hello janus
# wherever the PC's keyboard cursor is currently focused.
#
# Purpose confirm that the Pico is enumerating as a USB HID keyboard
# and that adafruit_hid can drive it. This proves the toolchain end-to-end
# before we touch any of the actual project code.
#
# How to test
#   1. Open Notepad (or any text field) on the PC to have ready for test.
#   2. Save this files content as code.py on the CIRCUITPY drive.
#   3. The Pico will reboot automatically when the file is saved.
#   4. Click into the Notepad (or text field) so the keyboard cursor is in that field.
#   5. Wait. After ~5 seconds you should see hello janus typed.

import time
import usb_hid
from adafruit_hid.keyboard import Keyboard
from adafruit_hid.keyboard_layout_us import KeyboardLayoutUS


# Give the user a moment to focus a text field after the Pico reboots.
# Without this delay, the keystrokes go to whatever was focused at the
# instant the device enumerated -- often the IDE or File Explorer.
STARTUP_DELAY_SECONDS = 5

 
def main()
    # Initialize the HID keyboard interface. usb_hid.devices is the list
    # of HID interfaces declared in boot.py; the default CircuitPython
    # boot.py exposes a keyboard, mouse, and consumer-control device.
    keyboard = Keyboard(usb_hid.devices)
    layout = KeyboardLayoutUS(keyboard)

    print(fsmoke test waiting {STARTUP_DELAY_SECONDS}s before typing)
    time.sleep(STARTUP_DELAY_SECONDS)

    print(smoke test typing 'hello janus')
    layout.write(hello janus)

    print(smoke test done)


main()