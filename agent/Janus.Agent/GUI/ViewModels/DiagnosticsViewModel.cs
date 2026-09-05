using Janus.Agent.Gui.Shared;
using Janus.Agent.Platform;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Threading;

namespace Janus.Agent.Gui.ViewModels;

// View model behind DiagnosticsView.
//
// FILES ONLY. Diag does not subscribe to LogSink. Every event
// shown here comes from disk (via HistoricalLogLoader). This is a
// deliberate split from MainViewModel, which uses the live LogSink
// stream for its at-a-glance view. Main = realtime, Diag = files.
//
// When SelectedDate is today:
//   * Initial load: full file parse, populates LogLines.
//   * Refresh timer (every 5s): incremental read -- only lines
//     appended to the on-disk file(s) since the last refresh get
//     parsed and appended.
//
// When SelectedDate is any past day:
//   * Full load from file(s), no refresh (past days don't grow).
//   * Auto-scroll checkbox disables (nothing new is arriving).
//   * "historical view" label appears.
//
// Midnight rollover: the user's chosen date is not auto-advanced.
// If they were viewing "today" (say 9/4) and midnight passes, the
// system's DateTime.Today advances to 9/5 but SelectedDate stays
// at 9/4. IsViewingToday flips false naturally; the refresh timer
// starts skipping; the historical label appears; auto-scroll
// disables. User clicks Today to catch up to the new day.

internal sealed class DiagnosticsViewModel : INotifyPropertyChanged
{
    private const int MaxLinesDisplayed = 5000;
    private const int RefreshIntervalSeconds = 5;
    private const string FilterCriteriaChangedProperty = "FilterCriteriaChanged";

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _midnightTimer;
    private readonly DispatcherTimer _refreshTimer;
    private DateTime _cachedToday = DateTime.Today;

    // File offsets from the last successful load. Keys are file
    // paths, values are line counts already consumed. Reset on
    // every date change so a switch to a new date always full-loads.
    private Dictionary<string, long> _fileLineIndices = new();

    // Guard: prevents overlapping loads (e.g. refresh firing mid-
    // date-change). Pending flag re-runs OnDateChangedAsync once
    // the current load finishes.
    private bool _isLoading;
    private bool _pendingReload;
    private bool _isPaused = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---- Log collection ----------------------------------------------

    public ObservableCollection<LogLine> LogLines { get; } = new();

