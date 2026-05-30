using System.Text.Json;

// Janus agent configuration. Loaded once at startup from appsettings.json
// (in the executable's directory). All values default to the constants
// below; missing JSON fields or a missing config file fall through to
// defaults so the agent still starts.
//
// External modules read settings via Config.X properties. The private
// setters keep the public surface effectively read-only: only Config
// itself mutates them, during Load().

internal static class Config
{
    // ---- Serial port ------------------------------------------------------

    public static int SerialBaud { get; private set; } = 921600;
    public static int SerialReadTimeoutMs { get; private set; } = 5000;
    public static int SerialWriteTimeoutMs { get; private set; } = 5000;
    public static int SerialReadBufferSize { get; private set; } = 1024 * 1024;
    public static int SerialWriteBufferSize { get; private set; } = 1024 * 1024;

    // ---- Main loop timing -------------------------------------------------

    public static int TimingMainTickMs { get; private set; } = 50;
    public static int TimingReconnectDelayMs { get; private set; } = 1000;
    public static int TimingCursorSendIntervalMs { get; private set; } = 100;
    public static int TimingCursorKeepaliveSeconds { get; private set; } = 2;
    public static int TimingDisplayRefreshSeconds { get; private set; } = 10;

    // ---- Clipboard policy -------------------------------------------------

    public static int ClipboardAutoSyncBytes { get; private set; } = 16 * 1024;
    public static int ClipboardMaxBytes { get; private set; } = 256 * 1024;
    public static ClipboardOutboundMode ClipboardOutboundMode { get; private set; } = ClipboardOutboundMode.Auto;

    // ---- Manual clipboard-push triggers -----------------------------------
    //
    // Single key in the agent's own console window, OR a global hotkey
    // (works from any focused window). Both call PushClipboardToPeer.

    public static string ClipboardPushConsoleKey { get; private set; } = "c";
    public static bool ClipboardPushHotkeyEnabled { get; private set; } = false;
    public static bool ClipboardPushHotkeyCtrl { get; private set; } = true;
    public static bool ClipboardPushHotkeyShift { get; private set; } = true;
    public static bool ClipboardPushHotkeyAlt { get; private set; } = false;
    public static string ClipboardPushHotkeyKey { get; private set; } = "C";

    // ---- Switch-to-peer triggers -----------------------------------------
    //
    // Mirrors the clipboard-push pattern. Sends "SWITCH PEER" to the
    // controller, which performs the switch to the OTHER side.

    public static string SwitchConsoleKey { get; private set; } = "s";
    public static bool SwitchHotkeyEnabled { get; private set; } = false;
    public static bool SwitchHotkeyCtrl { get; private set; } = true;
    public static bool SwitchHotkeyShift { get; private set; } = true;
    public static bool SwitchHotkeyAlt { get; private set; } = false;
    public static string SwitchHotkeyKey { get; private set; } = "S";

    // ---- Load -------------------------------------------------------------

    public static void Load()
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

            ApplySerial(cfg.Serial);
            ApplyClipboard(cfg.Clipboard);
            ApplyTiming(cfg.Timing);
            ApplySwitch(cfg.Switch);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load appsettings.json: {ex.Message}. Using defaults.");
        }
    }

    private static void ApplySerial(SerialSection? section)
    {
        if (section is null) return;
        if (section.Baud > 0) SerialBaud = section.Baud;
        if (section.ReadTimeoutMs > 0) SerialReadTimeoutMs = section.ReadTimeoutMs;
        if (section.WriteTimeoutMs > 0) SerialWriteTimeoutMs = section.WriteTimeoutMs;
        if (section.ReadBufferSize > 0) SerialReadBufferSize = section.ReadBufferSize;
        if (section.WriteBufferSize > 0) SerialWriteBufferSize = section.WriteBufferSize;
    }

    private static void ApplyTiming(TimingSection? section)
    {
        if (section is null) return;
        if (section.MainTickMs > 0) TimingMainTickMs = section.MainTickMs;
        if (section.ReconnectDelayMs > 0) TimingReconnectDelayMs = section.ReconnectDelayMs;
        if (section.CursorSendIntervalMs > 0) TimingCursorSendIntervalMs = section.CursorSendIntervalMs;
        if (section.CursorKeepaliveSeconds > 0) TimingCursorKeepaliveSeconds = section.CursorKeepaliveSeconds;
        if (section.DisplayRefreshSeconds > 0) TimingDisplayRefreshSeconds = section.DisplayRefreshSeconds;
    }

    private static void ApplyClipboard(ClipboardSection? section)
    {
        if (section is null) return;
        if (section.AutoSyncBytes > 0) ClipboardAutoSyncBytes = section.AutoSyncBytes;
        if (section.MaxBytes > 0) ClipboardMaxBytes = section.MaxBytes;

        if (!string.IsNullOrWhiteSpace(section.OutboundMode))
        {
            ClipboardOutboundMode = section.OutboundMode.Trim().ToLowerInvariant() switch
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
            ClipboardPushConsoleKey = push.ConsoleKey.Trim().ToLowerInvariant();
        }
        ClipboardPushHotkeyEnabled = push.HotkeyEnabled;
        ClipboardPushHotkeyCtrl = push.HotkeyCtrl;
        ClipboardPushHotkeyShift = push.HotkeyShift;
        ClipboardPushHotkeyAlt = push.HotkeyAlt;
        if (!string.IsNullOrWhiteSpace(push.HotkeyKey))
        {
            ClipboardPushHotkeyKey = push.HotkeyKey.Trim().ToUpperInvariant();
        }
    }

    private static void ApplySwitch(SwitchSection? section)
    {
        if (section is null) return;
        if (!string.IsNullOrWhiteSpace(section.ConsoleKey))
        {
            SwitchConsoleKey = section.ConsoleKey.Trim().ToLowerInvariant();
        }
        SwitchHotkeyEnabled = section.HotkeyEnabled;
        SwitchHotkeyCtrl = section.HotkeyCtrl;
        SwitchHotkeyShift = section.HotkeyShift;
        SwitchHotkeyAlt = section.HotkeyAlt;
        if (!string.IsNullOrWhiteSpace(section.HotkeyKey))
        {
            SwitchHotkeyKey = section.HotkeyKey.Trim().ToUpperInvariant();
        }
    }

    // ---- JSON deserialization shapes (private implementation detail) -----

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
}

internal enum ClipboardOutboundMode
{
    Auto,
    Manual,
}