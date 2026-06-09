using System.Windows;
// "Application" exists in both System.Windows.Forms and System.Windows;
// alias it to the WPF type so unqualified references compile cleanly.
// Window, WindowState, and ShutdownMode are WPF-only and don't need
// the same treatment.
using Application = System.Windows.Application;

namespace Janus.Agent.Gui;

// Lifecycle manager for the WPF window. Mirrors MessageWindow's
// pattern (own STA thread + own message pump) but for WPF instead
// of WinForms.
//
// Why a dedicated thread rather than reusing MessageWindow's: WPF
// and WinForms each want to OWN their Dispatcher / Application.Run
// loop. Trying to share one threading context across both leads
// to subtle deadlocks and re-entrancy bugs. Two threads with two
// pumps is the standard interop pattern.
//
// The thread is created lazily by Start(deviceId). It runs an
// Application instance set to ShutdownMode.OnExplicitShutdown, so
// closing the window (which we already redirect to Hide()) doesn't
// terminate the dispatcher. Only GuiHost.Stop calls Application.
// Shutdown.
//
// Show / Hide / IsVisible all marshal to the dispatcher because they
// touch the Window instance, which has strict thread affinity in WPF.

internal static class GuiHost
{
    private static Thread? _thread;
    private static Application? _app;
    private static GuiWindow? _window;
    private static readonly ManualResetEventSlim _ready = new(initialState: false);

    /// <summary>Start the GUI thread. Blocks until the dispatcher
    /// exists and the window is constructed (so subsequent Show()
    /// calls have something to act on). Idempotent.</summary>
    public static void Start(string deviceId)
    {
        if (_thread is not null) return;

        _thread = new Thread(() =>
        {
            try
            {
                _app = new Application
                {
                    // Closing the (last) window must NOT shut the
                    // dispatcher down -- the user often closes the
                    // GUI but expects the agent to keep running in
                    // the tray. Only GuiHost.Stop will call Shutdown.
                    ShutdownMode = ShutdownMode.OnExplicitShutdown,
                };
                _window = new GuiWindow(deviceId);
                _ready.Set();
                _app.Run();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GuiHost thread error: {ex.Message}");
                _ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "GuiHost",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // Wait up to 3 seconds for window construction. If WPF fails
        // to initialize (e.g., missing graphics support in some
        // environments), we don't want to block startup forever.
        _ready.Wait(3000);
    }

    /// <summary>Show the GUI window. Marshalled to the dispatcher
    /// thread. No-op if Start has not been called or the window
    /// failed to construct.</summary>
    public static void Show()
    {
        var app = _app;
        var window = _window;
        if (app is null || window is null) return;

        try
        {
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!window.IsVisible)
                    {
                        window.Show();
                    }
                    if (window.WindowState == WindowState.Minimized)
                    {
                        window.WindowState = WindowState.Normal;
                    }
                    window.Activate();
                    window.Topmost = true;
                    window.Topmost = false;   // brief topmost dance to force focus
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"GuiHost show error: {ex.Message}");
                }
            }));
        }
        catch { /* dispatcher might be tearing down */ }
    }

    /// <summary>Hide the GUI window without disposing it. Show() can
    /// bring it back later.</summary>
    public static void Hide()
    {
        var app = _app;
        var window = _window;
        if (app is null || window is null) return;

        try
        {
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (window.IsVisible) window.Hide();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"GuiHost hide error: {ex.Message}");
                }
            }));
        }
        catch { }
    }

    /// <summary>True if the window is currently shown. Synchronous
    /// (waits for the dispatcher); called from the tray menu's
    /// Opening handler, which is rare enough that the round trip
    /// isn't a concern.</summary>
    public static bool IsVisible
    {
        get
        {
            var app = _app;
            var window = _window;
            if (app is null || window is null) return false;
            try
            {
                return (bool?)app.Dispatcher.Invoke(() => window.IsVisible) ?? false;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Stop the dispatcher and tear down the window. Called
    /// from Program.cs's finally during agent shutdown.</summary>
    public static void Stop()
    {
        var app = _app;
        if (app is null) return;

        try
        {
            app.Dispatcher.Invoke(() =>
            {
                try { _window?.ForceClose(); } catch { }
                try { app.Shutdown(); } catch { }
            });
        }
        catch { /* dispatcher might already be tearing down */ }

        try { _thread?.Join(1000); } catch { }
        _thread = null;
        _app = null;
        _window = null;
    }
}