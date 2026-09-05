using Janus.Agent.Settings;
using System.Globalization;

namespace Janus.Agent.Gui.ViewModels;

// Window-level view model. Holds state that isn't specific to a
// single tab -- currently just the Cfg* properties bound to the
// shared settings modal.
//
// The Cfg* properties are static snapshots: Config is loaded once at
// startup and doesn't change during runtime, so INPC notifications
// aren't needed. When live config editing lands, they'll start
// raising change events (or move onto a dedicated SettingsViewModel).

internal sealed class GuiViewModel
{
    // Connection
    public string CfgBaud           => Config.SerialBaud.ToString("N0", CultureInfo.InvariantCulture);
    public string CfgReadTimeout    => $"{Config.SerialReadTimeoutMs} ms";
    public string CfgWriteTimeout   => $"{Config.SerialWriteTimeoutMs} ms";
    public string CfgReadBuffer     => FormatBytes(Config.SerialReadBufferSize);
    public string CfgWriteBuffer    => FormatBytes(Config.SerialWriteBufferSize);

    // Switch triggers
    public string CfgOnLock         => Config.SwitchOnLock ? "Enabled" : "Disabled";
    public string CfgOnShutdown     => Config.SwitchOnShutdown ? "Enabled" : "Disabled";
    public string CfgSwitchConsole  => $"'{Config.SwitchConsoleKey}'";
    public string CfgSwitchHotkey   => Config.SwitchHotkeyEnabled
        ? FormatHotkey(Config.SwitchHotkeyCtrl, Config.SwitchHotkeyShift, Config.SwitchHotkeyAlt, Config.SwitchHotkeyKey)
        : "Disabled";

    // Clipboard
    public string CfgOutboundMode   => Config.ClipboardOutboundMode.ToString();
    public string CfgAutoSyncBytes  => FormatBytes(Config.ClipboardAutoSyncBytes);
    public string CfgMaxBytes       => FormatBytes(Config.ClipboardMaxBytes);
    public string CfgPushConsole    => $"'{Config.ClipboardPushConsoleKey}'";
    public string CfgPushHotkey     => Config.ClipboardPushHotkeyEnabled
        ? FormatHotkey(Config.ClipboardPushHotkeyCtrl, Config.ClipboardPushHotkeyShift, Config.ClipboardPushHotkeyAlt, Config.ClipboardPushHotkeyKey)
        : "Disabled";

    // Timing (advanced)
    public string CfgMainTick           => $"{Config.TimingMainTickMs} ms";
    public string CfgReconnectDelay     => $"{Config.TimingReconnectDelayMs} ms";
    public string CfgCursorSendInterval => $"{Config.TimingCursorSendIntervalMs} ms";
    public string CfgCursorKeepalive    => $"{Config.TimingCursorKeepaliveSeconds} s";
    public string CfgDisplayRefresh     => $"{Config.TimingDisplayRefreshSeconds} s";

    // ---- Formatting helpers ------------------------------------------

    private static string FormatHotkey(bool ctrl, bool shift, bool alt, string key)
    {
        var parts = new List<string>(4);
        if (ctrl)  parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt)   parts.Add("Alt");
        parts.Add(key);
        return string.Join("+", parts);
    }

    private static string FormatBytes(int bytes)
    {
        if (bytes >= 1024 * 1024) return $"{bytes / 1024 / 1024} MB";
        if (bytes >= 1024)        return $"{bytes / 1024} KB";
        return $"{bytes} B";
    }
}