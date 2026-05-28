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
#   2. HID interfaces. We expose a Keyboard and a Mouse so the controller
#      can drive both via HID reports. ConsumerControl (media keys, volume,
#      etc.) is intentionally omitted -- our wire protocol doesn't carry
#      those, and dropping the interface keeps the descriptor simpler.
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
#
# These strings appear in Device Manager and `lsusb` style tools. They
# are purely cosmetic -- Windows uses the standard built-in HID class
# drivers regardless of what we put here. The `serial_number` is what
# Windows uses to remember per-port settings (e.g. "this device should
# always get COM10"), so it's worth giving each Pico a stable, unique
# serial. Change "P" to "W" on the work Pico.

supervisor.set_usb_identification(
    manufacturer="Janus",
    product="Janus HID Bridge",
    vid=0x239A,                # Adafruit-assigned VID; safe for hobby use
    pid=0xCAFE,                # arbitrary PID we picked; unique within Janus
)

# CircuitPython generates a per-chip serial number automatically based on
# the RP2350's hardware UID, so each Pico already presents a unique
# serial to Windows without us needing to set one. Windows uses this
# automatic value for COM port stickiness.


# -----------------------------------------------------------------------
# 2. HID interfaces: keyboard + mouse only
# -----------------------------------------------------------------------
#
# usb_hid.enable() takes a tuple of HID device descriptors. The standard
# CircuitPython "boot keyboard" + "boot mouse" descriptors give us
# everything our wire protocol needs and nothing it doesn't.

usb_hid.enable(
    (
        usb_hid.Device.KEYBOARD,
        usb_hid.Device.MOUSE,
    )
)


# -----------------------------------------------------------------------
# 3. CDC ACM serial endpoint
# -----------------------------------------------------------------------
#
# `data=True` enables a second CDC endpoint (the "data" channel). The
# first CDC, which CircuitPython exposes by default, is the REPL/console;
# we leave that on for development convenience. The second is what our
# agent connects to for clipboard/display traffic.
#
# On Windows these will appear as TWO COM ports. The data port is the
# higher-numbered one; the console is the lower. We'll surface them
# distinctly in Device Manager via their friendly names (Windows derives
# those from the interface descriptors automatically).

usb_cdc.enable(console=True, data=True)


# -----------------------------------------------------------------------
# 4. CIRCUITPY drive
# -----------------------------------------------------------------------
#
# The mass-storage drive is on by default. We leave it enabled during
# development for easy file edits. To harden a production deployment
# (read-only Pico that ignores attempts to modify code.py), uncomment:
#
#     storage.disable_usb_drive()
#
# Don't enable that until you're ready to commit -- once disabled, the
# only way back is to re-flash CircuitPython via BOOTSEL and erase
# everything. Keep flexibility while we're iterating.