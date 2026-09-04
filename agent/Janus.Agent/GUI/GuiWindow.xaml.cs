using Janus.Agent.Gui.Shared;
using Janus.Agent.Gui.ViewModels;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace Janus.Agent.Gui;

// Shell for the WPF window. Hosts the tabbed content (MainView +
// DiagnosticsView) plus the shared settings modal.
//
// Modal-open is decoupled from ActionsPanel via a bubbling RoutedEvent
// (ActionsPanel.SettingsRequestedEvent). AddHandler on the window
// itself catches the event no matter which nested ActionsPanel fired
// it -- so both Main and Diag tabs open the same modal instance
// without additional wiring.

public partial class GuiWindow : Window
{
    // ---- DWM dark mode (P/Invoke) ----------------------------------

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

    // ---- State -----------------------------------------------------

    private readonly GuiViewModel _viewModel = new();
    private bool _forceClosing;

    // ---- Construction ----------------------------------------------

    public GuiWindow(string deviceId)
    {
        InitializeComponent();

        DataContext = _viewModel;

        AddHandler(ActionsPanel.SettingsRequestedEvent,
            new RoutedEventHandler(ActionsPanel_SettingsRequested));

        KeyDown += GuiWindow_KeyDown;

        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "janus_lg.ico");
            if (File.Exists(iconPath))
            {
                Icon = new BitmapImage(new Uri(iconPath));
            }
            else
            {
                Log.System.Warn("Window icon not found at {IconPath}; using default.", iconPath);
            }
        }
        catch (Exception ex)
        {
            Log.System.Warn(ex, "Window icon load error; using default.");
        }
    }

    // ---- Dark title bar ---------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

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
            Log.System.Warn(ex, "Dark title bar attribute error.");
        }
    }

    // ---- Close-to-tray ---------------------------------------------

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClosing)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    /// <summary>Called by GuiHost.Stop to actually close the window
    /// rather than hiding it. Shuts down each view's ViewModel so
    /// their status timers and LogSink subscriptions release cleanly.</summary>
    public void ForceClose()
    {
        _forceClosing = true;
        try { MainViewInstance.Shutdown(); } catch { }
        try { DiagViewInstance.Shutdown(); } catch { }
        Close();
    }

    // ---- Settings modal --------------------------------------------

    private void ActionsPanel_SettingsRequested(object sender, RoutedEventArgs e)
    {
        ShowSettingsModal();
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e)
    {
        HideSettingsModal();
    }

    private void ModalBackdrop_Click(object sender, MouseButtonEventArgs e)
    {
        HideSettingsModal();
    }

    private void GuiWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SettingsModal.Visibility == Visibility.Visible)
        {
            HideSettingsModal();
            e.Handled = true;
        }
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