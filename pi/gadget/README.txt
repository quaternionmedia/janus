converting g_serial to configfs instructions

remove g_serial from cmdline on pi if it's there
	sudo nano /boot/firmware/cmdline.txt

create the setup script
	mkdir -p ~/janus/bridge
	nano ~/janus/bridge/setup_usb_gadget.sh

In this file we'll define some unique ids, JANUS-WORK-001 and Janus Work Serial. 
Change these to whatever you want. 
paste this into setup script and save file
	#!/usr/bin/env bash
	set -euo pipefail

	G=/sys/kernel/config/usb_gadget/janus_work
	UDC_NAME="3f980000.usb"

	modprobe libcomposite

	mkdir -p "$G"
	cd "$G"

	# Core IDs (different product for work)
	echo 0x1d6b > idVendor
	echo 0x0105 > idProduct
	echo 0x0100 > bcdDevice
	echo 0x0200 > bcdUSB
	echo 0x02 > bDeviceClass
	echo 0x00 > bDeviceSubClass
	echo 0x00 > bDeviceProtocol

	# Strings (unique identity)
	mkdir -p strings/0x409
	echo "JANUS-WORK-001" > strings/0x409/serialnumber
	echo "Janus" > strings/0x409/manufacturer
	echo "Janus Work Serial" > strings/0x409/product

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

create the teardown script
	nano ~/janus/bridge/teardown_usb_gadget.sh

paste this into it
	#!/usr/bin/env bash
	set -euo pipefail

	G=/sys/kernel/config/usb_gadget/janus_work

	if [[ -d "$G" && -f "$G/UDC" ]]; then
		echo "" > "$G/UDC"
	fi

make both executable
	chmod +x ~/janus/bridge/setup_usb_gadget.sh
	chmod +x ~/janus/bridge/teardown_usb_gadget.sh

create the gadget service
	sudo nano /etc/systemd/system/janus-usb-gadget.service

paste this into it and save file
	[Unit]
	Description=Janus USB ACM gadget
	DefaultDependencies=no
	After=local-fs.target sys-kernel-config.mount
	Wants=sys-kernel-config.mount

	[Service]
	Type=oneshot
	RemainAfterExit=yes
	ExecStart=/home/pi/janus/bridge/setup_usb_gadget.sh
	ExecStop=/home/pi/janus/bridge/teardown_usb_gadget.sh

	[Install]
	WantedBy=multi-user.target

create the bridge serivce
	sudo nano /etc/systemd/system/janus-bridge.service

paste this into it and save file 
	[Unit]
	Description=Janus UART <-> USB bridge
	Requires=janus-usb-gadget.service
	After=janus-usb-gadget.service

	[Service]
	Type=simple
	ExecStart=/usr/bin/python3 /home/pi/janus/bridge/bridge.py
	Restart=always
	RestartSec=1
	User=root

	[Install]
	WantedBy=multi-user.target

reboot the pi
	sudo reboot

Services are not setup to run on boot yet. Start them manually 
	sudo systemctl daemon-reload
	sudo systemctl start janus-usb-gadget.service
	sudo systemctl start janus-bridge.service

then on windows machine check you see the expected device id and which COM it's under
	Get-CimInstance Win32_SerialPort | Select-Object DeviceID, Name, PNPDeviceID, Status

Use the same COM number and check you can open serial
	mode COMx
	$p = New-Object System.IO.Ports.SerialPort COMx,115200
	$p.Open()
	"OPEN OK"
	$p.Close()

if all that succeeds, enable the services so they start on boot
	sudo systemctl enable janus-usb-gadget.service
	sudo systemctl enable janus-bridge.service


