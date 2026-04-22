#!/usr/bin/env bash
set -euo pipefail

G=/sys/kernel/config/usb_gadget/janus_personal

if [[ -d "$G" && -f "$G/UDC" ]]; then
    echo "" > "$G/UDC"
fi