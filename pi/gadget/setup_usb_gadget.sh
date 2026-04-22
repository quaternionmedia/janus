#!/usr/bin/env bash
set -euo pipefail

G=/sys/kernel/config/usb_gadget/janus_personal
UDC_NAME="3f980000.usb"

modprobe libcomposite

mkdir -p "$G"
cd "$G"

# Core IDs
echo 0x1d6b > idVendor
echo 0x0104 > idProduct
echo 0x0100 > bcdDevice
echo 0x0200 > bcdUSB
echo 0x02 > bDeviceClass
echo 0x00 > bDeviceSubClass
echo 0x00 > bDeviceProtocol

# Strings
mkdir -p strings/0x409
echo "JANUS-PERSONAL-001" > strings/0x409/serialnumber
echo "Janus" > strings/0x409/manufacturer
echo "Janus Personal Serial" > strings/0x409/product

# Config
mkdir -p configs/c.1
mkdir -p configs/c.1/strings/0x409
echo "CDC Config" > configs/c.1/strings/0x409/configuration
echo 250 > configs/c.1/MaxPower

# ACM function
mkdir -p functions/acm.usb0

# Link function if not already linked
if [[ ! -L configs/c.1/acm.usb0 ]]; then
    ln -s "$G/functions/acm.usb0" "$G/configs/c.1/acm.usb0"
fi

# Bind gadget only if not already bound
if [[ "$(cat UDC 2>/dev/null || true)" != "$UDC_NAME" ]]; then
    echo "$UDC_NAME" > UDC
fi