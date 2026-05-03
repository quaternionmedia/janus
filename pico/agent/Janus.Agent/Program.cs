using System.Globalization;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

internal static class Program
{
    private const int MouseEventFMove = 0x0001;
    private const int MouseEventFLeftDown = 0x0002;
    private const int MouseEventFLeftUp = 0x0004;
    private const int MouseEventFRightDown = 0x0008;
    private const int MouseEventFRightUp = 0x0010;
    private const int MouseEventFMiddleDown = 0x0020;
    private const int MouseEventFMiddleUp = 0x0040;
    private const int MouseEventFWheel = 0x0800;
    private const int MouseEventFHwheel = 0x1000;

    private const uint KeyeventfExtendedkey = 0x0001;
    private const uint KeyeventfKeyup = 0x0002;

    // Clipboard size tiers, all in raw UTF-8 bytes (before base64):
    //   0 .. ClipboardAutoSyncBytes        -> auto-sync on change
    //   ClipboardAutoSyncBytes .. ClipboardMaxBytes
    //                                      -> only sync on manual request
    //                                         ('c' in router, or user action)
    //   > ClipboardMaxBytes                -> refuse; clear destination so
    //                                         stale content doesn't paste
    private const int ClipboardAutoSyncBytes = 16 * 1024;
    private const int ClipboardMaxBytes = 256 * 1024;

    // Serial buffers. Sized to comfortably hold a full max-size clipboard
    // line (256 KB raw -> ~341 KB base64 -> ~341 KB on the wire) plus
    // room for pipelined input events. At 921600 baud a 341 KB line
    // takes ~3.7 seconds to transmit; the buffer has to absorb the whole
    // thing because the reader may not drain it until the line completes.
    private const int SerialReadBufferSize = 1024 * 1024;
    private const int SerialWriteBufferSize = 1024 * 1024;

    // Raised read timeout so a large inbound clipboard line can finish
    // arriving without ReadLine bailing out. ReceiveLoop treats timeouts as
    // normal and just retries, but a tighter timeout causes unnecessary
    // churn in the middle of a legitimate big transfer.
    private const int SerialReadTimeoutMs = 5000;

    private static volatile bool _isActiveTarget;
    private static DateTime _lastCursorSentUtc = DateTime.MinValue;
    private static int _lastCursorX = int.MinValue;
    private static int _lastCursorY = int.MinValue;
    private static volatile bool _inputEnabled = true;
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

