using Janus.Agent.Clipboard;
using Janus.Agent.Platform;
using System.IO.Ports;

namespace Janus.Agent;

// Janus.Agent entry. Owns startup/shutdown lifecycle, the composition
// root, and the reconnect loop. All real work happens in modules:
//   Serial          -- wire I/O, receive loop, display/cursor send
//   ClipboardSync   -- inbound verbs, monitor callback, manual push
//   ClipboardText   -- text I/O + hash dedup
//   MessageWindow   -- STA hidden window backing clipboard listener
//                      and global hotkey registration
//   Triggers        -- console-key + hotkey dispatch into actions
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

        Config.Load();

        using CancellationTokenSource cts = new();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine($"Janus.Agent [{deviceId}] started. Press Ctrl+C to stop.");
        Console.WriteLine($"port: {portName}");
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
                () => Triggers.SwitchToPeer("hotkey"),
                "switch");
        }

        if (Config.SwitchOnLock)
        {
            MessageWindow.RegisterLockListener(() => Triggers.SwitchToPeer("lock"));
            Console.WriteLine("switch on workstation lock: enabled");
        }

        Triggers.StartConsoleKeyReader(cts.Token);

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
            MessageWindow.Stop();
        }
    }
}