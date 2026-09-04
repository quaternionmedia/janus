using Janus.Agent.Platform;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;

namespace Janus.Agent.Gui.ViewModels;

// View model behind MainView. Holds the observable state MainView's
// XAML binds to:
//
//   Status group:      StatusText, StatusDot, PortInfo, IsConnected
//   This-PC group:     ThisPc
//   Active target:     ActiveTarget, ActiveTargetSuffix
//   Last activity:     LastActivity
//   Log:               LogLines (ObservableCollection<LogLine>)
//   Search:            SearchText -- live filter over the log
//
// Two update paths into the live state:
//   * Periodic (500 ms DispatcherTimer) -- refreshes the status group
//     and IsConnected from Serial's statics.
//   * Reactive (LogSink.LineAdded) -- appends a new LogLine to the
//     collection, marshalling onto the dispatcher.
//
// Settings-modal Cfg* properties moved out of this VM in the stage 1
// split; they now live on GuiViewModel because the modal is a
// window-level UI element (shared across Main and Diag tabs).

internal sealed class MainViewModel : INotifyPropertyChanged
{
    private const int MaxLinesDisplayed = 5000;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _statusTimer;
    private readonly Action<LogLine> _onLineAdded;

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

    private string _portInfo = "\u2014";
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

    private string _activeTarget = "\u2014";
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

    private string _lastActivity = "\u2014";
    public string LastActivity
    {
        get => _lastActivity;
        private set { if (_lastActivity != value) { _lastActivity = value; Raise(); } }
    }

    // Bound to the action buttons' IsEnabled through DataContext
    // inheritance into the ActionsPanel UserControl. When the serial
    // port is down, Switch / Send-clipboard / Reconnect would all be
    // no-ops on their underlying static methods; disabling the buttons
    // makes that state visible.
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
            CollectionViewSource.GetDefaultView(LogLines).Refresh();
        }
    }

    // ---- Construction ------------------------------------------------

    public MainViewModel(Dispatcher dispatcher, string deviceId)
    {
        _dispatcher = dispatcher;
        ThisPc = deviceId == "P" ? "Personal (P)" : "Work (W)";

        var view = CollectionViewSource.GetDefaultView(LogLines);
        view.Filter = LogFilter;

        foreach (LogLine line in LogSink.Snapshot())
        {
            LogLines.Add(line);
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
        return line.Message.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Reactive path: new log line ---------------------------------

    private void OnLineAdded(LogLine line)
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

    private void AppendLogLine(LogLine line)
    {
        LogLines.Add(line);
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
        PortInfo = connected ? $"{port!.PortName} \u00B7 {port.BaudRate}" : "\u2014";

        string? currentTarget = Serial.CurrentTarget;
        if (currentTarget is null)
        {
            ActiveTarget = "\u2014";
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
            ? "\u2014"
            : FormatRelative(DateTime.UtcNow - lastUtc);
    }

    private static string FormatRelative(TimeSpan since)
    {
        if (since.TotalSeconds < 0) return "just now";
        if (since.TotalSeconds < 2)  return "just now";
        if (since.TotalSeconds < 60) return $"{(int)since.TotalSeconds} sec ago";
        if (since.TotalMinutes < 60) return $"{(int)since.TotalMinutes} min ago";
        if (since.TotalHours < 24)   return $"{(int)since.TotalHours} hr ago";
        return $"{(int)since.TotalDays} d ago";
    }

    // ---- INPC plumbing -----------------------------------------------

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}