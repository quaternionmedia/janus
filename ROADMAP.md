# Janus Roadmap

1. Tray icon (expanded scope):
   - Notification on large clipboard receive
   - Right-click context menu with submenus for clipboard (send to peer, etc.)

2. WizMouse — OS-level smooth scrolling; quick install whenever it gets annoying

3. Desktop app — Configuration settings editable in UI, loose idea - save-and-restart via detached PowerShell relaunching the scheduled task while current process exits (edit config files via forms instead of YAML/JSON)

4. Desktop app — Diagnostics view - Log stream - Live filterable logs with verbosity levels (would require adding log levels to controller + agent first)
   
5. Desktop app — Diagnostics view - Counters/Metrics
   
6. Desktop app — Diagnostics view - Live Status

7. Configurable extra buttons — two-layer config (controller maps device events → generic slots; Pico maps slots → HID actions)

8.  Image clipboard — chunked protocol for non-text content

9.  Implement file-list clipboard (very unlikely, RDP does this by proxying the file through connection). But maybe we could use pi5 as shared storage?

10. Monitor layout configuration — un-hardcode Personal-top / Work-bottom

11. Security pass — especially important if this goes open-source

12. Tests

13. Agent side Pico log capture (stream over CDC, persist on Windows)

14. Uncomment 'storage.disable_usb_drive()' in pico's boot.py once confident in code. Once done, the only way to change is to BOOTSEL+re-flash CircuitPython (which wipes everything). Be sure latest is in repo.