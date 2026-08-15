# Janus

An air-gapped KVM (keyboard/mouse switch) for sharing one keyboard and mouse
between two PCs — Personal (P) and Work (W) — without ever creating a network
or USB path between them.

```
Keyboard + Mouse
       │
       ▼
   Raspberry Pi 5  ──── Python controller (janus-controller)
       │
       │ UART @ 921600, one line per PC
       ├──────────────┐
       ▼              ▼
   RP2350 Pico    RP2350 Pico   ──── CircuitPython (boot.py + code.py)
       │              │
       │ USB          │ USB       HID (kbd + mouse + consumer)
       │              │           CDC virtual COM
       ▼              ▼
   Personal PC    Work PC        ──── Janus.Agent (.NET 9, WPF)
```

The Pi is the brain — it grabs raw evdev events from the keyboard and mouse
and decides which Pico to send them to. Each Pico is a dumb bridge: it
translates the Pi's serial lines into HID reports the PC sees as coming from
a real keyboard and mouse, and forwards non-HID traffic (clipboard, cursor,
display info) over a virtual COM to the Windows agent on that PC.

The two PCs never share a physical wire.

---

## Contents

1. [Hardware bill of materials](#hardware-bill-of-materials)
2. [Wiring](#wiring)
3. [Raspberry Pi 5 setup](#raspberry-pi-5-setup)
4. [Pico setup (do this twice, once per PC)](#pico-setup)
5. [Windows agent setup (do this on both PCs)](#windows-agent-setup)
6. [First-run smoke test](#first-run-smoke-test)
7. [Configuration reference](#configuration-reference)
8. [Common tasks](#common-tasks)

---

## Hardware bill of materials

- 1× Raspberry Pi 5 (any RAM tier) + power supply + SD card
- 2× RP2350 Pico 2 (non-wireless variants) with soldered headers
- 2× USB-A → micro-USB cables (Pico ↔ PC)
- 6× female-female jumper wires (TX, RX, GND × 2 pairs)
- 1× wired USB keyboard
- 1× wired USB mouse
- 2× Windows 11 PCs

Wireless Picos are avoided deliberately — the Work PC is under an air-gap
security requirement, and a wireless microcontroller sitting on that PC's USB
bus is a policy problem even if you never enable the radio.

---

## Wiring

Three wires per Pico. Two Picos, so six wires total.

### Pico side (confirmed from firmware)

Each Pico uses **UART0** on `GP0` (TX) and `GP1` (RX). Ground is any GND pin.

```
Pico            Pi 5
GP0  (TX)  ───► RX   (of the UART assigned to this Pico)
GP1  (RX)  ◄─── TX   (of the UART assigned to this Pico)
GND        ─── GND
```

TX and RX cross over. That's the whole rule.

### Pi 5 side (verify against your config.txt)

The controller expects two hardware UARTs at these device paths:

| Purpose        | Device path       |
|----------------|-------------------|
| Personal Pico  | `/dev/ttyAMA0`    |
| Work Pico      | `/dev/ttyAMA2`    |

`/dev/ttyAMA0` is the Pi 5's primary UART, on **GPIO 14 / TXD0 (pin 8)** and
**GPIO 15 / RXD0 (pin 10)**. `/dev/ttyAMA2` requires a `dtoverlay=uart2`
line in `/boot/firmware/config.txt`, which puts UART2 on **GPIO 4 / TXD2
(pin 7)** and **GPIO 5 / RXD2 (pin 29)**. GND is any ground pin — 6, 9,
14, 20, 25, 30, 34, and 39 all work.

Full mapping, matching the Pi 5 → Pico wiring:

| PC       | Pi 5 UART | Pi 5 TX (→ Pico RX) | Pi 5 RX (← Pico TX) |
|----------|-----------|---------------------|---------------------|
| Personal | UART0     | pin 8  (GPIO 14)    | pin 10 (GPIO 15)    |
| Work     | UART2     | pin 7  (GPIO 4)     | pin 29 (GPIO 5)     |

### Hot-plugging

The Pico↔Pi UART link is safe to disconnect and reconnect with everything
powered on, provided you follow ground-order discipline:

- **Disconnecting:** signal wires first, GND last.
- **Reconnecting:** GND first, signal wires after.

The rule exists because the Pico is powered by its PC and the Pi has its own
supply — if the TX/RX pins are linked while ground isn't, current sneaks
through the GPIO protection diodes. Do it in the right order and it's fine
indefinitely.

---

## Raspberry Pi 5 setup

### 1. OS install

Flash Raspberry Pi OS Lite (64-bit, Bookworm or newer) to an SD card using
Raspberry Pi Imager. The controller is headless, so Lite is all you need.

During imager setup, configure:
- Hostname: whatever you like
- User: `pi` (the systemd unit expects this user; if you use a different
  name, edit `janus-controller.service` before installing it)
- SSH: enabled
- Wi-Fi / locale: your call

### 2. First boot

SSH in and update:

```bash
sudo apt update && sudo apt full-upgrade -y
sudo reboot
```

### 3. Enable both UARTs

Edit `/boot/firmware/config.txt` and confirm/add:

```
enable_uart=1
dtoverlay=uart2
```

Then disable the serial login shell so it doesn't fight the controller for
`/dev/ttyAMA0`:

```bash
sudo raspi-config
# Interface Options → Serial Port
# "login shell over serial?"     → No
# "serial port hardware enabled?" → Yes
```

Reboot and confirm both devices exist:

```bash
ls -l /dev/ttyAMA0 /dev/ttyAMA2
```

After running raspi-config, sanity-check that the kernel isn't still grabbing the UART for its console:

```bash
cat /boot/firmware/cmdline.txt
```

If you see `console=serial0,115200` (or `console=ttyAMA0,115200`) anywhere in that line, remove that fragment. Edit the file with `sudo nano /boot/firmware/cmdline.txt` — keep it a single line, don't add newlines — and reboot. 
Any other `console=` (like `console=tty1`) is fine and should stay.

### 4. Group membership

The controller needs to read `/dev/input/event*` (via the `input` group) and
read/write the UARTs (via `dialout`):

```bash
sudo usermod -a -G input,dialout pi
```

Log out and back in for group changes to take effect.

### 5. Install uv

The controller runs under [uv](https://github.com/astral-sh/uv), which
handles the venv and Python dependencies on demand:

```bash
curl -LsSf https://astral.sh/uv/install.sh | sh
```

### 6. Clone the repo and place the controller

The systemd unit expects the controller at `/home/pi/janus/controller`:

```bash
mkdir -p /home/pi/janus
cd /home/pi/janus
git clone <your-repo-url> controller
# or copy the controller/ directory from your dev box
```

### 7. Identify your input devices

Plug in your keyboard and mouse, then find their stable by-id paths:

```bash
ls /dev/input/by-id/
```

Look for names like `usb-Razer_Razer_Basilisk_V3-event-mouse` and
`usb-SteelSeries_SteelSeries_Apex_Gaming_Keyboard-event-if02`. Copy the full
paths — you'll paste them into a device profile in the next step.

Some devices (especially gaming mice with configurable buttons) expose more
than one input node. If a button doesn't come through, check
`controller/profiles/razer_steelseries.yaml` for the pattern: that mouse
needs both its `event-mouse` node (for motion/wheel) **and** its
`if02-event-kbd` node (for macro buttons) listed.

You can also run `controller/capture_events.py <path>` on any node to see
what events it actually emits.

### 8. Pick or create a device profile

`controller/profiles/` ships with:

- `logitech_k400.yaml` — Logitech K400+ combo receiver
- `razer_steelseries.yaml` — Razer Basilisk V3 + SteelSeries Apex

To use one of these, edit `controller/config.yaml`:

```yaml
device_config_file: "razer_steelseries.yaml"
```

To add a new setup, copy an existing profile, replace the paths, and point
`device_config_file` at your new filename.

#### For tips on setting up your keyboard+mouse setup see `Add a new keyboard or mouse` and `Debug an input problem` sections below. Also item 7 above mentions event capture python to help configure.

Each profile also declares the UARTs:

```yaml
devices:
  personal_uart: /dev/ttyAMA0
  work_uart: /dev/ttyAMA2
```

Swap these if your Personal and Work Picos are on the other UARTs than what
the profile expects.

### 9. Install and start the service

```bash
sudo cp /home/pi/janus/controller/janus-controller.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now janus-controller
```

Verify:

```bash
sudo systemctl status janus-controller
sudo journalctl -u janus-controller -f
```

You should see the controller open both UARTs and log the devices it grabbed.

---

## Pico setup

Do this twice, once per Pico. Both Picos run identical firmware — the Pi
decides which PC each one talks to based on which UART it's wired to.

Everything you need lives in `bridge/` in this repo:

```
bridge/
├── adafruit-circuitpython-raspberry_pi_pico2-en_US-10.2.0.uf2
├── adafruit_hid/
├── boot.py
└── code.py
```

The UF2 and the `adafruit_hid/` library are vendored deliberately — pinning
to the exact versions this project was validated against avoids
CircuitPython version-drift surprises, and makes a fresh Pico setup a
pure file-copy with no external downloads.

### 1. Flash CircuitPython onto the Pico

1. Unplug the Pico.
2. Hold the BOOTSEL button.
3. Plug the USB cable into a PC while still holding BOOTSEL.
4. After ~1 second, release BOOTSEL.

A drive named `RP2350` appears. Drag
`bridge/adafruit-circuitpython-raspberry_pi_pico2-en_US-10.2.0.uf2` onto
it. The drive disappears, the Pico reboots, and a `CIRCUITPY` drive
appears in its place.

Verify the flash by opening `boot_out.txt` on `CIRCUITPY` — it should
name the Pico 2 and CircuitPython 10.2.0.

### 2. Install the adafruit_hid library

Copy the entire `bridge/adafruit_hid/` folder into `CIRCUITPY/lib/`.

### 3. Deploy the firmware

Copy `bridge/boot.py` and `bridge/code.py` to the root of `CIRCUITPY/`.

When done:

```
CIRCUITPY/
├── boot.py
├── code.py
└── lib/
    └── adafruit_hid/
```

### 4. Power-cycle the Pico

`boot.py` sets up USB descriptors (HID interfaces + virtual COM), which can
only take effect after a **full USB power cycle** — a soft reset is not
enough. Unplug the Pico, wait a few seconds, plug it back in.

### 5. Confirm it enumerated correctly

On the PC that owns this Pico, Device Manager should show:

- HID → keyboard, mouse, consumer control (all named "Janus HID Bridge")
- Ports (COM & LPT) → **two** new `USB Serial Device` entries. One is the
  CircuitPython REPL console; the other is the data endpoint the agent
  connects to.

To tell them apart: right-click each new COM port → Properties → Details →
"Bus reported device description." The one labelled **"CircuitPython CDC2
control"** is the data port. That's the COM number you pass to the agent.

If you don't see two new COMs, the descriptor didn't apply — verify
`boot.py` copied correctly and power-cycle again.

### Filesystem gotchas

- If saving `boot.py` or `code.py` throws a write-protected error,
  CircuitPython has the filesystem locked. Unplug the Pico for ~3 seconds
  and reconnect.
- Never open `code.py` in a REPL and edit at the same time. Save from your
  editor, then close and let auto-reload happen.

---

## Windows agent setup

Do this on **both** PCs. Each PC gets its own copy of the agent and its own
`appsettings.json`.

### 1. Prerequisites

- Windows 10 (recent) or Windows 11
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
  (x64) — or the .NET 9 SDK if you're building from source

### 2. Get the code

Clone or copy the repo to each PC.

### 3. Build

From the `agent/` directory (parent of `Janus.Agent/`):

```
dotnet build
```

or for a release build:

```
dotnet build -c Release
```

Confirm the output folder contains `Janus.Agent.exe`,
`Janus.Agent.runtimeconfig.json`, and `appsettings.json`. **All three must
be present** — if `runtimeconfig.json` is missing, the exe will fail with a
misleading "install .NET Desktop Runtime" dialog.

### 4. Configure appsettings.json

The agent reads `appsettings.json` from **the folder it launches from**
(i.e., next to the exe in `bin\Debug\` or `bin\Release\`). MSBuild copies
the project's `appsettings.json` to that folder on every build.

The two PCs need different settings — see the [switch
triggers](#configuration-reference) section below. Copy the shipped
appsettings.json as-is on the primary PC (typically Personal).
On the secondary PC (typically Work), set OnLock: true and 
OnShutdown: true so locking or shutting down the secondary 
automatically bounces input back to the primary.

### 5. First manual launch

From a PowerShell prompt on this PC:

```powershell
& 'C:\path\to\Janus.Agent\bin\Debug\net9.0-windows\Janus.Agent.exe' P COM9
```

Positional arguments:
1. **Device ID:** `P` (Personal) or `W` (Work). This identifies THIS PC to
   the controller, not the peer.
2. **COM port:** the virtual COM created by this PC's Pico. Look it up in
   Device Manager under "Ports (COM & LPT)".

Both are required — running the exe with no args prints an error and exits.

You should see the tray icon appear (Janus glyph). Right-click it → Show
window to open the log view. If serial connects, the status dot goes green
and you'll see `Serial connected: COMx` in the log.

### 6. Auto-start on login (scheduled task)

The agent runs in your interactive session (clipboard APIs and global
hotkeys require a real desktop, not Session 0). The included installer
registers a Task Scheduler task that launches on user login:

```powershell
cd C:\path\to\Janus.Agent
.\install-scheduled-task.ps1 -Side P -Port COM9
```

On the Work PC:

```powershell
.\install-scheduled-task.ps1 -Side W -Port COM7
```

To trigger it manually without logging out:

```powershell
Start-ScheduledTask -TaskName 'Janus.Agent (Personal)'
```

There's a `start-agent.bat` in the project root that wraps this
`schtasks /Run` call — double-click to launch, use as a Start Menu pin.

To remove it:

```powershell
.\install-scheduled-task.ps1 -Side P -Uninstall
```

---

## First-run smoke test

With both agents running and both Picos wired to the Pi:

1. **Type something on the Personal PC.** If input lands, mouse and
   keyboard forwarding works.
2. **Press `Right Ctrl + Right Alt + W`.** Input should switch to the Work
   PC. This is the Pi-side force-switch hotkey — it works even if agents
   are offline.
3. **Press `Right Ctrl + Right Alt + P`.** Back to Personal.
4. **Copy text on Personal, press `Ctrl + Shift + C`** (default clipboard
   push hotkey). Paste on Work — the text should be there.
5. **Lock Personal (`Win + L`) with `SwitchOnLock: true`** in that agent's
   config. Input should bounce to Work automatically.

If any of these fail, `journalctl -u janus-controller -f` on the Pi and the
agent's log window on each PC will show what's happening.

---

## Configuration reference

Three config files control everything.

- **`controller/config.yaml`** (Pi): switching behaviour, dead-peer
  detection, terminal commands, verbose logging.
- **`controller/profiles/<name>.yaml`** (Pi): device paths for one hardware
  setup. `config.yaml` names the active profile.
- **`Janus.Agent/appsettings.json`** (each PC, independently): clipboard
  behaviour, switch triggers, hotkeys.

### The important ones (Pi: config.yaml)

**`switching.auto_edge_switch_enabled`** *(default: false)* — When true, the
controller auto-switches when the cursor pushes against the source
monitor's edge. False (default) means you switch only via the tray, the
hotkey, or the Pi's terminal.

**`switching.switch_push_pixels`** *(default: 12)* — With auto-edge
switching on, how much accumulated push is required at the edge before it
fires. Device-sensitive: a high-DPI mouse generates far more units per
inch than a trackpad, so tune this per hardware.

**`switching.dead_peer_home_base`** *(default: "P")* — Which side is
"home." If the active PC's agent goes silent for `dead_peer_threshold_seconds`
(default 10), the controller auto-switches to the peer — but only if the
peer is not the home base. Set to `null` to disable dead-peer auto-switch
entirely.

**`device_config_file`** — Which profile in `profiles/` is active. Change
this one line to swap hardware setups.

### Everything else (Pi: config.yaml)

`serial.baud` (921600 — must match Picos and agents), `switching.edge_arm_pixels`
(2), `switching.switch_entry_margin_y` (32), `switching.switch_cooldown_seconds`
(0.15), `switching.target_announce_interval_seconds` (2.0),
`switching.dead_peer_threshold_seconds` (10.0),
`switching.dead_peer_switch_cooldown_seconds` (30.0), `commands.personal` (p),
`commands.work` (w), `commands.clipboard` (c), `commands.quit` (q),
`logging.verbose` (false — flip to true when debugging input issues, else
leave off; it fills the log with every mouse move).

### The important ones (agent: appsettings.json)

**`Clipboard.OutboundMode`** *(default: "Manual")* — `Manual` means your
clipboard stays local until you explicitly push it (hotkey, tray action, or
the Pi's `c` command). `Auto` broadcasts every clipboard change to the peer
automatically. Manual is the recommended default, especially on the Work PC
where auto-broadcast of anything you copy is a policy concern.

**`Switch.OnLock`** *(default: false)* — When true, locking this PC (Win+L,
idle lock, Ctrl+Alt+Del → Lock) auto-switches input to the peer. Enable on
your secondary PC, disable on your primary. Otherwise, locking Personal
sends input to Work — probably not what you want.

**`Switch.OnShutdown`** *(default: false)* — Same idea, but for shutdown,
restart, logoff, sleep, hibernate. The agent has a few seconds during these
events to fire the switch before Windows kills it. Enable on secondary,
disable on primary.

**`Switch.HotkeyEnabled` + `HotkeyCtrl/Shift/Alt/Key`** *(default:
Shift+Alt+S)* — Global hotkey to switch to the peer. Works from any focused
window. Change if it collides with something you use.

**`Clipboard.Push.HotkeyEnabled` + `Hotkey*`** *(default: Ctrl+Shift+C)* —
Global hotkey to push this PC's clipboard to the peer. Note that
Ctrl+Shift+C is used by some browsers (element inspector) and Windows
Terminal — while the agent is running, this hotkey takes precedence.

### Everything else (agent: appsettings.json)

`Serial.Baud` (921600), `Serial.ReadTimeoutMs` (5000),
`Serial.WriteTimeoutMs` (5000), `Serial.ReadBufferSize` (1 MB),
`Serial.WriteBufferSize` (1 MB), `Timing.MainTickMs` (50),
`Timing.ReconnectDelayMs` (1000), `Timing.CursorSendIntervalMs` (100),
`Timing.CursorKeepaliveSeconds` (2), `Timing.DisplayRefreshSeconds` (10),
`Clipboard.AutoSyncBytes` (16 KB — cap for Auto-mode broadcasts),
`Clipboard.MaxBytes` (256 KB — hard cap for any clipboard payload in either
direction), `Clipboard.Push.ConsoleKey` (`c`), `Switch.ConsoleKey` (`s`).
The console keys are legacy from when the agent ran with a real console
window; harmless as WinExe but not useful.

---

## Common tasks

### Restart everything

```bash
# Pi
sudo systemctl restart janus-controller

# PC (from the tray)
Right-click Janus tray icon → Quit
Double-click start-agent.bat  (or the scheduled task)
```

### Update the controller

```bash
cd /home/pi/janus/controller
git pull
sudo systemctl restart janus-controller
```

`uv` picks up any `pyproject.toml` changes on the next `uv run`.

### Update the Pico firmware

Copy the new `boot.py` / `code.py` to `CIRCUITPY/`. If `boot.py` changed,
full USB power-cycle. If only `code.py` changed, CircuitPython auto-reloads
on save (or press `Ctrl+D` in the REPL).

### Update an agent

Quit the tray icon first (releases the exe file lock and the COM port).
Then `dotnet build`. Then relaunch.

### Add a new keyboard or mouse

1. Plug it in on the Pi.
2. `ls /dev/input/by-id/` to find its path.
3. Copy an existing profile in `controller/profiles/` and edit the paths.
4. Point `device_config_file` in `config.yaml` at the new profile name.
5. Restart the controller.

If the device has multiple input nodes (gaming mice, combo receivers),
list all the relevant ones — see `razer_steelseries.yaml` for a
worked example. Dispatch is by event type, not by which list a path
appears in, so it's safe to list the same node in both `mouse` and
`keyboard`.

### Change a hotkey

Edit `appsettings.json` on the agent whose hotkey you're changing. Quit
and restart that agent. Hotkeys are registered process-wide, so a hotkey
set on Personal doesn't affect Work.

### Debug an input problem

1. On the Pi, set `logging.verbose: true` in `config.yaml`, restart the
   service, and watch `journalctl -u janus-controller -f`. Every mouse
   button and every forwarded event will log.
2. On the PC, open the agent's log window (tray → Show window).
3. `capture_events.py <device-path>` on the Pi to isolate what a specific
   device is emitting, independent of the controller.

Turn `verbose` back off when done — it makes the log unreadable at normal
usage.