    private static readonly Dictionary<string, ushort> VirtualKeys = new(StringComparer.Ordinal)
    {
        ["KEY_A"] = 0x41,
        ["KEY_B"] = 0x42,
        ["KEY_C"] = 0x43,
        ["KEY_D"] = 0x44,
        ["KEY_E"] = 0x45,
        ["KEY_F"] = 0x46,
        ["KEY_G"] = 0x47,
        ["KEY_H"] = 0x48,
        ["KEY_I"] = 0x49,
        ["KEY_J"] = 0x4A,
        ["KEY_K"] = 0x4B,
        ["KEY_L"] = 0x4C,
        ["KEY_M"] = 0x4D,
        ["KEY_N"] = 0x4E,
        ["KEY_O"] = 0x4F,
        ["KEY_P"] = 0x50,
        ["KEY_Q"] = 0x51,
        ["KEY_R"] = 0x52,
        ["KEY_S"] = 0x53,
        ["KEY_T"] = 0x54,
        ["KEY_U"] = 0x55,
        ["KEY_V"] = 0x56,
        ["KEY_W"] = 0x57,
        ["KEY_X"] = 0x58,
        ["KEY_Y"] = 0x59,
        ["KEY_Z"] = 0x5A,

        ["KEY_0"] = 0x30,
        ["KEY_1"] = 0x31,
        ["KEY_2"] = 0x32,
        ["KEY_3"] = 0x33,
        ["KEY_4"] = 0x34,
        ["KEY_5"] = 0x35,
        ["KEY_6"] = 0x36,
        ["KEY_7"] = 0x37,
        ["KEY_8"] = 0x38,
        ["KEY_9"] = 0x39,

        ["KEY_SPACE"] = 0x20,
        ["KEY_ENTER"] = 0x0D,
        ["KEY_ESC"] = 0x1B,
        ["KEY_TAB"] = 0x09,
        ["KEY_BACKSPACE"] = 0x08,

        ["KEY_LEFTSHIFT"] = 0xA0,
        ["KEY_RIGHTSHIFT"] = 0xA1,
        ["KEY_LEFTCTRL"] = 0xA2,
        ["KEY_RIGHTCTRL"] = 0xA3,
        ["KEY_LEFTALT"] = 0xA4,
        ["KEY_RIGHTALT"] = 0xA5,
        ["KEY_LEFTMETA"] = 0x5B,
        ["KEY_RIGHTMETA"] = 0x5C,

        ["KEY_UP"] = 0x26,
        ["KEY_DOWN"] = 0x28,
        ["KEY_LEFT"] = 0x25,
        ["KEY_RIGHT"] = 0x27,

        ["KEY_INSERT"] = 0x2D,
        ["KEY_DELETE"] = 0x2E,
        ["KEY_HOME"] = 0x24,
        ["KEY_END"] = 0x23,
        ["KEY_PAGEUP"] = 0x21,
        ["KEY_PAGEDOWN"] = 0x22,

        ["KEY_CAPSLOCK"] = 0x14,

        ["KEY_F1"] = 0x70,
        ["KEY_F2"] = 0x71,
        ["KEY_F3"] = 0x72,
        ["KEY_F4"] = 0x73,
        ["KEY_F5"] = 0x74,
        ["KEY_F6"] = 0x75,
        ["KEY_F7"] = 0x76,
        ["KEY_F8"] = 0x77,
        ["KEY_F9"] = 0x78,
        ["KEY_F10"] = 0x79,
        ["KEY_F11"] = 0x7A,
        ["KEY_F12"] = 0x7B,

        ["KEY_MINUS"] = 0xBD,
        ["KEY_EQUAL"] = 0xBB,
        ["KEY_LEFTBRACE"] = 0xDB,
        ["KEY_RIGHTBRACE"] = 0xDD,
        ["KEY_BACKSLASH"] = 0xDC,
        ["KEY_SEMICOLON"] = 0xBA,
        ["KEY_APOSTROPHE"] = 0xDE,
        ["KEY_GRAVE"] = 0xC0,
        ["KEY_COMMA"] = 0xBC,
        ["KEY_DOT"] = 0xBE,
        ["KEY_SLASH"] = 0xBF,
    };

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

        using CancellationTokenSource cts = new();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        Console.WriteLine($"Janus.Agent [{deviceId}] started. Press Ctrl+C to stop.");
        Console.WriteLine("Press 'e' to toggle injected input on/off.");
        Console.WriteLine($"port: {portName}");
        Console.WriteLine();

        Task inputToggleTask = Task.Run(() => InputToggleLoop(cts.Token), cts.Token);

        StartClipboardMonitor();

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                using SerialPort? port = TryOpenPort(portName);

