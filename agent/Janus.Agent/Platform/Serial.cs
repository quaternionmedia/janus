using Janus.Agent.Clipboard;
using Janus.Agent.Settings;
using System.Globalization;
using System.IO;
using System.IO.Ports;

namespace Janus.Agent.Platform;

// Janus agent's wire I/O. Owns the active serial session (port +
// device id + active-target flag), the receive loop, and the periodic
// display/cursor send.
//
// Inbound lines are parsed here and dispatched by verb. CURSOR SET is
// handled inline because it sits directly on Win32.SetCursorPos.
// Clipboard verbs fan out to ClipboardSync.X handlers.

internal static class Serial
{
    // ---- Public state -------------------------------------------------
    //
    // Set internally by BeginSession / EndSession / HandleIncomingLine.
    // External readers (clipboard outbound code, switch triggers, the
    // GUI's status sidebar) consume these to send via the active port
    // or render status; they never write them.

    public static SerialPort? ActivePort { get; private set; }
    public static string? ActiveDeviceId { get; private set; }
    public static bool IsActiveTarget { get; private set; }

    /// <summary>The target the controller is currently routing input to,
    /// as last reported via a TARGET broadcast. "P" or "W", or null if
    /// we haven't heard from the controller yet (or the session was
    /// reset). Distinct from IsActiveTarget, which only tells us
    /// whether THIS PC is the active one.</summary>
    public static string? CurrentTarget { get; private set; }

    /// <summary>UTC timestamp of the last inbound line received from
    /// the controller. Updated at the top of HandleIncomingLine. Used
    /// by the GUI's "last activity" sidebar field. DateTime.MinValue
    /// before the first line arrives (or after a session reset).
    /// </summary>
    public static DateTime LastActivityUtc { get; private set; } = DateTime.MinValue;

    // ---- Private state ------------------------------------------------
    //
    // Per-session caches reset at BeginSession. Not visible outside Serial.

    private static DateTime _lastCursorSentUtc = DateTime.MinValue;
    private static int _lastCursorX = int.MinValue;
    private static int _lastCursorY = int.MinValue;
    private static string? _lastDisplayMessage;
    private static DateTime _lastDisplaySentUtc = DateTime.MinValue;
    private static bool _displaySentForCurrentConnection;

    // ---- Session lifecycle --------------------------------------------

    public static void BeginSession(SerialPort port, string deviceId)
    {
        _displaySentForCurrentConnection = false;
        _lastDisplaySentUtc = DateTime.MinValue;
        IsActiveTarget = false;
        CurrentTarget = null;
        LastActivityUtc = DateTime.MinValue;
        _lastCursorSentUtc = DateTime.MinValue;
        _lastCursorX = int.MinValue;
        _lastCursorY = int.MinValue;
        ActivePort = port;
        ActiveDeviceId = deviceId;
    }

    public static void EndSession()
    {
        ActivePort = null;
        ActiveDeviceId = null;
        // Keep CurrentTarget and LastActivityUtc as-is on session end so
        // the GUI's status sidebar shows the LAST known state rather
        // than going blank during reconnect cycles. They reset cleanly
        // on the next BeginSession.
    }

    /// <summary>Request a serial reconnect by closing the current port.
    /// The main reconnect loop sees ActivePort go null + the receive loop
    /// erroring out, iterates, and reopens via TryOpenPort. Safe to call
    /// from any thread, including when no port is open (no-op in that
    /// case). Used by the tray menu's Reconnect item.</summary>
    public static void RequestReconnect()
    {
        SerialPort? port = ActivePort;
        if (port is null) return;

        try
        {
            port.Close();
        }
        catch
        {
            // The reconnect loop's finally will Dispose anyway; swallow
            // here so a transient error doesn't bubble into the tray
            // click handler.
        }
    }
    
    // ---- Port open ----------------------------------------------------

