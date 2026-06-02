# Janus Roadmap

1. Code cleanup/restructure — split input_router.py and Program.cs into focused modules. Probably worth doing before the next round of features so we don't pile more onto the current monoliths. Easier reviews, easier to open-source later.

2. Configurable switching methods — parallel to how clipboard already has multiple triggers:
   - Letter key in agent console (e.g. s to switch, like c for clipboard)
   - Config toggle for mouse-edge auto-switch (currently hardcoded on)
   - Tray menu (covered by #3)
   - Global hotkey (parallel to clipboard hotkey)
   - Implement switch on lock/shutdown/reboot/logout

3. Tray icon (expanded scope):
   - Notification on large clipboard receive
   - Right-click context menu with submenus for clipboard (send to peer, etc.) and switch (P / W)
   - Double-click opens desktop app (depends on #6)

4. Windows agent auto-start — Task Scheduler / startup shortcut on both PCs

5. WizMouse — OS-level smooth scrolling; quick install whenever it gets annoying

6. Desktop app — windowed GUI for:
   - Configuration UI (edit config files via forms instead of YAML/JSON)
   - Manual switch + clipboard-push buttons
   - Live filterable logs with verbosity levels (would require adding log levels to controller + agent first)

7. Uncomment 'storage.disable_usb_drive()' in pico's boot.py once confident in code. Once done, the only way to change is to BOOTSEL+re-flash CircuitPython (which wipes everything). Be sure latest is in repo.

8. Configurable extra buttons — two-layer config (controller maps device events → generic slots; Pico maps slots → HID actions)

9.  Image clipboard — chunked protocol for non-text content

10. Implement file-list clipboard (very unlikely, RDP does this by proxying the file through connection). But maybe we could use pi5 as shared storage?

11. Monitor layout configuration — un-hardcode Personal-top / Work-bottom

12. Security pass — especially important if this goes open-source

13. Benchmark / metrics / stress test

14. Agent side Pico log capture (stream over CDC, persist on Windows)
