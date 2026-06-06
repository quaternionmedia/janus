using Janus.Agent.Clipboard;
using Janus.Agent.Events;
using Janus.Agent.Platform;
using Janus.Agent.Settings;
using Janus.Agent.Tray;
using System.IO.Ports;

namespace Janus.Agent;

// Janus.Agent entry. Owns startup/shutdown lifecycle, the composition
// root, and the reconnect loop. All real work happens in modules:
//   Serial          -- wire I/O, receive loop, display/cursor send
//   ClipboardSync   -- inbound verbs, monitor callback, manual push
//   ClipboardText   -- text I/O + hash dedup
//   MessageWindow   -- STA hidden window backing clipboard listener,
//                      global hotkey registration, session-lock +
//                      system power-event notification
//   Actions         -- console-key + hotkey dispatch into actions
//   ConsoleWindow   -- hide/show the agent's own console + tool-window style
//   TrayIcon        -- NotifyIcon + context menu, primary user-facing UI
//   Config          -- appsettings.json -> static properties
//   Win32           -- P/Invoke surface
//
// ---- NOTE on input injection ----------------------------------------
// As of stage 4, the Pico injects HID reports directly. The agent no
// longer handles MOUSE MOVE / MOUSE BUTTON / MOUSE WHEEL / KEY messages
// -- those are intercepted on the Pico and never reach this process.
// Only CURSOR SET, CLIPBOARD *, and TARGET arrive here.
//
// CURSOR SET stays in the agent because HID is relative-only: there's
// no way to say "go to absolute (x, y)" via a standard HID mouse.
// SetCursorPos is a Win32 call that does exactly this. Required at
// switch time to land the cursor at the right entry point.
// ---------------------------------------------------------------------

internal static class Program
{
    private static async Task Main(string[] args)
    {
        string deviceId = args.Length > 0 ? args[0].ToUpperInvariant() : "P";
        string portName = args.Length > 1 ? args[1] : string.Empty;

        if (deviceId != "P" && deviceId != "W")
        {
            Console.WriteLine("Invalid device id. Use 'P' or 'W'.");
            return;
        }

        if (string.IsNullOrWhiteSpace(portName))
        {
            Console.WriteLine("Missing COM port. Example: P COM9");
            return;
        }

        // Apply tool-window style + hide console BEFORE any other startup
        // logging. ApplyToolWindowStyle() flips WS_EX_TOOLWINDOW so the
        // window won't appear in the taskbar even when later shown via
        // the tray menu. Hide() makes it invisible immediately so the
        // user doesn't see a console flash at process start.
        //
        // Console.WriteLine still works against a hidden console -- the
        // text accumulates in the console buffer and is visible when the
        // user clicks "Show window" from the tray.
        ConsoleWindow.ApplyToolWindowStyle();
        ConsoleWindow.Hide();

        Config.Load();

        using CancellationTokenSource cts = new();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine($"Janus.Agent [{deviceId}] started. Press Ctrl+C to stop.");
        Console.WriteLine($"serial port: {portName}");
        Console.WriteLine($"clipboard outbound mode: {Config.ClipboardOutboundMode}");
        Console.WriteLine($"clipboard push: console key '{Config.ClipboardPushConsoleKey}'"
            + (Config.ClipboardPushHotkeyEnabled ? ", global hotkey enabled" : ", global hotkey disabled"));
        Console.WriteLine($"switch devices: console key '{Config.SwitchConsoleKey}'"
            + (Config.SwitchHotkeyEnabled ? ", global hotkey enabled" : ", global hotkey disabled"));
        Console.WriteLine();

        // ---- Composition -------------------------------------------------

        MessageWindow.Start();
        MessageWindow.RegisterClipboardListener(ClipboardSync.OnChange);

        if (Config.ClipboardPushHotkeyEnabled)
        {
            MessageWindow.RegisterHotkey(
                Config.ClipboardPushHotkeyCtrl,
                Config.ClipboardPushHotkeyShift,
                Config.ClipboardPushHotkeyAlt,
                Config.ClipboardPushHotkeyKey,
                () => ClipboardSync.Push("hotkey"),
                "clipboard push");
        }
        if (Config.SwitchHotkeyEnabled)
        {
            MessageWindow.RegisterHotkey(
                Config.SwitchHotkeyCtrl,
                Config.SwitchHotkeyShift,
                Config.SwitchHotkeyAlt,
                Config.SwitchHotkeyKey,
                () => Actions.SwitchToPeer("hotkey"),
                "switch");
        }

        if (Config.SwitchOnLock)
        {
            MessageWindow.RegisterLockListener(() => Actions.SwitchToPeer("lock"));
            Console.WriteLine("switch on workstation lock: enabled");
        }

        if (Config.SwitchOnShutdown)
        {
            MessageWindow.RegisterPowerEventListener(() => Actions.SwitchToPeer("shutdown"));
            Console.WriteLine("switch on shutdown/suspend: enabled");
        }

        Actions.StartConsoleKeyReader(cts.Token);

        // TrayIcon hooks onto MessageWindow's STA thread; must be
        // started AFTER MessageWindow.Start(). Quit menu item calls
        // cts.Cancel, which unwinds this Main exactly the same way
        // Ctrl+C in the console would.
        TrayIcon.Start(deviceId, () => cts.Cancel());

        // ---- Reconnect loop ----------------------------------------------

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                using SerialPort? port = Serial.TryOpenPort(portName);

                if (port is null)
                {
                    await Task.Delay(Config.TimingReconnectDelayMs, cts.Token);
                    continue;
                }

                Console.WriteLine();
                Console.WriteLine($"Serial connected: {portName}");
                Serial.BeginSession(port, deviceId);

                // Seed the sync hash with the current clipboard so whatever
                // happens to be on it at startup doesn't fire a spurious
                // "I have new clipboard data!" broadcast.
                ClipboardText.SeedHash();

                using CancellationTokenSource sessionCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

                Task receiveTask = Task.Run(
                    () => Serial.RunReceiveLoop(port, sessionCts.Token, deviceId),
                    sessionCts.Token);

                try
                {
                    while (!cts.Token.IsCancellationRequested && port.IsOpen)
                    {
                        Serial.SendDisplayIfChanged(port, deviceId);
                        Serial.SendCursorIfNeeded(port, deviceId);
                        await Task.Delay(Config.TimingMainTickMs, cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex) when (Serial.IsSerialException(ex))
                {
                    Console.WriteLine($"Serial session error: {ex.Message}");
                }
                finally
                {
                    Serial.EndSession();
                    sessionCts.Cancel();

                    try { port.Close(); }    catch { }
                    try { port.Dispose(); }  catch { }
                    try { await receiveTask; } catch { }
                }

                if (!cts.Token.IsCancellationRequested)
                {
                    Console.WriteLine($"Serial disconnected. Retrying: {portName}");
                    await Task.Delay(Config.TimingReconnectDelayMs, cts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Console.WriteLine("Stopping agent.");
            // Tear down UI before the message pump. TrayIcon.Stop()
            // marshals onto MessageWindow's STA thread, so the pump
            // must still be running when we call it.
            TrayIcon.Stop();
            MessageWindow.Stop();
        }
    }
}