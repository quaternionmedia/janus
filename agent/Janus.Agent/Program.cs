using Janus.Agent.Clipboard;
using Janus.Agent.Events;
using Janus.Agent.Gui;
using Janus.Agent.Platform;
using Janus.Agent.Settings;
using Janus.Agent.Tray;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Formatting.Compact;
using System.IO;
using System.IO.Ports;
using Log = Janus.Agent.Logging.Log;

namespace Janus.Agent;

// Janus.Agent entry. Owns startup/shutdown lifecycle, the composition
// root, and the reconnect loop. All real work happens in modules:
//   Serial          -- wire I/O, receive loop, display/cursor send
//   ClipboardSync   -- inbound verbs, monitor callback, manual push
//   ClipboardText   -- text I/O + hash dedup
//   MessageWindow   -- STA hidden window backing clipboard listener,
//                      global hotkey registration, session-lock +
//                      system power-event notification; also serves
//                      as the owner HWND for the tray's Win32 popup
//                      menu
//   Actions         -- console-key + hotkey dispatch into actions
//   ConsoleWindow   -- (vestigial) hide/show the agent's own console
//                      window; effectively a no-op now that we build
//                      as WinExe
//   TrayIcon        -- NotifyIcon + Win32 popup menu, primary
//                      user-facing UI
//   GuiHost         -- dispatcher thread + WPF logs/status window
//   LogSink         -- in-process ring buffer feeding the GUI
//   GuiSink         -- Serilog sink that pushes LogEvents into LogSink
//   Log             -- structured logging facade over Serilog
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

        // Argument validation runs BEFORE Serilog is configured (which
        // needs appsettings.json to have loaded). These errors go to
        // raw stdout only -- fine, because the process exits immediately.
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

        // ---- Serilog + logging pipeline ----------------------------------
        //
        // Order matters here:
        //   1. Build IConfiguration from appsettings.json.
        //   2. Build the Serilog logger and assign it to Serilog.Log.Logger
        //      BEFORE anything constructs a CategoryLogger (which reads
        //      Serilog.Log to build its ForContext scope).
        //   3. In Production only, install SerilogConsoleTee on Console.Out
        //      so stray writelines still land in the log store. In
        //      Development, skip the tee -- Serilog's own Console sink
        //      writes to stdout, and routing that back through the tee
        //      would recurse.

        IConfigurationRoot appConfig = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .Build();

        string envName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
        bool isDevelopment = envName.Equals("Development", StringComparison.OrdinalIgnoreCase);
        
        // Log path: user-configurable via Logging:OutputPath in appsettings.
        // Blank / missing = default under %LOCALAPPDATA%\Janus\logs. Custom
        // paths are run through ExpandEnvironmentVariables so users can write
        // "%USERPROFILE%\Documents\Janus" etc. Serilog's File sink itself
        // doesn't expand env vars, which is why we handle it here.
        string? configuredLogDir = appConfig["Janus:LogOutputPath"];
        string logDir = string.IsNullOrWhiteSpace(configuredLogDir)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Janus", "logs")
            : Environment.ExpandEnvironmentVariables(configuredLogDir);
        Directory.CreateDirectory(logDir);
        string logPath = Path.Combine(logDir, "agent-.jsonl");

        LoggerConfiguration loggerConfig = new LoggerConfiguration()
            .ReadFrom.Configuration(appConfig)
            .WriteTo.File(
                formatter: new CompactJsonFormatter(),
                path: logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true)
            .WriteTo.Sink(new GuiSink());

        if (isDevelopment)
        {
            // WinExe processes don't inherit the parent terminal's console
            // by default. Attach it explicitly so the Console sink's writes
            // land somewhere visible during `dotnet run`.
            Win32.AttachConsole(Win32.ATTACH_PARENT_PROCESS);

            loggerConfig = loggerConfig.WriteTo.Console(
                theme: Serilog.Sinks.SystemConsole.Themes.AnsiConsoleTheme.Code,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u5} [{Category:1}] {Message:lj} {NewLine}{Exception}");
        }

        Serilog.Log.Logger = loggerConfig.CreateLogger();

        if (!isDevelopment)
        {
            Console.SetOut(new SerilogConsoleTee());
        }
        else
        {
            Log.System.Info("Running in DEVELOPMENT mode.\n");
        }

        // Signal dark-mode capability to Windows. After this call, the
        // OS will render native popup menus (used by our tray icon via
        // TrackPopupMenuEx) in dark colors when the system theme is
        // dark. Undocumented uxtheme.dll ordinal 135, but stable across
        // Win10 1903+ / Win11. Must be called early -- before any
        // window is created -- to take effect.
        try
        {
            Win32.SetPreferredAppMode(Win32.APP_MODE_ALLOW_DARK);
        }
        catch (Exception ex)
        {
            // uxtheme.dll missing on a non-desktop SKU, or the ordinal
            // changed in some future Windows update. Not fatal; menus
            // will just stay light.
            Log.System.Warn(ex, "SetPreferredAppMode failed.");
        }

        // Tool-window style + hide on the console window. Both are
        // safe no-ops now that we build as WinExe (GetConsoleWindow
        // returns IntPtr.Zero and the methods early-return), but they
        // remain in case someone ever flips OutputType back to Exe.
        ConsoleWindow.ApplyToolWindowStyle();
        ConsoleWindow.Hide();

        Config.Load();

        using CancellationTokenSource cts = new();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Log.System.Info("Janus.Agent [{DeviceId}] started. Press Ctrl+C to stop.", deviceId);
        Log.System.Info("serial port: {PortName}", portName);
        Log.System.Info("clipboard outbound mode: {OutboundMode}", Config.ClipboardOutboundMode);
        Log.System.Info("clipboard push: console key '{ConsoleKey}', global hotkey {HotkeyState}",
            Config.ClipboardPushConsoleKey,
            Config.ClipboardPushHotkeyEnabled ? "enabled" : "disabled");
        Log.System.Info("switch devices: console key '{ConsoleKey}', global hotkey {HotkeyState}\n",
            Config.SwitchConsoleKey,
            Config.SwitchHotkeyEnabled ? "enabled" : "disabled");

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
            Log.System.Info("switch on workstation lock: enabled");
        }

        if (Config.SwitchOnShutdown)
        {
            MessageWindow.RegisterPowerEventListener(() => Actions.SwitchToPeer("shutdown"));
            Log.System.Info("switch on shutdown/suspend: enabled");
        }

        Actions.StartConsoleKeyReader(cts.Token);

        // TrayIcon hooks onto MessageWindow's STA thread; must be
        // started AFTER MessageWindow.Start(). Quit menu item calls
        // cts.Cancel, which unwinds this Main exactly the same way
        // Ctrl+C in the console would.
        TrayIcon.Start(deviceId, () => cts.Cancel());

        // GuiHost owns its own STA thread (separate from MessageWindow's
        // -- WPF and WinForms can't share a dispatcher). Start it AFTER
        // TrayIcon so all startup banner lines are already in LogSink
        // and will be seeded into the window's log view when it
        // constructs. The window is created hidden; the tray's
        // "Show window" item is the way to bring it up.
        GuiHost.Start(deviceId);

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

                Log.Serial.Info("Serial connected: {PortName}", portName);
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
                    Log.Serial.Error(ex, "Serial session error.");
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
                    Log.Serial.Warn("Serial disconnected. Retrying: {PortName}", portName);
                    await Task.Delay(Config.TimingReconnectDelayMs, cts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Log.System.Info("Stopping agent.");
            // Tear down UI in reverse-startup order:
            //  1. GuiHost  -- close the WPF window, shut its dispatcher.
            //  2. TrayIcon -- remove the tray icon and destroy the
            //                 Win32 popup menu (marshals onto
            //                 MessageWindow's STA, so the WinForms pump
            //                 must still be running).
            //  3. MessageWindow -- stop the WinForms STA pump.
            GuiHost.Stop();
            TrayIcon.Stop();
            MessageWindow.Stop();

            // Flush any buffered log entries and release file handles.
            Serilog.Log.CloseAndFlush();
        }
    }
}