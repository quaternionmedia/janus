using Janus.Agent.Clipboard;
using Janus.Agent.Platform;
using System.IO.Ports;

namespace Janus.Agent;

// User-action dispatch layer. The actions themselves -- clipboard push,
// peer switch -- are agnostic to how they're invoked; this module is
// the routing for console keys and (via Program composition) global
// hotkeys.

internal static class Triggers
{
    // Single shared action for all manual switch triggers (console key,
    // global hotkey, future tray). Sends "SWITCH PEER" to the
    // controller, which switches to whichever side ISN'T this agent.
    // Safe to call from any thread; snapshots the port first.
    public static void SwitchToPeer(string source)
    {
        SerialPort? port = Serial.ActivePort;
        if (port is null || !port.IsOpen)
        {
            Console.WriteLine($"switch ({source}) ignored: no serial connection.");
            return;
        }

        try
        {
            port.WriteLine("SWITCH PEER");
            Console.WriteLine($"switch ({source}): requested switch to peer.");
        }
        catch (Exception ex) when (Serial.IsSerialException(ex))
        {
            Console.WriteLine($"switch send error: {ex.Message}");
        }
    }

    // Background reader for the agent's own console. Watches stdin for
    // the configured push/switch keys and fires the corresponding
    // action when seen. Runs on a background thread so it doesn't block
    // the main serial loop.
    //
    // Note on Ctrl+C: Console.CancelKeyPress (registered in Main)
    // handles Ctrl+C independently of this reader. ReadKey here only
    // sees ordinary keystrokes, so the two don't conflict. If stdin is
    // redirected (no console, e.g. running under a service with no
    // console), ReadKey throws InvalidOperationException -- we catch
    // it and quietly stop the reader, since there's no interactive
    // console to read from anyway.
    public static void StartConsoleKeyReader(CancellationToken token)
    {
        Thread reader = new(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!Console.KeyAvailable)
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    ConsoleKeyInfo info = Console.ReadKey(intercept: true);

                    // Compare case-insensitively against the configured key.
                    string pressed = info.KeyChar.ToString().ToLowerInvariant();
                    if (pressed == Config.ClipboardPushConsoleKey)
                    {
                        ClipboardSync.Push("console");
                    }
                    else if (pressed == Config.SwitchConsoleKey)
                    {
                        SwitchToPeer("console");
                    }
                }
                catch (InvalidOperationException)
                {
                    // No interactive console (stdin redirected). Nothing
                    // to read; stop the reader thread.
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"console reader error: {ex.Message}");
                    Thread.Sleep(250);
                }
            }
        })
        {
            IsBackground = true,
            Name = "ConsoleKeyReader",
        };
        reader.Start();
    }
}