    public static SerialPort? TryOpenPort(string portName)
    {
        SerialPort? port = null;
        try
        {
            port = new(portName, Config.SerialBaud)
            {
                NewLine = "\n",
                ReadTimeout = Config.SerialReadTimeoutMs,
                WriteTimeout = Config.SerialWriteTimeoutMs,
                DtrEnable = true,
                RtsEnable = true,
                ReadBufferSize = Config.SerialReadBufferSize,
                WriteBufferSize = Config.SerialWriteBufferSize,
            };
            port.Open();
            return port;
        }
        catch (Exception ex) when (IsSerialException(ex))
        {
            Log.Serial.Warn(ex, "Open failed for {PortName}.", portName);
            port?.Dispose();
            return null;
        }
    }

    // ---- Receive loop -------------------------------------------------

    public static void RunReceiveLoop(SerialPort port, CancellationToken cancellationToken, string deviceId)
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
                Log.Serial.Warn(ex, "Receive error.");
                break;
            }
            catch (Exception ex)
            {
                // A malformed line (bad int parse, unexpected format, etc.)
                // must not kill this task. Log and continue; the serial
                // link itself is still healthy.
                Log.Serial.Error(ex, "Receive handler error; skipping line.");
                continue;
            }
        }
    }

    private static void HandleIncomingLine(string line, SerialPort port, string deviceId)
    {
        // Any inbound line is proof the controller is alive. Refresh
        // the last-activity timestamp first thing so the GUI's sidebar
        // updates promptly regardless of which verb follows.
        LastActivityUtc = DateTime.UtcNow;

        if (line.StartsWith("TARGET ", StringComparison.Ordinal))
        {
            string activeTarget = line[7..];
            bool wasActive = IsActiveTarget;
            CurrentTarget = activeTarget;
            IsActiveTarget = string.Equals(activeTarget, deviceId, StringComparison.Ordinal);

            // Transition to active: invalidate the cached cursor so the
            // next SendCursorIfNeeded tick treats the real position as
            // "changed" and pushes it to the router. Without this, the
            // router's belief of our cursor position can lag the real
            // one by an entire session until the next real cursor move.
            if (!wasActive && IsActiveTarget)
            {
                _lastCursorX = int.MinValue;
                _lastCursorY = int.MinValue;
                _lastCursorSentUtc = DateTime.MinValue;
                Log.Switch.Debug("=== ACTIVE TARGET: {ActiveTarget} ===", activeTarget);
            }
            else if (wasActive && !IsActiveTarget)
            {
                // Transitioned away from us
                Log.Switch.Debug("=== ACTIVE TARGET: {ActiveTarget} ===", activeTarget);
            }

            return;
        }

        if (line.Equals("CLIPBOARD REQUEST", StringComparison.Ordinal))
        {
            ClipboardSync.HandleRequest(port);
            return;
        }

        if (line.Equals("CLIPBOARD CLEAR", StringComparison.Ordinal))
        {
            ClipboardSync.HandleClear();
            return;
        }

        if (line.StartsWith("CLIPBOARD SET ", StringComparison.Ordinal))
        {
            ClipboardSync.HandleSet(line);
            return;
        }

        if (line.StartsWith("CURSOR SET ", StringComparison.Ordinal))
        {
            HandleCursorSet(line);
            return;
        }

        // MOUSE MOVE / MOUSE BUTTON / MOUSE WHEEL / MOUSE HWHEEL / KEY
        // are now consumed on the Pico and never reach the agent. If
        // one ever shows up here it means the Pico's HID routing failed
        // -- log it as a warning rather than silently ignoring.
        if (line.StartsWith("MOUSE ", StringComparison.Ordinal)
            || line.StartsWith("KEY ", StringComparison.Ordinal))
        {
            Log.System.Warn("unexpected input message reached agent: {Line}", line);
        }
        else
        {
            Log.Mouse.Warn("CURSOR SET parse error: {Line}", line);
        }
    }

    private static void HandleCursorSet(string line)
    {
        // "CURSOR SET X=1234 Y=567"
        // Kept on the agent (not handed off to Pico HID) because HID
        // mouse reports are relative-only -- there's no standard way
        // to request absolute cursor positioning. Win32.SetCursorPos
        // is the primitive that does this directly.
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        int? x = null;
        int? y = null;

        foreach (string part in parts)
        {
            if (part.StartsWith("X=", StringComparison.Ordinal))
            {
                if (!int.TryParse(part["X=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedX))
                {
                    Log.Mouse.Warn("CURSOR SET parse error: {Line}", line);
                    return;
                }
                x = parsedX;
            }
            else if (part.StartsWith("Y=", StringComparison.Ordinal))
            {
                if (!int.TryParse(part["Y=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedY))
                {
                    Log.Mouse.Warn("CURSOR SET parse error: {Line}", line);
                    return;
                }
                y = parsedY;
            }
        }

        if (x is null || y is null)
        {
            return;
        }

        Win32.SetCursorPos(x.Value, y.Value);
        Log.Mouse.Verbose("cursor set: {X}, {Y}", x.Value, y.Value);
    }

    // ---- Outbound: display + cursor sync ------------------------------

    public static void SendDisplayIfChanged(SerialPort port, string deviceId)
    {
        int left = Win32.GetSystemMetrics(Win32.SystemMetric.SM_XVIRTUALSCREEN);
        int top = Win32.GetSystemMetrics(Win32.SystemMetric.SM_YVIRTUALSCREEN);
        int width = Win32.GetSystemMetrics(Win32.SystemMetric.SM_CXVIRTUALSCREEN);
        int height = Win32.GetSystemMetrics(Win32.SystemMetric.SM_CYVIRTUALSCREEN);

        string displayMessage = $"DISPLAY {deviceId} L={left} T={top} W={width} H={height}";
        bool changed = _lastDisplayMessage != displayMessage;

        bool refreshDue = DateTime.UtcNow - _lastDisplaySentUtc >= TimeSpan.FromSeconds(Config.TimingDisplayRefreshSeconds);

        if (!changed && _displaySentForCurrentConnection && !refreshDue)
        {
            return;
        }

        port.WriteLine(displayMessage);
        if (changed)
        {
            Log.System.Debug("display sent: {DisplayMessage}", displayMessage);
        }

        _lastDisplayMessage = displayMessage;
        _displaySentForCurrentConnection = true;
        _lastDisplaySentUtc = DateTime.UtcNow;
    }

    public static void SendCursorIfNeeded(SerialPort port, string deviceId)
    {
        if (!IsActiveTarget)
        {
            return;
        }

        if (DateTime.UtcNow - _lastCursorSentUtc < TimeSpan.FromMilliseconds(Config.TimingCursorSendIntervalMs))
        {
            return;
        }

        if (!Win32.GetCursorPos(out Win32.POINT point))
        {
            return;
        }

        bool changed = point.X != _lastCursorX || point.Y != _lastCursorY;
        bool keepaliveDue = DateTime.UtcNow - _lastCursorSentUtc >= TimeSpan.FromSeconds(Config.TimingCursorKeepaliveSeconds);

        if (!changed && !keepaliveDue)
        {
            return;
        }

        port.WriteLine($"CURSOR {deviceId} X={point.X} Y={point.Y}");
        // NOTE: Below log is too noisy, would spam log file to ~2M lines per day. 
        //       Keep it commented out unless debugging cursor issues.
        //Log.Mouse.Verbose("cursor sent: {PointX}, {PointY}", point.X, point.Y);
        _lastCursorX = point.X;
        _lastCursorY = point.Y;
        _lastCursorSentUtc = DateTime.UtcNow;
    }

    // ---- Util ---------------------------------------------------------

    public static bool IsSerialException(Exception ex)
    {
        return ex is IOException
            || ex is UnauthorizedAccessException
            || ex is InvalidOperationException
            || ex is TimeoutException;
    }
}