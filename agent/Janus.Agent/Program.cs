using System.Globalization;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class Program
{
    // ---- Configuration --------------------------------------------------
    //
    // All tuning values come from appsettings.json at startup. Defaults
    // below match the historical hardcoded values, so a missing config
    // file or omitted field falls back to the same behavior as before.
    // See appsettings.json for the schema.

    // Serial port settings
    private static int _serialBaud = 921600;
    private static int _serialReadTimeoutMs = 5000;
    private static int _serialWriteTimeoutMs = 5000;
    private static int _serialReadBufferSize = 1024 * 1024;
    private static int _serialWriteBufferSize = 1024 * 1024;

    // Main loop timing
    private static int _timingMainTickMs = 50;
    private static int _timingReconnectDelayMs = 1000;
    private static int _timingCursorSendIntervalMs = 100;
    private static int _timingCursorKeepaliveSeconds = 2;
    private static int _timingDisplayRefreshSeconds = 10;

    // Clipboard policy
    private static int _clipboardAutoSyncBytes = 16 * 1024;
    private static int _clipboardMaxBytes = 256 * 1024;
    private static ClipboardOutboundMode _clipboardOutboundMode = ClipboardOutboundMode.Auto;

    // Manual clipboard-push triggers. Lets the user push this PC's
    // clipboard to the peer without going to the controller terminal.
    // All three triggers (console key, global hotkey, future tray) call
    // the same PushClipboardToPeer() action.
    private static string _clipboardPushConsoleKey = "c";   // key in the agent's own console
    private static bool _clipboardPushHotkeyEnabled = false;
    private static bool _clipboardPushHotkeyCtrl = true;
    private static bool _clipboardPushHotkeyShift = true;
    private static bool _clipboardPushHotkeyAlt = false;
    private static string _clipboardPushHotkeyKey = "C";    // single character A-Z / 0-9

    // Switch-to-peer triggers. Mirrors the clipboard-push pattern.
    // Sends "SWITCH PEER" to the controller, which performs the switch
    // to whichever side ISN'T this agent.
    private static string _switchConsoleKey = "s";
    private static bool _switchHotkeyEnabled = false;
    private static bool _switchHotkeyCtrl = true;
    private static bool _switchHotkeyShift = true;
    private static bool _switchHotkeyAlt = false;
    private static string _switchHotkeyKey = "S";
    
    // ---- Runtime state --------------------------------------------------

    private static volatile bool _isActiveTarget;
    private static DateTime _lastCursorSentUtc = DateTime.MinValue;
    private static int _lastCursorX = int.MinValue;
    private static int _lastCursorY = int.MinValue;
    private static string? _lastDisplayMessage;
    private static DateTime _lastDisplaySentUtc = DateTime.MinValue;
    private static bool _displaySentForCurrentConnection;

    // Hash of the last clipboard value this agent either sent out or
    // accepted inbound. Used to suppress the auto-sync feedback loop:
    // when the monitor fires with a clipboard matching this hash, the
    // change was caused by sync itself and must not be re-broadcast.
    private static string _lastSyncedClipboardHash = string.Empty;
    private static readonly object _clipboardHashLock = new();
    private static SerialPort? _activePort;
    private static string? _activeDeviceId;
    private static ClipboardWindow? _clipboardWindow;
    private static Thread? _clipboardThread;

    // ---- NOTE on input injection ----------------------------------------
    //
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

        LoadConfig();

        using CancellationTokenSource cts = new();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine($"Janus.Agent [{deviceId}] started. Press Ctrl+C to stop.");
        Console.WriteLine($"port: {portName}");
        Console.WriteLine($"clipboard outbound mode: {_clipboardOutboundMode}");
        Console.WriteLine($"clipboard push: console key '{_clipboardPushConsoleKey}'"
            + (_clipboardPushHotkeyEnabled ? ", global hotkey enabled" : ", global hotkey disabled"));
        Console.WriteLine($"switch target: console key '{_switchConsoleKey}'"
            + (_switchHotkeyEnabled ? ", global hotkey enabled" : ", global hotkey disabled"));
        Console.WriteLine();

        StartClipboardMonitor();
        StartConsoleKeyReader(cts.Token);

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                using SerialPort? port = TryOpenPort(portName);

                if (port is null)
                {
                    await Task.Delay(_timingReconnectDelayMs, cts.Token);
                    continue;
                }

                Console.WriteLine($"Serial connected: {portName}");
                _displaySentForCurrentConnection = false;
                _lastDisplaySentUtc = DateTime.MinValue;
                _isActiveTarget = false;
                _lastCursorSentUtc = DateTime.MinValue;
                _lastCursorX = int.MinValue;
                _lastCursorY = int.MinValue;
                _activePort = port;
                _activeDeviceId = deviceId;

                // Seed the sync hash with the current clipboard so whatever
                // happens to be on it at startup doesn't fire a spurious
                // "I have new clipboard data!" broadcast.
                SeedClipboardHash();

                using CancellationTokenSource sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);

                Task receiveTask = Task.Run(() => ReceiveLoop(port, sessionCts.Token, deviceId), sessionCts.Token);

                try
                {
                    while (!cts.Token.IsCancellationRequested && port.IsOpen)
                    {
                        SendDisplayIfChanged(port, deviceId);
                        SendCursorIfNeeded(port, deviceId);
                        await Task.Delay(_timingMainTickMs, cts.Token);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex) when (IsSerialException(ex))
                {
                    Console.WriteLine($"Serial session error: {ex.Message}");
                }
                finally
                {
                    _activePort = null;
                    _activeDeviceId = null;
                    sessionCts.Cancel();

                    try
                    {
                        port.Close();
                    }
                    catch
                    {
                    }
                    try
                    {
                        port.Dispose();
                    }
                    catch
                    {
                    }
                    try
                    {
                        await receiveTask;
                    }
                    catch
                    {
                    }
                }

                if (!cts.Token.IsCancellationRequested)
                {
                    Console.WriteLine($"Serial disconnected. Retrying: {portName}");
                    await Task.Delay(_timingReconnectDelayMs, cts.Token);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Console.WriteLine("Stopping agent.");
            StopClipboardMonitor();
        }
    }

    // ---- Configuration loading ------------------------------------------

    private static void LoadConfig()
    {
        string configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");

        if (!File.Exists(configPath))
        {
            Console.WriteLine($"appsettings.json not found at {configPath}; using defaults.");
            return;
        }

        try
        {
            string json = File.ReadAllText(configPath);
            AgentConfig? cfg = JsonSerializer.Deserialize<AgentConfig>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                });

            if (cfg is null)
            {
                Console.WriteLine("appsettings.json parsed as empty; using defaults.");
                return;
            }

            ApplySerialConfig(cfg.Serial);
            ApplyClipboardConfig(cfg.Clipboard);
            ApplyTimingConfig(cfg.Timing);
            ApplySwitchConfig(cfg.Switch);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load appsettings.json: {ex.Message}. Using defaults.");
        }
    }

    private static void ApplySerialConfig(SerialSection? section)
    {
        if (section is null) return;
        if (section.Baud > 0) _serialBaud = section.Baud;
        if (section.ReadTimeoutMs > 0) _serialReadTimeoutMs = section.ReadTimeoutMs;
        if (section.WriteTimeoutMs > 0) _serialWriteTimeoutMs = section.WriteTimeoutMs;
        if (section.ReadBufferSize > 0) _serialReadBufferSize = section.ReadBufferSize;
        if (section.WriteBufferSize > 0) _serialWriteBufferSize = section.WriteBufferSize;
    }

    private static void ApplyTimingConfig(TimingSection? section)
    {
        if (section is null) return;
        if (section.MainTickMs > 0) _timingMainTickMs = section.MainTickMs;
        if (section.ReconnectDelayMs > 0) _timingReconnectDelayMs = section.ReconnectDelayMs;
        if (section.CursorSendIntervalMs > 0) _timingCursorSendIntervalMs = section.CursorSendIntervalMs;
        if (section.CursorKeepaliveSeconds > 0) _timingCursorKeepaliveSeconds = section.CursorKeepaliveSeconds;
        if (section.DisplayRefreshSeconds > 0) _timingDisplayRefreshSeconds = section.DisplayRefreshSeconds;
    }

    private static void ApplyClipboardConfig(ClipboardSection? section)
    {
        if (section is null) return;
        if (section.AutoSyncBytes > 0) _clipboardAutoSyncBytes = section.AutoSyncBytes;
        if (section.MaxBytes > 0) _clipboardMaxBytes = section.MaxBytes;

        if (!string.IsNullOrWhiteSpace(section.OutboundMode))
        {
            _clipboardOutboundMode = section.OutboundMode.Trim().ToLowerInvariant() switch
            {
                "manual" => ClipboardOutboundMode.Manual,
                "auto" => ClipboardOutboundMode.Auto,
                _ => ClipboardOutboundMode.Auto,
            };
        }

        if (section.Push is null) return;
        var push = section.Push;
        if (!string.IsNullOrWhiteSpace(push.ConsoleKey))
        {
            _clipboardPushConsoleKey = push.ConsoleKey.Trim().ToLowerInvariant();
        }
        _clipboardPushHotkeyEnabled = push.HotkeyEnabled;
        _clipboardPushHotkeyCtrl = push.HotkeyCtrl;
        _clipboardPushHotkeyShift = push.HotkeyShift;
        _clipboardPushHotkeyAlt = push.HotkeyAlt;
        if (!string.IsNullOrWhiteSpace(push.HotkeyKey))
        {
            _clipboardPushHotkeyKey = push.HotkeyKey.Trim().ToUpperInvariant();
        }
    }

    private static void ApplySwitchConfig(SwitchSection? section)
    {
        if (section is null) return;
        if (!string.IsNullOrWhiteSpace(section.ConsoleKey))
        {
            _switchConsoleKey = section.ConsoleKey.Trim().ToLowerInvariant();
        }
        _switchHotkeyEnabled = section.HotkeyEnabled;
        _switchHotkeyCtrl = section.HotkeyCtrl;
        _switchHotkeyShift = section.HotkeyShift;
        _switchHotkeyAlt = section.HotkeyAlt;
        if (!string.IsNullOrWhiteSpace(section.HotkeyKey))
        {
            _switchHotkeyKey = section.HotkeyKey.Trim().ToUpperInvariant();
        }
    }

    private sealed class AgentConfig
    {
        public SerialSection? Serial { get; set; }
        public ClipboardSection? Clipboard { get; set; }
        public TimingSection? Timing { get; set; }
        public SwitchSection? Switch { get; set; }
    }

    private sealed class SerialSection
    {
        public int Baud { get; set; }
        public int ReadTimeoutMs { get; set; }
        public int WriteTimeoutMs { get; set; }
        public int ReadBufferSize { get; set; }
        public int WriteBufferSize { get; set; }
    }

    private sealed class TimingSection
    {
        public int MainTickMs { get; set; }
        public int ReconnectDelayMs { get; set; }
        public int CursorSendIntervalMs { get; set; }
        public int CursorKeepaliveSeconds { get; set; }
        public int DisplayRefreshSeconds { get; set; }
    }

    private enum ClipboardOutboundMode
    {
        Auto,
        Manual,
    }

    private sealed class ClipboardSection
    {
        public string? OutboundMode { get; set; }
        public int AutoSyncBytes { get; set; }
        public int MaxBytes { get; set; }
        public PushSection? Push { get; set; }
    }

    private sealed class PushSection
    {
        public string? ConsoleKey { get; set; }
        public bool HotkeyEnabled { get; set; }
        public bool HotkeyCtrl { get; set; }
        public bool HotkeyShift { get; set; }
        public bool HotkeyAlt { get; set; }
        public string? HotkeyKey { get; set; }
    }

    private sealed class SwitchSection
    {
        public string? ConsoleKey { get; set; }
        public bool HotkeyEnabled { get; set; }
        public bool HotkeyCtrl { get; set; }
        public bool HotkeyShift { get; set; }
        public bool HotkeyAlt { get; set; }
        public string? HotkeyKey { get; set; }
    }
 
    // ---- Serial port ----------------------------------------------------

    private static SerialPort? TryOpenPort(string portName)
    {
        SerialPort? port = null;
        try
        {
            port = new(portName, _serialBaud)
            {
                NewLine = "\n",
                ReadTimeout = _serialReadTimeoutMs,
                WriteTimeout = _serialWriteTimeoutMs,
                DtrEnable = true,
                RtsEnable = true,
                ReadBufferSize = _serialReadBufferSize,
                WriteBufferSize = _serialWriteBufferSize,
            };
            port.Open();
            return port;
        }
        catch (Exception ex) when (IsSerialException(ex))
        {
            Console.WriteLine($"Open failed for {portName}: {ex.Message}");
            port?.Dispose();
            return null;
        }
    }

    private static void ReceiveLoop(SerialPort port, CancellationToken cancellationToken, string deviceId)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                string? line = port.ReadLine()?.Trim();

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                HandleIncomingLine(line, port, deviceId);
            }
            catch (TimeoutException)
            {
                continue;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (IsSerialException(ex))
            {
                Console.WriteLine($"Receive error: {ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                // A malformed line (bad int parse, unexpected format, etc.)
                // must not kill this task. Log and continue; the serial link
                // itself is still healthy.
                Console.WriteLine($"Receive handler error (skipping line): {ex.GetType().Name}: {ex.Message}");
                continue;
            }
        }
    }

    private static void SendDisplayIfChanged(SerialPort port, string deviceId)
    {
        int left = GetSystemMetrics(SystemMetric.SM_XVIRTUALSCREEN);
        int top = GetSystemMetrics(SystemMetric.SM_YVIRTUALSCREEN);
        int width = GetSystemMetrics(SystemMetric.SM_CXVIRTUALSCREEN);
        int height = GetSystemMetrics(SystemMetric.SM_CYVIRTUALSCREEN);

        string displayMessage = $"DISPLAY {deviceId} L={left} T={top} W={width} H={height}";
        bool changed = _lastDisplayMessage != displayMessage;

        bool refreshDue = DateTime.UtcNow - _lastDisplaySentUtc >= TimeSpan.FromSeconds(_timingDisplayRefreshSeconds);

        if (!changed && _displaySentForCurrentConnection && !refreshDue)
        {
            return;
        }

        port.WriteLine(displayMessage);

        _lastDisplayMessage = displayMessage;
        _displaySentForCurrentConnection = true;
        _lastDisplaySentUtc = DateTime.UtcNow;
    }

    private static void SendCursorIfNeeded(SerialPort port, string deviceId)
    {
        if (!_isActiveTarget)
        {
            return;
        }

        if (DateTime.UtcNow - _lastCursorSentUtc < TimeSpan.FromMilliseconds(_timingCursorSendIntervalMs))
        {
            return;
        }

        if (!GetCursorPos(out POINT point))
        {
            return;
        }

        bool changed = point.X != _lastCursorX || point.Y != _lastCursorY;
        bool keepaliveDue = DateTime.UtcNow - _lastCursorSentUtc >= TimeSpan.FromSeconds(_timingCursorKeepaliveSeconds);

        if (!changed && !keepaliveDue)
        {
            return;
        }

        port.WriteLine($"CURSOR {deviceId} X={point.X} Y={point.Y}");
        _lastCursorX = point.X;
        _lastCursorY = point.Y;
        _lastCursorSentUtc = DateTime.UtcNow;
    }

    private static void HandleIncomingLine(string line, SerialPort port, string deviceId)
    {
        if (line.StartsWith("TARGET ", StringComparison.Ordinal))
        {
            string activeTarget = line[7..];
            bool wasActive = _isActiveTarget;
            _isActiveTarget = string.Equals(activeTarget, deviceId, StringComparison.Ordinal);

            // Transition to active: invalidate the cached cursor so the very
            // next SendCursorIfNeeded tick treats the real position as
            // "changed" and pushes it to the router. Without this, the
            // router's belief of our cursor position can lag the real one
            // by an entire session until the next real cursor move.
            if (!wasActive && _isActiveTarget)
            {
                _lastCursorX = int.MinValue;
                _lastCursorY = int.MinValue;
                _lastCursorSentUtc = DateTime.MinValue;
                Console.WriteLine($"=== ACTIVE TARGET: {activeTarget} ===");
            }
            else if (wasActive && !_isActiveTarget)
            {
                Console.WriteLine($"=== ACTIVE TARGET: {activeTarget} ===");
            }

            return;
        }

        if (line.Equals("CLIPBOARD REQUEST", StringComparison.Ordinal))
        {
            HandleClipboardRequest(port);
            return;
        }

        if (line.Equals("CLIPBOARD CLEAR", StringComparison.Ordinal))
        {
            HandleClipboardClear();
            return;
        }

        if (line.StartsWith("CLIPBOARD SET ", StringComparison.Ordinal))
        {
            HandleClipboardSet(line);
            return;
        }

        if (line.StartsWith("CURSOR SET ", StringComparison.Ordinal))
        {
            HandleCursorSet(line);
            return;
        }

        // MOUSE MOVE / MOUSE BUTTON / MOUSE WHEEL / MOUSE HWHEEL / KEY
        // are now consumed on the Pico and never reach the agent. If
        // one ever shows up here it means the Pico's HID routing
        // failed -- log it as a warning rather than silently ignoring.
        if (line.StartsWith("MOUSE ", StringComparison.Ordinal)
            || line.StartsWith("KEY ", StringComparison.Ordinal))
        {
            Console.WriteLine($"unexpected input message reached agent: {line}");
        }
    }

    // ---- Clipboard & Switch trigger handlers ---------------------------------------------

    // Single shared action for all manual push triggers (console key,
    // global hotkey, future tray). Grabs the current active port and
    // sends this PC's clipboard to the peer -- exactly what the
    // controller's 'c' command does via CLIPBOARD REQUEST, but initiated
    // locally. Safe to call from any thread; it snapshots the port first.
    private static void PushClipboardToPeer(string source)
    {
        SerialPort? port = _activePort;
        if (port is null || !port.IsOpen)
        {
            Console.WriteLine($"push ({source}) ignored: no serial connection.");
            return;
        }

        Console.WriteLine($"push ({source}): sending clipboard to peer.");
        HandleClipboardRequest(port);
    }

    // Single shared action for all manual switch triggers (console key,
    // global hotkey, future tray). Sends "SWITCH PEER" to the controller,
    // which switches to whichever side ISN'T this agent. Safe to call
    // from any thread; snapshots the port first.
    private static void SwitchToPeer(string source)
    {
        SerialPort? port = _activePort;
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
        catch (Exception ex) when (IsSerialException(ex))
        {
            Console.WriteLine($"switch send error: {ex.Message}");
        }
    }

    private static void HandleClipboardRequest(SerialPort port)
    {
        // Manual push triggered by the controller's 'c' command OR by a
        // local trigger via PushClipboardToPeer(). Always honored
        // regardless of outbound mode -- the user explicitly asked for
        // this.
        string text = GetClipboardTextSafe();
        byte[] rawBytes = Encoding.UTF8.GetBytes(text);

        if (rawBytes.Length > _clipboardMaxBytes)
        {
            Console.WriteLine(
                $"clipboard refused (outbound): {rawBytes.Length} bytes exceeds {_clipboardMaxBytes} limit");
            // Tell the other side to clear its clipboard so a stale value
            // doesn't silently paste.
            try
            {
                port.WriteLine("CLIPBOARD CLEAR");
            }
            catch (Exception ex) when (IsSerialException(ex))
            {
                Console.WriteLine($"clipboard clear send error: {ex.Message}");
            }
            return;
        }

        string encoded = Convert.ToBase64String(rawBytes);

        try
        {
            port.WriteLine($"CLIPBOARD DATA TEXT={encoded}");
        }
        catch (Exception ex) when (IsSerialException(ex))
        {
            Console.WriteLine($"clipboard send error: {ex.Message}");
            return;
        }

        UpdateSyncedClipboardHash(rawBytes);
        Console.WriteLine($"clipboard sent ({rawBytes.Length} bytes)");
    }

    private static void HandleClipboardSet(string line)
    {
        // Inbound clipboard from the peer is always accepted regardless of
        // outbound policy. The policy controls what we BROADCAST, not what
        // we ACCEPT. Asymmetric by design: Personal-side wants to receive
        // from Work freely, but not leak its own clipboard to Work.

        // Pull only the TEXT= payload; length check before any base64
        // decode allocation.
        const string marker = " TEXT=";
        int idx = line.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
        {
            return;
        }

        string encoded = line[(idx + marker.Length)..];

        byte[] rawBytes;
        try
        {
            rawBytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException ex)
        {
            Console.WriteLine($"Clipboard decode error: {ex.Message}");
            return;
        }

        if (rawBytes.Length > _clipboardMaxBytes)
        {
            // The other side violated the size contract (or the message
            // got corrupted). Clear local clipboard so nothing stale
            // lingers.
            Console.WriteLine(
                $"clipboard oversized inbound: {rawBytes.Length} bytes, clearing destination");
            SetClipboardTextSafe(string.Empty);
            UpdateSyncedClipboardHash(Array.Empty<byte>());
            return;
        }

        try
        {
            string text = Encoding.UTF8.GetString(rawBytes);
            // Record hash BEFORE we apply the change so the monitor event
            // that our own SetText is about to fire gets correctly
            // suppressed as a sync echo.
            UpdateSyncedClipboardHash(rawBytes);
            SetClipboardTextSafe(text);
            Console.WriteLine($"clipboard received ({rawBytes.Length} bytes)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Clipboard set error: {ex.Message}");
        }
    }

    private static void HandleClipboardClear()
    {
        Console.WriteLine("clipboard clear received");
        SetClipboardTextSafe(string.Empty);
        UpdateSyncedClipboardHash(Array.Empty<byte>());
    }

    private static void HandleCursorSet(string line)
    {
        // "CURSOR SET X=1234 Y=567"
        // Kept on the agent (not handed off to Pico HID) because HID
        // mouse reports are relative-only -- there's no standard way to
        // request absolute cursor positioning. SetCursorPos is the Win32
        // primitive that does this directly.
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        int? x = null;
        int? y = null;

        foreach (string part in parts)
        {
            if (part.StartsWith("X=", StringComparison.Ordinal))
            {
                if (!int.TryParse(part["X=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedX))
                {
                    Console.WriteLine($"CURSOR SET parse error: {line}");
                    return;
                }
                x = parsedX;
            }
            else if (part.StartsWith("Y=", StringComparison.Ordinal))
            {
                if (!int.TryParse(part["Y=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedY))
                {
                    Console.WriteLine($"CURSOR SET parse error: {line}");
                    return;
                }
                y = parsedY;
            }
        }

        if (x is null || y is null)
        {
            return;
        }

        SetCursorPos(x.Value, y.Value);
    }

    private static string GetClipboardTextSafe()
    {
        ClipboardWindow? window = _clipboardWindow;
        if (window is null || window.IsDisposed || !window.IsHandleCreated)
        {
            // Window not ready (startup race) or disposed (shutdown).
            // Fall back to a one-off STA thread to preserve correctness.
            return GetClipboardTextOnFreshSTAThread();
        }

        string result = string.Empty;
        Action work = () =>
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    result = Clipboard.GetText();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Clipboard get error: {ex.Message}");
            }
        };

        try
        {
            if (window.InvokeRequired)
            {
                window.Invoke(work);
            }
            else
            {
                // Already on the STA thread (e.g. OnClipboardChanged); run
                // directly to avoid InvokeRequired's needless marshal hop.
                work();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Clipboard get marshal error: {ex.Message}");
        }

        return result;
    }

    private static void SetClipboardTextSafe(string text)
    {
        ClipboardWindow? window = _clipboardWindow;
        if (window is null || window.IsDisposed || !window.IsHandleCreated)
        {
            SetClipboardTextOnFreshSTAThread(text);
            return;
        }

        Exception? capturedException = null;
        Action work = () =>
        {
            try
            {
                Clipboard.SetText(text ?? string.Empty);
            }
            catch (Exception ex)
            {
                capturedException = ex;
            }
        };

        try
        {
            if (window.InvokeRequired)
            {
                window.Invoke(work);
            }
            else
            {
                work();
            }
        }
        catch (Exception ex)
        {
            capturedException = ex;
        }

        if (capturedException is not null)
        {
            throw capturedException;
        }
    }

    private static string GetClipboardTextOnFreshSTAThread()
    {
        // Fallback for startup/shutdown when the monitor window isn't
        // available. Same behavior as the pre-refactor implementation.
        string result = string.Empty;
        Exception? capturedException = null;

        Thread thread = new(() =>
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    result = Clipboard.GetText();
                }
            }
            catch (Exception ex)
            {
                capturedException = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (capturedException is not null)
        {
            Console.WriteLine($"Clipboard get error: {capturedException.Message}");
        }

        return result;
    }

    private static void SetClipboardTextOnFreshSTAThread(string text)
    {
        Exception? capturedException = null;

        Thread thread = new(() =>
        {
            try
            {
                Clipboard.SetText(text ?? string.Empty);
            }
            catch (Exception ex)
            {
                capturedException = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (capturedException is not null)
        {
            throw capturedException;
        }
    }

    private static string ComputeHash(byte[] data)
    {
        byte[] hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }

    private static void UpdateSyncedClipboardHash(byte[] rawBytes)
    {
        string hash = ComputeHash(rawBytes);
        lock (_clipboardHashLock)
        {
            _lastSyncedClipboardHash = hash;
        }
    }

    private static bool IsAlreadySynced(byte[] rawBytes)
    {
        string hash = ComputeHash(rawBytes);
        lock (_clipboardHashLock)
        {
            return string.Equals(hash, _lastSyncedClipboardHash, StringComparison.Ordinal);
        }
    }

    private static void SeedClipboardHash()
    {
        try
        {
            string text = GetClipboardTextSafe();
            UpdateSyncedClipboardHash(Encoding.UTF8.GetBytes(text));
        }
        catch
        {
            // Best-effort seeding; if it fails, the first monitor event
            // will just do a one-time redundant send, which is harmless.
        }
    }

    // ---- Clipboard monitor (Windows AddClipboardFormatListener) ----
    //
    // Runs a dedicated STA thread that owns a hidden message-only window.
    // Windows posts WM_CLIPBOARDUPDATE to that window on every clipboard
    // change. When one arrives, the OnClipboardChanged callback decides
    // what to do based on the outbound policy:
    //
    //   Auto mode:   compare hash against the last synced value; if
    //                different, push the new clipboard to the active port.
    //   Manual mode: log a hint that the user can manually push via 'c'
    //                in the controller. Update the hash but do NOT send.
    //
    // The hash check is what keeps the two-agent loop from bouncing
    // forever in Auto mode: incoming CLIPBOARD SET on agent A updates the
    // hash before SetText fires, so A's own monitor callback sees
    // hash-match and suppresses the re-broadcast.

    // Background reader for the agent's own console. Watches stdin for the
    // configured push key and fires a clipboard push when seen. Runs on a
    // background thread so it doesn't block the main serial loop.
    //
    // Note on Ctrl+C: Console.CancelKeyPress (registered in Main) handles
    // Ctrl+C independently of this reader. ReadKey here only sees ordinary
    // keystrokes, so the two don't conflict. If stdin is redirected (no
    // console, e.g. running under a service with no console), ReadKey
    // throws InvalidOperationException -- we catch it and quietly stop the
    // reader, since there's no interactive console to read from anyway.
    private static void StartConsoleKeyReader(CancellationToken token)
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
                    if (pressed == _clipboardPushConsoleKey)
                    {
                        PushClipboardToPeer("console");
                    }
                    else if (pressed == _switchConsoleKey)
                    {
                        SwitchToPeer("console");
                    }
                }
                catch (InvalidOperationException)
                {
                    // No interactive console (stdin redirected). Nothing to
                    // read; stop the reader thread.
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

    private static void StartClipboardMonitor()
    {
        _clipboardThread = new Thread(() =>
        {
            try
            {
                _clipboardWindow = new ClipboardWindow(
                    OnClipboardChanged,
                    onPushHotkey: () => PushClipboardToPeer("hotkey"),
                    onSwitchHotkey: () => SwitchToPeer("hotkey"));
                Application.Run(_clipboardWindow);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Clipboard monitor error: {ex.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "ClipboardMonitor",
        };
        _clipboardThread.SetApartmentState(ApartmentState.STA);
        _clipboardThread.Start();
    }

    private static void StopClipboardMonitor()
    {
        try
        {
            _clipboardWindow?.BeginInvoke(new Action(() =>
            {
                try
                {
                    _clipboardWindow.Close();
                }
                catch
                {
                }
            }));
        }
        catch
        {
        }

        try
        {
            _clipboardThread?.Join(500);
        }
        catch
        {
        }
    }

    private static void OnClipboardChanged()
    {
        // Capture the port/device-id snapshot up front. The main loop may
        // null these out mid-callback on disconnect; we want a consistent
        // view for the whole send.
        SerialPort? port = _activePort;
        string? deviceId = _activeDeviceId;

        if (port is null || deviceId is null || !port.IsOpen)
        {
            return;
        }

        string text;
        try
        {
            text = GetClipboardTextSafe();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Clipboard monitor read error: {ex.Message}");
            return;
        }

        byte[] rawBytes = Encoding.UTF8.GetBytes(text);

        // Suppress if this clipboard change was caused by our own sync
        // (either our SetText from an inbound line, or a previous outbound
        // we already broadcast). Applies in both Auto and Manual modes.
        if (IsAlreadySynced(rawBytes))
        {
            return;
        }

        // ---- MANUAL OUTBOUND MODE ---------------------------------------
        //
        // In Manual mode we never auto-broadcast. Every local clipboard
        // change is announced to the console with a hint telling the user
        // how to push it manually. The hash is updated so we don't
        // re-announce the same content if the monitor fires again.
        //
        // Future extension point: this is where a tray notification or
        // popup with a "send to peer" button would hook in. Today it's
        // just a console message.
        if (_clipboardOutboundMode == ClipboardOutboundMode.Manual)
        {
            if (rawBytes.Length > _clipboardMaxBytes)
            {
                Console.WriteLine(
                    $"clipboard change ({rawBytes.Length} bytes) exceeds {_clipboardMaxBytes} hard limit; cannot be sent.");
            }
            else
            {
                Console.WriteLine(
                    $"clipboard change ({rawBytes.Length} bytes); press 'c' in controller to send to peer.");
            }
            UpdateSyncedClipboardHash(rawBytes);
            return;
        }

        // ---- AUTO OUTBOUND MODE (original behavior) ---------------------

        if (rawBytes.Length > _clipboardMaxBytes)
        {
            // Over hard ceiling: refuse and tell the other side to clear
            // its clipboard so a stale value doesn't paste silently.
            Console.WriteLine(
                $"clipboard auto-sync refused: {rawBytes.Length} bytes exceeds {_clipboardMaxBytes} hard limit");
            try
            {
                port.WriteLine("CLIPBOARD CLEAR");
            }
            catch (Exception ex) when (IsSerialException(ex))
            {
                Console.WriteLine($"clipboard clear send error: {ex.Message}");
            }
            // Record the oversized hash so we don't re-attempt every tick.
            UpdateSyncedClipboardHash(rawBytes);
            return;
        }

        if (rawBytes.Length > _clipboardAutoSyncBytes)
        {
            // Between auto-sync and hard ceiling: don't broadcast. User
            // can trigger a manual 'c' in the router if they want this
            // on the other side. Leave the other side's existing clipboard
            // alone (they can still paste whatever was there before).
            Console.WriteLine(
                $"clipboard change {rawBytes.Length} bytes exceeds auto-sync threshold "
                + $"({_clipboardAutoSyncBytes}); use manual 'c' to propagate.");
            UpdateSyncedClipboardHash(rawBytes);
            return;
        }

        string encoded = Convert.ToBase64String(rawBytes);

        try
        {
            port.WriteLine($"CLIPBOARD DATA TEXT={encoded}");
            UpdateSyncedClipboardHash(rawBytes);
            Console.WriteLine($"clipboard auto-sync sent ({rawBytes.Length} bytes)");
        }
        catch (Exception ex) when (IsSerialException(ex))
        {
            Console.WriteLine($"clipboard auto-sync send error: {ex.Message}");
        }
    }

    private sealed class ClipboardWindow : Form
    {
        private const int WmClipboardupdate = 0x031D;
        private const int WmHotkey = 0x0312;
        private const int PushHotkeyId = 0xB001;
        private const int SwitchHotkeyId = 0xB002;

        // Windows modifier flags for RegisterHotKey.
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModNorepeat = 0x4000;

        private readonly Action _onChange;
        private readonly Action? _onPushHotkey;
        private readonly Action? _onSwitchHotkey;
        private bool _pushHotkeyRegistered;
        private bool _switchHotkeyRegistered;

        public ClipboardWindow(Action onChange,  Action? onPushHotkey, Action? onSwitchHotkey)
        {
            _onChange = onChange;
            _onPushHotkey = onPushHotkey;
            _onSwitchHotkey = onSwitchHotkey;

            // Message-only, never shown, never in taskbar.
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new System.Drawing.Point(-2000, -2000);
            Size = new System.Drawing.Size(1, 1);
            Opacity = 0;
            Load += (_, _) => Hide();

            AddClipboardFormatListener(Handle);
            TryRegisterHotkeys();
        }

        private void TryRegisterHotkeys()
        {
            if (_clipboardPushHotkeyEnabled && _onPushHotkey is not null)
            {
                _pushHotkeyRegistered = RegisterSingleHotkey(
                    PushHotkeyId, "clipboard push",
                    _clipboardPushHotkeyCtrl, _clipboardPushHotkeyShift, _clipboardPushHotkeyAlt,
                    _clipboardPushHotkeyKey);
            }
 
            if (_switchHotkeyEnabled && _onSwitchHotkey is not null)
            {
                _switchHotkeyRegistered = RegisterSingleHotkey(
                    SwitchHotkeyId, "switch",
                    _switchHotkeyCtrl, _switchHotkeyShift, _switchHotkeyAlt,
                    _switchHotkeyKey);
            }
        }
 
        private bool RegisterSingleHotkey(int id, string label, bool ctrl, bool shift, bool alt, string key)
        {
            uint mods = ModNorepeat;
            if (ctrl) mods |= ModControl;
            if (shift) mods |= ModShift;
            if (alt) mods |= ModAlt;
 
            if (string.IsNullOrEmpty(key))
            {
                Console.WriteLine($"{label} hotkey enabled but no key configured; skipping.");
                return false;
            }
 
            // Convert the configured key char to a virtual-key code. For
            // A-Z and 0-9 the VK code equals the ASCII code of the
            // uppercase character.
            uint vk = key[0];
 
            bool ok = RegisterHotKey(Handle, id, mods, vk);
            string combo =
                (ctrl ? "Ctrl+" : "")
                + (shift ? "Shift+" : "")
                + (alt ? "Alt+" : "")
                + key;
            if (ok)
            {
                Console.WriteLine($"{label} hotkey registered: {combo}");
            }
            else
            {
                Console.WriteLine(
                    $"{label} hotkey registration failed ({combo}); another app may own this combo.");
            }
            return ok;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmClipboardupdate)
            {
                try
                {
                    _onChange();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Clipboard change handler error: {ex.Message}");
                }
            }
            else if (m.Msg == WmHotkey)
            {
                int hotkeyId = m.WParam.ToInt32();
                if (hotkeyId == PushHotkeyId)
                {
                    try
                    {
                        _onPushHotkey?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Clipboard push hotkey handler error: {ex.Message}");
                    }
                }
                else if (hotkeyId == SwitchHotkeyId)
                {
                    try
                    {
                        _onSwitchHotkey?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Switch hotkey handler error: {ex.Message}");
                    }
                }
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (_pushHotkeyRegistered)
                {
                    UnregisterHotKey(Handle, PushHotkeyId);
                }
                if (_switchHotkeyRegistered)
                {
                    UnregisterHotKey(Handle, SwitchHotkeyId);
                }
                RemoveClipboardFormatListener(Handle);
            }
            catch
            {
            }
            base.Dispose(disposing);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private static bool IsSerialException(Exception ex)
    {
        return ex is IOException
            || ex is UnauthorizedAccessException
            || ex is InvalidOperationException
            || ex is TimeoutException;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(SystemMetric smIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private enum SystemMetric
    {
        SM_XVIRTUALSCREEN = 76,
        SM_YVIRTUALSCREEN = 77,
        SM_CXVIRTUALSCREEN = 78,
        SM_CYVIRTUALSCREEN = 79
    }
}