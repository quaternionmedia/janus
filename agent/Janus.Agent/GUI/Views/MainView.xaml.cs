using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace Janus.Agent.Gui.Views;

// Code-behind for MainView. Owns auto-scroll behavior for the log
// ScrollViewer -- the "auto-scroll" checkbox is a live toggle that
// pins the view to the bottom as new lines arrive, unless the user
// has scrolled up to inspect history (in which case new arrivals
// don't yank the view back).
//
// DataContext: expected to be a MainViewModel, wired up in the
// constructor. GuiHost is responsible for creating the parent
// GuiWindow, which creates a MainView; the ViewModel is instantiated
// here rather than being passed in, so the view owns its own state
// container.

public partial class MainView : UserControl
{
    private readonly MainViewModel _viewModel;
    private bool _wasAtBottom = true;

    public MainView()
    {
        InitializeComponent();

        // ViewModel construction is deferred until we can capture the
        // dispatcher (needed for cross-thread log-append marshalling).
        // Loaded is the earliest reliable point for that -- we may be
        // constructed at any time by the parent Window's XAML, but the
        // dispatcher isn't always associated until we're actually in
        // the visual tree.
        Loaded += (_, _) => { /* handled via constructor below */ };

        _viewModel = new MainViewModel(Dispatcher, deviceId: GuiHost.DeviceId);
        DataContext = _viewModel;
    }

    public void Shutdown()
    {
        _viewModel.Shutdown();
    }

    // ---- Auto-scroll ------------------------------------------------
    //
    // Two moving parts:
    //   1. LogScrollViewer_ScrollChanged tracks whether we're pinned
    //      to the bottom after every scroll event (user-initiated or
    //      programmatic).
    //   2. When new content extends the scrollable area (VerticalChange
    //      == 0 but ExtentHeightChange > 0), we scroll back to the
    //      bottom IFF the checkbox is on AND the user hadn't scrolled
    //      up.

    private void LogScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange > 0)
        {
            if (AutoScrollCheck.IsChecked == true && _wasAtBottom)
            {
                LogScrollViewer.ScrollToBottom();
            }
        }
        else
        {
            _wasAtBottom = IsAtBottom();
        }
    }

    private void AutoScrollCheck_Click(object sender, RoutedEventArgs e)
    {
        bool nowChecked = AutoScrollCheck.IsChecked == true;
        _wasAtBottom = nowChecked;
        if (nowChecked)
        {
            LogScrollViewer.ScrollToBottom();
        }
    }

    private bool IsAtBottom()
    {
        if (LogScrollViewer.ScrollableHeight <= 0) return true;
        return LogScrollViewer.VerticalOffset >= LogScrollViewer.ScrollableHeight - 1;
    }
}