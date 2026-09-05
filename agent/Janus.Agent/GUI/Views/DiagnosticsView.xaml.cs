using System.ComponentModel;
using System.Windows;

namespace Janus.Agent.Gui.Views;

// Code-behind for DiagnosticsView.
//
// Handles the button-click plumbing for the filter bar (Today, Now,
// +5m/+30m/+1h, Reset, Clear-search, Search-focus) and gates the
// auto-scroll checkbox based on whether the user is viewing today
// or a historical date.
//
// Filter-rebuild trigger uses the synthetic FilterChangedSignal
// property that DiagnosticsViewModel raises whenever any of the
// ~20 filter-affecting properties change. Subscribing once here
// beats listing every property name in a switch.

public partial class DiagnosticsView : UserControl
{
    private readonly DiagnosticsViewModel _viewModel;
    private LogDocumentSync? _logSync;

    public DiagnosticsView()
    {
        InitializeComponent();
        _viewModel = new DiagnosticsViewModel(Dispatcher);
        DataContext = _viewModel;
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        Loaded += DiagnosticsView_Loaded;
        IsVisibleChanged += DiagnosticsView_IsVisibleChanged;
    }

    public void Shutdown()
    {
        try { _viewModel.PropertyChanged -= ViewModel_PropertyChanged; } catch { }
        try { _logSync?.Detach(); } catch { }
        _viewModel.Shutdown();
    }

    private void DiagnosticsView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_logSync != null) return;

        Brush mutedBrush = (Brush)FindResource("MutedBrush");
        _logSync = new LogDocumentSync(
            richTextBox: LogView,
            source:      _viewModel.LogLines,
            filter:      _viewModel.PassesFilter,
            prefixBrush: mutedBrush)
        {
            AutoScrollEnabled = AutoScrollCheck.IsChecked == true,
            WordWrapEnabled   = WordWrapCheck.IsChecked == true,
        };
    }

    private void DiagnosticsView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _viewModel.SetActive((bool)e.NewValue);
    }
    
    // ---- ViewModel change plumbing --------------------------------

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == DiagnosticsViewModel.FilterChangedSignal)
        {
            // Any filter criterion changed -- re-run predicate
            // against every line, re-emit paragraphs. LogDocumentSync
            // handles the re-scroll if AutoScroll is on.
            _logSync?.Rebuild();
        }
        else if (e.PropertyName == nameof(DiagnosticsViewModel.IsViewingToday))
        {
            HandleTodayViewingChange();
        }
    }

    /// <summary>When toggling in/out of historical mode, adjust the
    /// auto-scroll checkbox. Historical view is a frozen snapshot,
    /// so auto-scroll has nothing to follow -- disable it. Going
    /// back to today re-enables it and defaults it to on (user's
    /// preferred behavior per the spec).</summary>
    private void HandleTodayViewingChange()
    {
        bool viewingToday = _viewModel.IsViewingToday;
        AutoScrollCheck.IsEnabled = viewingToday;
        AutoScrollCheck.IsChecked = viewingToday;
        if (_logSync != null)
        {
            _logSync.AutoScrollEnabled = viewingToday;
        }
    }

    // ---- Date-row buttons -----------------------------------------

    private void TodayButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.GoToToday();
    }

    // ---- Time-row buttons -----------------------------------------

    private void NowButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SetTimeFromToNow();
    }

    private void PresetPlus_Click(object sender, RoutedEventArgs e)
    {
        // Duration in minutes is set as the button's Tag in XAML.
        if (sender is Button b && b.Tag is string tagStr && int.TryParse(tagStr, out int minutes))
        {
            _viewModel.AddDurationToTo(minutes);
        }
    }

    private void ResetTimeButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ResetTimeRange();
    }

    // ---- Search-row buttons ---------------------------------------

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        SearchBox.Focus();
    }

    // private void SearchIcon_Click(object sender, RoutedEventArgs e)
    // {
    //     // Search runs live (bound via UpdateSourceTrigger=PropertyChanged),
    //     // so this button is a focus-into-search affordance rather than
    //     // an "apply search" trigger. Still worth having -- gives the
    //     // search box a discoverable icon.
    //     SearchBox.Focus();
    //     Keyboard.Focus(SearchBox);
    // }

    // ---- Log-header toggles ---------------------------------------

    private void WordWrapCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_logSync == null) return;
        _logSync.WordWrapEnabled = WordWrapCheck.IsChecked == true;
    }

    private void AutoScrollCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_logSync == null) return;
        _logSync.AutoScrollEnabled = AutoScrollCheck.IsChecked == true;
        if (_logSync.AutoScrollEnabled)
        {
            _logSync.ScrollToEnd();
        }
    }
}