                if (port is null)
                {
                    await Task.Delay(1000, cts.Token);
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
                        await Task.Delay(50, cts.Token);
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
                    await Task.Delay(1000, cts.Token);
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

            try
            {
                await inputToggleTask;
            }
            catch
            {
            }
        }
    }

    private static SerialPort? TryOpenPort(string portName)
    {
        SerialPort? port = null;
        try
        {
            port = new(portName, 921600)
            {
                NewLine = "\n",
                ReadTimeout = SerialReadTimeoutMs,
                WriteTimeout = 5000,
                DtrEnable = true,
                RtsEnable = true,
                ReadBufferSize = SerialReadBufferSize,
                WriteBufferSize = SerialWriteBufferSize,
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

    private static void InputToggleLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(50);
                continue;
            }

            ConsoleKeyInfo key = Console.ReadKey(true);

            if (key.Key == ConsoleKey.E)
            {
                _inputEnabled = !_inputEnabled;
                Console.WriteLine($"Injected input enabled: {_inputEnabled}");
            }
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

        bool refreshDue = DateTime.UtcNow - _lastDisplaySentUtc >= TimeSpan.FromSeconds(10);

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

        if (DateTime.UtcNow - _lastCursorSentUtc < TimeSpan.FromMilliseconds(100))
        {
            return;
        }

        if (!GetCursorPos(out POINT point))
        {
            return;
        }

        bool changed = point.X != _lastCursorX || point.Y != _lastCursorY;
        bool keepaliveDue = DateTime.UtcNow - _lastCursorSentUtc >= TimeSpan.FromSeconds(2);

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

        if (!_inputEnabled)
        {
            return;
        }

        if (line.StartsWith("MOUSE MOVE ", StringComparison.Ordinal))
        {
            HandleMouseMove(line);
            return;
        }

        if (line.StartsWith("MOUSE BUTTON ", StringComparison.Ordinal))
        {
            HandleMouseButton(line);
            return;
        }

        if (line.StartsWith("MOUSE WHEEL ", StringComparison.Ordinal))
        {
            HandleMouseWheel(line);
            return;
        }

        if (line.StartsWith("MOUSE HWHEEL ", StringComparison.Ordinal))
        {
            HandleMouseHWheel(line);
            return;
        }

        if (line.StartsWith("CURSOR SET ", StringComparison.Ordinal))
        {
            HandleCursorSet(line);
            return;
        }

        if (line.StartsWith("KEY ", StringComparison.Ordinal))
        {
            HandleKey(line);
        }
    }

    private static void HandleClipboardRequest(SerialPort port)
    {
        string text = GetClipboardTextSafe();
        byte[] rawBytes = Encoding.UTF8.GetBytes(text);

        if (rawBytes.Length > ClipboardMaxBytes)
        {
            Console.WriteLine(
                $"clipboard refused (outbound): {rawBytes.Length} bytes exceeds {ClipboardMaxBytes} limit");
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

        if (rawBytes.Length > ClipboardMaxBytes)
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

    private static void HandleMouseMove(string line)
    {
        int dx = 0;
        int dy = 0;

        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (string part in parts)
        {
            if (part.StartsWith("DX=", StringComparison.Ordinal))
            {
                if (!int.TryParse(part["DX=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out dx))
                {
                    Console.WriteLine($"MOUSE MOVE parse error: {line}");
                    return;
                }
            }
            else if (part.StartsWith("DY=", StringComparison.Ordinal))
            {
                if (!int.TryParse(part["DY=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out dy))
                {
                    Console.WriteLine($"MOUSE MOVE parse error: {line}");
                    return;
                }
            }
        }

        //Console.WriteLine($"HANDLE MOUSE MOVE dx={dx} dy={dy}");
        
        mouse_event(MouseEventFMove, dx, dy, 0, UIntPtr.Zero);
    }

    private static void HandleMouseButton(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 3)
        {
            return;
        }

        uint flags = parts[2] switch
        {
            "LEFT=DOWN" => MouseEventFLeftDown,
            "LEFT=UP" => MouseEventFLeftUp,
            "RIGHT=DOWN" => MouseEventFRightDown,
            "RIGHT=UP" => MouseEventFRightUp,
            "MIDDLE=DOWN" => MouseEventFMiddleDown,
            "MIDDLE=UP" => MouseEventFMiddleUp,
            _ => 0
        };

        if (flags != 0)
        {
            mouse_event(flags, 0, 0, 0, UIntPtr.Zero);
        }
    }

    private static void HandleMouseWheel(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (string part in parts)
        {
            if (part.StartsWith("DELTA=", StringComparison.Ordinal))
            {
                if (!int.TryParse(part["DELTA=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int delta))
                {
                    Console.WriteLine($"MOUSE WHEEL parse error: {line}");
                    return;
                }
                mouse_event(MouseEventFWheel, 0, 0, delta * 120, UIntPtr.Zero);
                return;
            }
        }
    }

    private static void HandleMouseHWheel(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (string part in parts)
        {
            if (part.StartsWith("DELTA=", StringComparison.Ordinal))
            {
                if (!int.TryParse(part["DELTA=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int delta))
                {
                    Console.WriteLine($"MOUSE HWHEEL parse error: {line}");
                    return;
                }
                mouse_event(MouseEventFHwheel, 0, 0, delta * 120, UIntPtr.Zero);
                return;
            }
        }
    }

    private static void HandleCursorSet(string line)
    {
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

    private static void HandleKey(string line)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        string? keyName = null;
        string? state = null;

        foreach (string part in parts)
        {
            if (part.StartsWith("NAME=", StringComparison.Ordinal))
            {
                keyName = part["NAME=".Length..];
            }
            else if (part.StartsWith("STATE=", StringComparison.Ordinal))
            {
                state = part["STATE=".Length..];
            }
        }

        if (string.IsNullOrWhiteSpace(keyName) || string.IsNullOrWhiteSpace(state))
        {
            return;
        }

        if (!VirtualKeys.TryGetValue(keyName, out ushort vk))
        {
            return;
        }

        uint flags = 0;

        if (IsExtendedKey(keyName))
        {
            flags |= KeyeventfExtendedkey;
        }

        if (state.Equals("UP", StringComparison.Ordinal))
        {
            flags |= KeyeventfKeyup;
        }

        keybd_event((byte)vk, 0, flags, UIntPtr.Zero);
    }

    private static bool IsExtendedKey(string keyName)
    {
        return keyName is
            "KEY_UP" or
            "KEY_DOWN" or
            "KEY_LEFT" or
            "KEY_RIGHT" or
            "KEY_INSERT" or
            "KEY_DELETE" or
            "KEY_HOME" or
            "KEY_END" or
            "KEY_PAGEUP" or
            "KEY_PAGEDOWN" or
            "KEY_RIGHTALT" or
            "KEY_RIGHTCTRL";
    }

    private static string GetClipboardTextSafe()
    {
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

    private static void SetClipboardTextSafe(string text)
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
    // change. When one arrives, compare hash against the last synced
    // value; if different, push the new clipboard to the active port.
    //
    // The hash check is what keeps the two-agent loop from bouncing
    // forever: incoming CLIPBOARD SET on agent A updates the hash before
    // SetText fires, so A's own monitor callback sees hash-match and
    // suppresses the re-broadcast.

    private static void StartClipboardMonitor()
    {
        _clipboardThread = new Thread(() =>
        {
            try
            {
                _clipboardWindow = new ClipboardWindow(OnClipboardChanged);
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
        // we already broadcast).
        if (IsAlreadySynced(rawBytes))
        {
            return;
        }

        if (rawBytes.Length > ClipboardMaxBytes)
        {
            // Over hard ceiling: refuse and tell the other side to clear
            // its clipboard so a stale value doesn't paste silently.
            Console.WriteLine(
                $"clipboard auto-sync refused: {rawBytes.Length} bytes exceeds {ClipboardMaxBytes} hard limit");
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

        if (rawBytes.Length > ClipboardAutoSyncBytes)
        {
            // Between auto-sync and hard ceiling: don't broadcast. User
            // can trigger a manual 'c' in the router if they want this
            // on the other side. Leave the other side's existing clipboard
            // alone (they can still paste whatever was there before).
            Console.WriteLine(
                $"clipboard change {rawBytes.Length} bytes exceeds auto-sync threshold "
                + $"({ClipboardAutoSyncBytes}); use manual 'c' to propagate.");
            // Record hash so subsequent identical monitor events don't log
            // again. A genuinely new copy (different hash) will re-trigger
            // this path and re-log.
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
        private readonly Action _onChange;

        public ClipboardWindow(Action onChange)
        {
            _onChange = onChange;

            // Message-only, never shown, never in taskbar.
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new System.Drawing.Point(-2000, -2000);
            Size = new System.Drawing.Size(1, 1);
            Opacity = 0;
            Load += (_, _) => Hide();

            AddClipboardFormatListener(Handle);
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
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                RemoveClipboardFormatListener(Handle);
            }
            catch
            {
            }
            base.Dispose(disposing);
        }
    }

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
    private static extern void mouse_event(uint dwFlags, int dx, int dy, int dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

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