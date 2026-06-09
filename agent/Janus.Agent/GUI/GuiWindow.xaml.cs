using Janus.Agent.Clipboard;
using Janus.Agent.Events;
using Janus.Agent.Platform;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace Janus.Agent.Gui;

// Code-behind for GuiWindow.xaml. Five responsibilities:
//
// 1. Close-to-tray. Overriding OnClosing cancels the close and hides
//    the window. ForceClose (called from GuiHost.Stop) is the only
//    path to a real close.
//
// 2. Auto-scroll. The log panel sticks to the bottom when new lines
//    arrive, unless the user has scrolled up to inspect history.
//
// 3. Window icon + dark title bar. Loaded from Resources/janus_lg.ico
//    and applied via DwmSetWindowAttribute on OnSourceInitialized.
//
// 4. Action button dispatch. Switch / Send clipboard / Reconnect
//    buttons in the sidebar call the same static methods the tray
//    menu does.
//
// 5. Settings modal. Gear icon in the sidebar footer shows it; X
//    button, backdrop click, or Esc key hides it.

public partial class GuiWindow : Window
{
    // ---- DWM dark mode (P/Invoke) ----------------------------------

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

    // ---- State -----------------------------------------------------

    private readonly GuiViewModel _viewModel;
    private bool _forceClosing;
    private bool _wasAtBottom = true;

    // ---- Construction ----------------------------------------------

    public GuiWindow(string deviceId)
    {
        InitializeComponent();

        _viewModel = new GuiViewModel(Dispatcher, deviceId);
        DataContext = _viewModel;
        Title = $"Janus.Agent ({deviceId})";

        LoadWindowIcon();
    }

    private void LoadWindowIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "janus_lg.ico");
            if (File.Exists(iconPath))
            {
                Icon = new BitmapImage(new Uri(iconPath, UriKind.Absolute));
            }
            else
            {
                Console.WriteLine($"Window icon not found at {iconPath}; using default.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Window icon load error: {ex.Message}; using default.");
        }
    }

    // ---- Lifecycle hooks -------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryApplyDarkTitleBar();
    }

    private void TryApplyDarkTitleBar()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int useDark = 1;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
            if (hr != 0)
            {
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, sizeof(int));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Dark title bar attribute error: {ex.Message}");
        }
    }

    /// <summary>Permanently close the window. Called by GuiHost.Stop
    /// at agent shutdown.</summary>
    internal void ForceClose()
    {
        _forceClosing = true;
        try { Close(); } catch { }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _viewModel.Shutdown();
        base.OnClosing(e);
    }

    // ---- Esc key: close settings modal if open ----------------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SettingsModal.Visibility == Visibility.Visible)
        {
            HideSettingsModal();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    // ---- Auto-scroll behavior --------------------------------------

    private void LogScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        bool extentChanged = Math.Abs(e.ExtentHeightChange) > 0.001;

        if (extentChanged)
        {
            if (_wasAtBottom)
            {
                LogScrollViewer.ScrollToBottom();
            }
            return;
        }

        bool atBottom = IsAtBottom();
        if (atBottom != _wasAtBottom)
        {
            _wasAtBottom = atBottom;
            AutoScrollCheck.IsChecked = atBottom;
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

    // ---- Action button handlers ------------------------------------
    //
    // Mirror the tray menu's behavior exactly. The static methods log
    // a "(source)" tag so the GUI's invocations show as "(gui)" in
    // the log -- easy to distinguish from tray clicks or hotkeys.

    private void SwitchAction_Click(object sender, RoutedEventArgs e)
    {
        try { Actions.SwitchToPeer("gui"); }
        catch (Exception ex) { Console.WriteLine($"GUI switch action error: {ex.Message}"); }
    }

    private void ClipboardAction_Click(object sender, RoutedEventArgs e)
    {
        try { ClipboardSync.Push("gui"); }
        catch (Exception ex) { Console.WriteLine($"GUI clipboard action error: {ex.Message}"); }
    }

    private void ReconnectAction_Click(object sender, RoutedEventArgs e)
    {
        try { Serial.RequestReconnect(); }
        catch (Exception ex) { Console.WriteLine($"GUI reconnect action error: {ex.Message}"); }
    }

    // ---- Settings modal --------------------------------------------

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ShowSettingsModal();
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e)
    {
        HideSettingsModal();
    }

    private void ModalBackdrop_Click(object sender, MouseButtonEventArgs e)
    {
        // The backdrop Rectangle covers the whole window area. The
        // panel Border draws on top of it (later in document order),
        // so clicks that land on the panel don't reach this handler.
        HideSettingsModal();
    }

    private void ShowSettingsModal()
    {
        SettingsModal.Visibility = Visibility.Visible;
    }

    private void HideSettingsModal()
    {
        SettingsModal.Visibility = Visibility.Collapsed;
    }
}