    // ---- Status ------------------------------------------------------

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set { if (_isConnected != value) { _isConnected = value; Raise(); } }
    }

    // ---- Date / historical state ------------------------------------

    private DateTime _selectedDate = DateTime.Today;
    public DateTime? SelectedDate
    {
        get => _selectedDate;
        set
        {
            DateTime newDate = (value ?? DateTime.Today).Date;
            if (_selectedDate == newDate) return;
            _selectedDate = newDate;
            Raise();
            Raise(nameof(IsViewingToday));
            Raise(nameof(HistoricalTooltip));
            _ = OnDateChangedAsync();
        }
    }

    /// <summary>Bound to DatePicker's DisplayDateEnd so the user
    /// can't pick future dates. Updates at midnight so tomorrow
    /// becomes selectable when it becomes today.</summary>
    public DateTime TodayDate => DateTime.Today;

    public bool IsViewingToday => _selectedDate == DateTime.Today;

    public string? HistoricalTooltip =>
        IsViewingToday ? null : "Viewing past day \u2014 no live tailing";

    private bool _hasNoFileForSelectedDate;
    public bool HasNoFileForSelectedDate
    {
        get => _hasNoFileForSelectedDate;
        private set { if (_hasNoFileForSelectedDate != value) { _hasNoFileForSelectedDate = value; Raise(); } }
    }

    private bool _isLoadingHistoricalDay;
    public bool IsLoadingHistoricalDay
    {
        get => _isLoadingHistoricalDay;
        private set { if (_isLoadingHistoricalDay != value) { _isLoadingHistoricalDay = value; Raise(); } }
    }

    // ---- Time range --------------------------------------------------

    private TimeSpan _timeFrom = TimeSpan.Zero;
    public TimeSpan TimeFrom
    {
        get => _timeFrom;
        set
        {
            if (_timeFrom == value) return;
            _timeFrom = value;
            Raise();
            Raise(nameof(TimeFromText));
            RaiseFilterChanged();
        }
    }

    private TimeSpan _timeTo = new(23, 59, 59);
    public TimeSpan TimeTo
    {
        get => _timeTo;
        set
        {
            if (_timeTo == value) return;
            _timeTo = value;
            Raise();
            Raise(nameof(TimeToText));
            RaiseFilterChanged();
        }
    }

    public string TimeFromText
    {
        get => FormatTime(_timeFrom);
        set
        {
            if (TryParseTime(value, out TimeSpan parsed)) TimeFrom = parsed;
            else Raise();
        }
    }

    public string TimeToText
    {
        get => FormatTime(_timeTo);
        set
        {
            if (TryParseTime(value, out TimeSpan parsed)) TimeTo = parsed;
            else Raise();
        }
    }

    // ---- Level chips -------------------------------------------------

    private bool _isTrace;
    public bool IsTrace { get => _isTrace; set => SetFilterProp(ref _isTrace, value); }

    private bool _isDebug = true;
    public bool IsDebug { get => _isDebug; set => SetFilterProp(ref _isDebug, value); }

    private bool _isInfo = true;
    public bool IsInfo { get => _isInfo; set => SetFilterProp(ref _isInfo, value); }

    private bool _isWarn = true;
    public bool IsWarn { get => _isWarn; set => SetFilterProp(ref _isWarn, value); }

    private bool _isError = true;
    public bool IsError { get => _isError; set => SetFilterProp(ref _isError, value); }

    // ---- Category chips ---------------------------------------------

    private bool _isCatSerial = true;
    public bool IsCatSerial { get => _isCatSerial; set => SetFilterProp(ref _isCatSerial, value); }

    private bool _isCatSwitch = true;
    public bool IsCatSwitch { get => _isCatSwitch; set => SetFilterProp(ref _isCatSwitch, value); }

    private bool _isCatClipboard = true;
    public bool IsCatClipboard { get => _isCatClipboard; set => SetFilterProp(ref _isCatClipboard, value); }

    private bool _isCatKeyboard = true;
    public bool IsCatKeyboard { get => _isCatKeyboard; set => SetFilterProp(ref _isCatKeyboard, value); }

    private bool _isCatMouse = true;
    public bool IsCatMouse { get => _isCatMouse; set => SetFilterProp(ref _isCatMouse, value); }

    private bool _isCatSystem = true;
    public bool IsCatSystem { get => _isCatSystem; set => SetFilterProp(ref _isCatSystem, value); }

    // ---- Source chips -----------------------------------------------

    private bool _isSrcAgent = true;
    public bool IsSrcAgent { get => _isSrcAgent; set => SetFilterProp(ref _isSrcAgent, value); }

    // ---- Search ------------------------------------------------------

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            Raise();
            RaiseFilterChanged();
        }
    }

    // ---- Filter predicate -------------------------------------------

    public bool PassesFilter(LogLine line)
    {
        TimeSpan tod = line.Timestamp.TimeOfDay;
        if (tod < _timeFrom || tod > _timeTo) return false;

        bool levelOk = line.Level switch
        {
            LogLevel.Verbose => _isTrace,
            LogLevel.Debug   => _isDebug,
            LogLevel.Info    => _isInfo,
            LogLevel.Warn    => _isWarn,
            LogLevel.Error   => _isError,
            _                => true,
        };
        if (!levelOk) return false;

        bool catOk = line.Category switch
        {
            LogCategory.Serial    => _isCatSerial,
            LogCategory.Switch    => _isCatSwitch,
            LogCategory.Clipboard => _isCatClipboard,
            LogCategory.Keyboard  => _isCatKeyboard,
            LogCategory.Mouse     => _isCatMouse,
            LogCategory.System    => _isCatSystem,
            _                     => true,
        };
        if (!catOk) return false;

        bool srcOk = line.Source switch
        {
            LogSource.Agent      => _isSrcAgent,
            LogSource.Controller => false,
            LogSource.Bridge     => false,
            _                    => true,
        };
        if (!srcOk) return false;

        if (string.IsNullOrEmpty(_searchText)) return true;
        return line.Message.Contains(_searchText, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Time-range action methods ---------------------------------

    public void SetTimeFromToNow()
    {
        TimeSpan now = DateTime.Now.TimeOfDay;
        TimeFrom = new TimeSpan(now.Hours, now.Minutes, now.Seconds);
    }

    public void AddDurationToTo(int minutes)
    {
        TimeSpan proposed = _timeFrom + TimeSpan.FromMinutes(minutes);
        if (proposed >= TimeSpan.FromDays(1))
        {
            proposed = new TimeSpan(23, 59, 59);
        }
        TimeTo = proposed;
    }

    public void ResetTimeRange()
    {
        TimeFrom = TimeSpan.Zero;
        TimeTo = new TimeSpan(23, 59, 59);
    }

    public void GoToToday()
    {
        SelectedDate = DateTime.Today;
    }

    // ---- Construction ------------------------------------------------

    public DiagnosticsViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        RefreshStatus();
        _statusTimer.Start();

        _midnightTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMinutes(1),
        };
        _midnightTimer.Tick += (_, _) => CheckMidnightRollover();
        _midnightTimer.Start();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(RefreshIntervalSeconds),
        };
        _refreshTimer.Tick += (_, _) => _ = RefreshTickAsync();
        _refreshTimer.Start();

        // Initial load: today.
        _ = OnDateChangedAsync();
    }

    public void Shutdown()
    {
        try { _statusTimer.Stop(); }   catch { }
        try { _midnightTimer.Stop(); } catch { }
        try { _refreshTimer.Stop(); }  catch { }
    }

    // ---- Date-change reload (full load) -----------------------------
    public void SetActive(bool active)
    {
        if (_isPaused != !active)
        {
            _isPaused = !active;
            if (active)
            {
                // Immediate catch-up refresh so the view doesn't lag
                // by up to 5 seconds when the user tabs in.
                _ = RefreshTickAsync();
            }
        }
    }

    private async Task OnDateChangedAsync()
    {
        // Serialize against concurrent refresh ticks / date changes.
        // Rare, but a rapid date-picker click can trigger it.
        if (_isLoading)
        {
            _pendingReload = true;
            return;
        }
        _isLoading = true;
        IsLoadingHistoricalDay = true;
        try
        {
            _fileLineIndices.Clear();

            HistoricalLoadResult result = await HistoricalLogLoader.LoadAsync(
                _selectedDate, lineIndices: null);

            LogLines.Clear();
            if (!result.AnyFileExists)
            {
                HasNoFileForSelectedDate = true;
            }
            else
            {
                foreach (LogLine line in result.Lines)
                {
                    LogLines.Add(line);
                }
                TrimLogIfNeeded();
                HasNoFileForSelectedDate = result.Lines.Count == 0;
            }
            _fileLineIndices = result.LineIndicesAfter;
            RaiseFilterChanged();
        }
        catch (Exception ex)
        {
            Log.System.Error(ex, "Failed to load log for {Date}.", _selectedDate);
        }
        finally
        {
            IsLoadingHistoricalDay = false;
            _isLoading = false;

            if (_pendingReload)
            {
                _pendingReload = false;
                _ = OnDateChangedAsync();
            }
        }
    }

    // ---- Refresh tick (incremental, today only) ---------------------

    private async Task RefreshTickAsync()
    {
        if (!IsViewingToday) return;
        if (_isLoading) return;
        if (_isPaused) return;

        _isLoading = true;
        try
        {
            HistoricalLoadResult result = await HistoricalLogLoader.LoadAsync(
                _selectedDate, lineIndices: _fileLineIndices);

            foreach (LogLine line in result.Lines)
            {
                LogLines.Add(line);
            }
            TrimLogIfNeeded();
            // Don't touch HasNoFileForSelectedDate on refresh --
            // refresh is streaming, not initial-state assessment.
            _fileLineIndices = result.LineIndicesAfter;
        }
        catch (Exception ex)
        {
            Log.System.Error(ex, "Historical refresh failed.");
        }
        finally
        {
            _isLoading = false;
        }
    }

    // ---- Midnight rollover ------------------------------------------

    private void CheckMidnightRollover()
    {
        DateTime nowToday = DateTime.Today;
        if (nowToday == _cachedToday) return;

        _cachedToday = nowToday;
        Raise(nameof(TodayDate));
        Raise(nameof(IsViewingToday));
        Raise(nameof(HistoricalTooltip));
        // Do NOT touch SelectedDate. User's date stays put; the
        // refresh timer will skip on next tick because
        // IsViewingToday is now false. Historical label appears via
        // its DataTrigger. Auto-scroll disables via the view's
        // PropertyChanged subscription on IsViewingToday.
    }

    // ---- Trim + status ----------------------------------------------

    private void TrimLogIfNeeded()
    {
        while (LogLines.Count > MaxLinesDisplayed)
        {
            LogLines.RemoveAt(0);
        }
    }

    private void RefreshStatus()
    {
        var port = Serial.ActivePort;
        IsConnected = port?.IsOpen == true;
    }

    // ---- Helpers ----------------------------------------------------

    private void SetFilterProp(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        Raise(name);
        RaiseFilterChanged();
    }

    private void RaiseFilterChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(FilterCriteriaChangedProperty));
    }

    public static string FilterChangedSignal => FilterCriteriaChangedProperty;

    private static string FormatTime(TimeSpan t) =>
        $"{t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";

    private static bool TryParseTime(string s, out TimeSpan result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (TimeSpan.TryParseExact(s, "hh\\:mm\\:ss", CultureInfo.InvariantCulture, out result))
            return true;
        if (TimeSpan.TryParseExact(s, "hh\\:mm", CultureInfo.InvariantCulture, out result))
            return true;
        return false;
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}