# Janus boot.py
#
# Runs once at CircuitPython startup, BEFORE USB is brought up. This is
# the only place where USB descriptors -- the things that determine how
# the PC sees this device -- can be configured.
#
# What we configure:
#
#   1. USB identity strings (manufacturer / product / serial number).
#      Determines the label that appears in Device Manager.
#
#   2. HID interfaces. We expose a Keyboard, a custom Mouse (matching
#      the Razer Basilisk V3's exact descriptor layout), and a
#      ConsumerControl so the controller can drive all three via HID
#      reports.
#
#   3. CDC ACM serial endpoint. The PC sees this as a virtual COM port
#      (e.g. COM10 on Windows). The agent on the PC opens this port to
#      send/receive clipboard, display, and target messages -- the same
#      role /dev/ttyGS0 used to play on the Pi Zeros.
#
# After saving this file the Pico has to FULLY POWER-CYCLE before the
# changes take effect (a soft reset isn't enough -- USB has to fully
# tear down and re-enumerate). Unplug, replug.

import storage
import supervisor
import usb_cdc
import usb_hid


# -----------------------------------------------------------------------
# 1. USB identity
# -----------------------------------------------------------------------

supervisor.set_usb_identification(
    manufacturer="Janus",
    product="Janus HID Bridge",
    vid=0x239A,                # Adafruit-assigned VID; safe for hobby use
    pid=0xCAFE,                # arbitrary PID we picked; unique within Janus
    
    # vid=0x1532, # Razer's VID; DO NOT USE THIS (requires special permissions on Windows)
    # pid=0x0099  # Razer Basilisk V3's PID; DO NOT USE THIS (requires special permissions on Windows
)


# -----------------------------------------------------------------------
# 2. HID interfaces: keyboard + custom mouse + consumer control
# -----------------------------------------------------------------------
#
# Custom mouse descriptor matching the Razer Basilisk V3's main mouse
# interface exactly (dumped from /sys/kernel/debug/hid/.../rdesc):
#
#   - 5 buttons (1 bit each)
#   - 11 bits padding (3 to byte-align buttons + 8 bytes empty)
#   - AC Pan: int8 (horizontal scroll, Consumer page)
#   - Wheel:  int8 (vertical scroll, Generic Desktop)
#   - X, Y:   int16 each
#
# Field order (Pan before Wheel) and types (int8 for both wheels)
# mirror the Razer exactly. Total Input report: 8 bytes.
#
# Input report layout (8 bytes, report ID 2):
#   [0]   buttons: bit0=L, bit1=R, bit2=M, bit3=back, bit4=fwd + 3 pad
#   [1]   padding (8 bits)
#   [2]   pan: int8 (horizontal scroll)
#   [3]   wheel: int8 (vertical scroll)
#   [4:6] X: int16 LE
#   [6:8] Y: int16 LE

_CUSTOM_MOUSE_DESCRIPTOR = bytes([
    0x05, 0x01,        # Usage Page (Generic Desktop)
    0x09, 0x02,        # Usage (Mouse)
    0xA1, 0x01,        # Collection (Application)
    0x85, 0x02,        #   Report ID (2)
    0x09, 0x01,        #   Usage (Pointer)
    0xA1, 0x00,        #   Collection (Physical)

    # -- 5 buttons --
    0x05, 0x09,        #     Usage Page (Button)
    0x19, 0x01,        #     Usage Minimum (1)
    0x29, 0x05,        #     Usage Maximum (5)
    0x15, 0x00,        #     Logical Minimum (0)
    0x25, 0x01,        #     Logical Maximum (1)
    0x95, 0x05,        #     Report Count (5)
    0x75, 0x01,        #     Report Size (1)
    0x81, 0x02,        #     Input (Data, Variable, Absolute)

    # 3 bits padding (round button byte to 8 bits)
    0x95, 0x01,        #     Report Count (1)
    0x75, 0x03,        #     Report Size (3)
    0x81, 0x01,        #     Input (Constant)

    # 8 bits padding (full empty byte, matching Razer's layout)
    0x95, 0x01,        #     Report Count (1)
    0x75, 0x08,        #     Report Size (8)
    0x81, 0x01,        #     Input (Constant)

    # -- AC Pan (horizontal scroll, int8) --
    0x05, 0x0C,        #     Usage Page (Consumer)
    0x0A, 0x38, 0x02,  #     Usage (AC Pan)
    0x15, 0x81,        #     Logical Minimum (-127)
    0x25, 0x7F,        #     Logical Maximum (127)
    0x95, 0x01,        #     Report Count (1)
    0x75, 0x08,        #     Report Size (8)
    0x81, 0x06,        #     Input (Data, Variable, Relative)

    # -- Wheel (vertical scroll, int8) --
    0x05, 0x01,        #     Usage Page (Generic Desktop)
    0x09, 0x38,        #     Usage (Wheel)
    0x15, 0x81,        #     Logical Minimum (-127)
    0x25, 0x7F,        #     Logical Maximum (127)
    0x95, 0x01,        #     Report Count (1)
    0x75, 0x08,        #     Report Size (8)
    0x81, 0x06,        #     Input (Data, Variable, Relative)

    # -- X, Y: 16-bit signed relative --
    0x09, 0x30,        #     Usage (X)
    0x09, 0x31,        #     Usage (Y)
    0x16, 0x00, 0x80,  #     Logical Minimum (-32768)
    0x26, 0xFF, 0x7F,  #     Logical Maximum (32767)
    0x95, 0x02,        #     Report Count (2)
    0x75, 0x10,        #     Report Size (16)
    0x81, 0x06,        #     Input (Data, Variable, Relative)

    0xC0,              #   End Collection (Physical)
    0xC0,              # End Collection (Application)
])

_custom_mouse = usb_hid.Device(
    report_descriptor=_CUSTOM_MOUSE_DESCRIPTOR,
    usage_page=0x01,        # Generic Desktop
    usage=0x02,             # Mouse
    report_ids=(2,),
    in_report_lengths=(8,), # 8-byte input report
    out_report_lengths=(0,),
)

usb_hid.enable(
    (
        usb_hid.Device.KEYBOARD,
        _custom_mouse,
        usb_hid.Device.CONSUMER_CONTROL,
    )
)


# -----------------------------------------------------------------------
# 3. CDC ACM serial endpoint
# -----------------------------------------------------------------------

usb_cdc.enable(console=True, data=True)


# -----------------------------------------------------------------------
# 4. CIRCUITPY drive
# -----------------------------------------------------------------------
#
# To harden a production deployment, uncomment:
#     storage.disable_usb_drive()