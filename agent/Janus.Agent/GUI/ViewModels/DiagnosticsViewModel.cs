using Janus.Agent.Platform;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace Janus.Agent.Gui.ViewModels;

// View model behind DiagnosticsView. Same shape as MainViewModel's
// log-handling side (LogLines, SearchText, PassesFilter, status
// refresh for IsConnected) minus the status-block state MainViewModel
// tracks (StatusText, PortInfo, ActiveTarget, etc.).
//
// Stage 4 will extend PassesFilter to include level, category,
// source, and time-range predicates alongside the current substring
// search.

internal sealed class DiagnosticsViewModel : INotifyPropertyChanged
{
    private const int MaxLinesDisplayed = 5000;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _statusTimer;
    private readonly Action<LogLine> _onLineAdded;

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- Public bindings ---------------------------------------------

    public ObservableCollection<LogLine> LogLines { get; } = new();

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set { if (_isConnected != value) { _isConnected = value; Raise(); } }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            Raise();
        }
    }

    /// <summary>Filter predicate consulted by LogDocumentSync. Public
    /// so the view can pass it as a Func delegate at sync
    /// construction.</summary>
    public bool PassesFilter(LogLine line)
    {
        if (string.IsNullOrEmpty(_searchText)) return true;
        return line.Message.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Construction ------------------------------------------------

    public DiagnosticsViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

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
        IsConnected = port?.IsOpen == true;
    }

    // ---- INPC plumbing -----------------------------------------------

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}