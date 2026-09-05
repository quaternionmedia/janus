using Janus.Agent.Gui.Shared;
using Janus.Agent.Gui.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Janus.Agent.Gui.Views;

// Code-behind for MainView.
//
// LogDocumentSync (created on Loaded) owns the RichTextBox's
// FlowDocument -- it mirrors MainViewModel.LogLines into Paragraphs,
// applies the ViewModel's PassesFilter predicate, and manages
// auto-scroll behavior. When SearchText changes on the VM, we call
// Rebuild() to reapply the filter across the whole document.

public partial class MainView : UserControl
{
    private readonly MainViewModel _viewModel;
    private LogDocumentSync? _logSync;

    public MainView()
    {
        InitializeComponent();

        _viewModel = new MainViewModel(Dispatcher, deviceId: GuiHost.DeviceId);
        DataContext = _viewModel;

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        // ScrollViewer inside the RichTextBox isn't in the visual
        // tree until Loaded, so defer sync construction until then.
        Loaded += MainView_Loaded;
    }

    public void Shutdown()
    {
        try { _viewModel.PropertyChanged -= ViewModel_PropertyChanged; } catch { }
        if (_logSync != null)
        {
            try { _logSync.ScrollStateChanged -= LogSync_ScrollStateChanged; } catch { }
            try { _logSync.Detach(); } catch { }
        }
        _viewModel.Shutdown();
    }

    private void MainView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_logSync != null) return;

        Brush mutedBrush = (Brush)FindResource("MutedBrush");
        _logSync = new LogDocumentSync(
            richTextBox: LogView,
            source:      _viewModel.LogLines,
            filter:      _viewModel.PassesFilter,
            prefixBrush: mutedBrush)
        {
            WordWrapEnabled = WordWrapCheck.IsChecked == true,
        };
        
        _logSync.ScrollStateChanged += LogSync_ScrollStateChanged;
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SearchText))
        {
            _logSync?.Rebuild();
        }
    }
    
    private void WordWrapCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_logSync == null) return;
        _logSync.WordWrapEnabled = WordWrapCheck.IsChecked == true;
    }
    
    private void LogSync_ScrollStateChanged(bool isAtBottom)
    {
        JumpToBottomButton.Visibility = isAtBottom
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void JumpToBottom_Click(object sender, RoutedEventArgs e)
    {
        _logSync?.ScrollToEnd();
    }
}