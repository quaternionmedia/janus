using Janus.Agent.Logging;
using Janus.Agent.Platform;
using Janus.Agent.Settings;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;

namespace Janus.Agent.Gui;

// View model behind GuiWindow. Holds the observable state the XAML
// binds to:
//
//   Status group:      StatusText, StatusDot, PortInfo, IsConnected
//   This-PC group:     ThisPc
//   Active target:     ActiveTarget, ActiveTargetSuffix
//   Last activity:     LastActivity
//   Log:               LogLines (ObservableCollection)
//   Search:            SearchText -- live filter over the log
//   Settings display:  Cfg* properties (read-only snapshots of Config)
//
// Two update paths into the live state:
//   * Periodic (500 ms DispatcherTimer) -- refreshes the status group
//     and IsConnected from Serial's statics.
//   * Reactive (LogSink.LineAdded) -- appends a new LogLine to the
//     collection, marshalling onto the dispatcher.
//
// The Cfg* properties are static snapshots: Config is loaded once at
// startup and doesn't change during runtime, so we don't bother with
// INPC notifications for them. If/when #3d adds live config editing,
// they'll need to start raising change events.

internal sealed class GuiViewModel : INotifyPropertyChanged
{
    private const int MaxLinesDisplayed = 5000;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _statusTimer;
    private readonly Action<string> _onLineAdded;

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- Public bindings: live state ---------------------------------

    public ObservableCollection<LogLine> LogLines { get; } = new();

    private string _statusText = "Disconnected";
    public string StatusText
    {
        get => _statusText;
        private set { if (_statusText != value) { _statusText = value; Raise(); } }
    }

    private Brush _statusDot = LogLineColors.Error;
    public Brush StatusDot
    {
        get => _statusDot;
        private set { if (!ReferenceEquals(_statusDot, value)) { _statusDot = value; Raise(); } }
    }

    private string _portInfo = "—";
    public string PortInfo
    {
        get => _portInfo;
        private set { if (_portInfo != value) { _portInfo = value; Raise(); } }
    }

    private string _thisPc = string.Empty;
    public string ThisPc
    {
        get => _thisPc;
        private set { if (_thisPc != value) { _thisPc = value; Raise(); } }
    }

    private string _activeTarget = "—";
    public string ActiveTarget
    {
        get => _activeTarget;
        private set { if (_activeTarget != value) { _activeTarget = value; Raise(); } }
    }

    private string _activeTargetSuffix = string.Empty;
    public string ActiveTargetSuffix
    {
        get => _activeTargetSuffix;
        private set { if (_activeTargetSuffix != value) { _activeTargetSuffix = value; Raise(); } }
    }

    private string _lastActivity = "—";
    public string LastActivity
    {
        get => _lastActivity;
        private set { if (_lastActivity != value) { _lastActivity = value; Raise(); } }
    }

    // Bound to the action buttons' IsEnabled. When the serial port is
    // down, Switch / Send-clipboard / Reconnect would all be no-ops on
    // their underlying static methods (they early-out if ActivePort is
    // null). Disabling the buttons makes that visible to the user.
    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set { if (_isConnected != value) { _isConnected = value; Raise(); } }
    }

    // ---- Search / filter ---------------------------------------------

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            Raise();
            // Trigger a re-evaluation of the filter predicate against
            // every item in LogLines. Cheap enough for our 5000-line
            // cap; if profiling ever shows it as a hot path, swap to a
            // debounced refresh.
            CollectionViewSource.GetDefaultView(LogLines).Refresh();
        }
    }

    // ---- Settings display (read-only snapshots of Config) ------------

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

    // ---- Construction ------------------------------------------------

    public GuiViewModel(Dispatcher dispatcher, string deviceId)
    {
        _dispatcher = dispatcher;
        ThisPc = deviceId == "P" ? "Personal (P)" : "Work (W)";

        // Install the log filter on the default view of LogLines.
        // ItemsControl bindings to "LogLines" automatically go through
        // this default view, so the predicate is consulted on every
        // collection-change event.
        var view = CollectionViewSource.GetDefaultView(LogLines);
        view.Filter = LogFilter;

        // Seed the log with any history that accumulated before the
        // GUI started.
        foreach (string line in LogSink.Snapshot())
        {
            LogLines.Add(new LogLine(line, LogLineColors.Categorize(line)));
        }
        TrimLogIfNeeded();

        _onLineAdded = OnLineAdded;
        LogSink.LineAdded += _onLineAdded;

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        RefreshStatus();
        _statusTimer.Start();
    }

    public void Shutdown()
    {
        try { _statusTimer.Stop(); } catch { }
        try { LogSink.LineAdded -= _onLineAdded; } catch { }
    }

    // ---- Log filter --------------------------------------------------

    private bool LogFilter(object item)
    {
        if (item is not LogLine line) return false;
        if (string.IsNullOrEmpty(_searchText)) return true;
        // Case-insensitive substring match. Search is purely on the
        // line text -- categories / brushes are derived from the same
        // text, so we don't need a separate field.
        return line.Text.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Reactive path: new log line ---------------------------------

    private void OnLineAdded(string line)
    {
        if (_dispatcher.CheckAccess())
        {
            AppendLogLine(line);
        }
        else
        {
            _dispatcher.BeginInvoke(new Action(() => AppendLogLine(line)));
        }
    }

    private void AppendLogLine(string line)
    {
        LogLines.Add(new LogLine(line, LogLineColors.Categorize(line)));
        TrimLogIfNeeded();
    }

    private void TrimLogIfNeeded()
    {
        while (LogLines.Count > MaxLinesDisplayed)
        {
            LogLines.RemoveAt(0);
        }
    }

    // ---- Periodic path: status refresh -------------------------------

    private void RefreshStatus()
    {
        var port = Serial.ActivePort;
        bool connected = port?.IsOpen == true;

        IsConnected = connected;
        StatusText = connected ? "Connected" : "Disconnected";
        StatusDot = connected ? LogLineColors.Success : LogLineColors.Error;
        PortInfo = connected ? $"{port!.PortName} · {port.BaudRate}" : "—";

        string? currentTarget = Serial.CurrentTarget;
        if (currentTarget is null)
        {
            ActiveTarget = "—";
            ActiveTargetSuffix = string.Empty;
        }
        else if (Serial.IsActiveTarget)
        {
            ActiveTarget = currentTarget;
            ActiveTargetSuffix = "input lands here";
        }
        else
        {
            ActiveTarget = currentTarget;
            ActiveTargetSuffix = "on peer";
        }

        DateTime lastUtc = Serial.LastActivityUtc;
        LastActivity = lastUtc == DateTime.MinValue
            ? "—"
            : FormatRelative(DateTime.UtcNow - lastUtc);
    }

    private static string FormatRelative(TimeSpan since)
    {
        if (since.TotalSeconds < 0) return "just now";      // clock jitter guard
        if (since.TotalSeconds < 2)  return "just now";
        if (since.TotalSeconds < 60) return $"{(int)since.TotalSeconds} sec ago";
        if (since.TotalMinutes < 60) return $"{(int)since.TotalMinutes} min ago";
        if (since.TotalHours < 24)   return $"{(int)since.TotalHours} hr ago";
        return $"{(int)since.TotalDays} d ago";
    }

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

    // ---- INPC plumbing -----------------------------------------------

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}