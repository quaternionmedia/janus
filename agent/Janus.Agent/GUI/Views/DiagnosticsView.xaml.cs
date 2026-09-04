using Janus.Agent.Gui.Shared;
using Janus.Agent.Gui.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Janus.Agent.Gui.Views;

// Code-behind for DiagnosticsView. Mirrors MainView's structure --
// same LogDocumentSync pattern, same auto-scroll checkbox behavior.
// Stage 4 will extend the ViewModel-side filter predicate to include
// level/category/source/time in addition to the current substring
// search; the sync + code-behind here don't need to change for that
// because they call PassesFilter opaquely.

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
            WordWrapEnabled = WordWrapCheck.IsChecked == true
        };
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiagnosticsViewModel.SearchText))
        {
            _logSync?.Rebuild();
        }
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
    
    private void WordWrapCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_logSync == null) return;
        _logSync.WordWrapEnabled = WordWrapCheck.IsChecked == true;
    }
}