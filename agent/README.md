VERSION: Development
NOTE: Current steps for forwarding input from pi-zeros to pi5

PiZeroW2 Setup:
1. Flash OS with RaspberryPi Imager
	RaspberryPi Imager:
		- Select Pi Zero W2
		- Select RaspberryPi OS 64 bit 
		- Select SD card 
			- Leave Exclude System Files checked
		- Input hostname and password
		- Select Region information
		- Select Secure tab
		- Fill in Wifi information
			- Leave hidden SSID unchecked
		- Check Enable SSH
		- Check Password Auth
		- Leave Enable RaspberryPi Connect unchecked
		- Click Submit/Save/Write button
		- Wait for flash/verify steps
		- Click Finish

2. Modify pi-zero settings
	- Put SD card into pi, plug micro-usb into the second port down from edge. (not power only)
	- Let pi boot (wait 60ish seconds)
	- Try and SSH into pi "ssh pi@HOSTNAME"
	- If successfull, run the following 
		sudo nano /boot/firmware/config.txt
	- Add the following to be bottom of the file under [all]
		dtoverlay=dwc2
		dtoverlay=miniuart-bt
	- press "Ctrl+X", then "Y", then "Enter"
	- Then run the following
		sudo nano /boot/firmware/cmdline.txt
	- Add the following after "rootwait" and before "quiet"
		modules-load=dwc2,g_serial
	- also remove "splash" after quiet if there
	- make sure this file remanes one line, no line feeds
	- press "Ctrl+X", then "Y", then "Enter"
	- Then run the following three commands
		sudo systemctl enable serial-getty@ttyGS0.service
		sudo systemctl start serial-getty@ttyGS0.service
		systemctl status serial-getty@ttyGS0.service
	- Should say enabled/available in green in places
	- Then run the following: 
		sudo reboot
	- Wait for reboot, 60ish seconds
	- make sure you can still ssh in
	- run "exit" to leave ssh window
	- run the following 
		sudo systemctl stop serial-getty@ttyGS0.service

3. modify settings on pi5
   - run the following
		sudo nano /boot/firmware/config.txt
   - add the following to the bottom of file under [all]
		dtparam=uart0=on
		dtoverlay=uart2-pi5
	- press "Ctrl+X", then "Y", then "Enter"
	- Then run the following: 
		sudo reboot
	
4. start forwarding by running thse two lines
    sudo stty -F /dev/ttyAMA0 115200 raw -echo -echoe -echok
	sudo sh -c 'cat /dev/ttyGS0 > /dev/ttyAMA0'

5. start receiver on pi5 with the below
	python3 ~/janus/controller/controller.py

6. Run project on pc
	- run dotnet project janus/agent
  
7. should see display/cursor information from pc, forwarded through pi0, received on pi5