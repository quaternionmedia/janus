namespace Janus.Agent.Platform;

// Show/hide the agent's own console window, and apply the tool-window
// extended style so it never appears in the taskbar (even when shown).
//
// The agent runs as OutputType=Exe, so it has a real console window
// from process start. We hide it at startup and let the tray icon
// surface it on demand. Hiding doesn't break Console.WriteLine: writes
// still go to the console buffer, so when the user clicks "Show
// window" the full scrollback is there waiting.
//
// WS_EX_TOOLWINDOW combined with NOT-WS_EX_APPWINDOW = "no taskbar
// entry, no Alt+Tab entry". Reasonable for a tray-driven utility
// window the user only occasionally peeks at.

internal static class ConsoleWindow
{
    private static IntPtr _hwnd = IntPtr.Zero;

    private static IntPtr Hwnd
    {
        get
        {
            // Cache after first lookup. The console hWnd doesn't change
            // for the lifetime of the process.
            if (_hwnd == IntPtr.Zero)
            {
                _hwnd = Win32.GetConsoleWindow();
            }
            return _hwnd;
        }
    }

    /// <summary>True if the console window is currently visible. False
    /// if hidden or if there is no console (which shouldn't happen for
    /// OutputType=Exe but we guard against IntPtr.Zero anyway).</summary>
    public static bool IsVisible
    {
        get
        {
            IntPtr h = Hwnd;
            if (h == IntPtr.Zero) return false;
            return Win32.IsWindowVisible(h);
        }
    }

    public static void Show()
    {
        IntPtr h = Hwnd;
        if (h == IntPtr.Zero) return;
        Win32.ShowWindow(h, Win32.SW_SHOW);
    }

    public static void Hide()
    {
        IntPtr h = Hwnd;
        if (h == IntPtr.Zero) return;
        Win32.ShowWindow(h, Win32.SW_HIDE);
    }

    /// <summary>Add WS_EX_TOOLWINDOW and strip WS_EX_APPWINDOW from the
    /// console window's extended style. After this, the window does
    /// NOT show in the taskbar or Alt+Tab, even when visible. Call
    /// once at startup before any Show().</summary>
    public static void ApplyToolWindowStyle()
    {
        IntPtr h = Hwnd;
        if (h == IntPtr.Zero) return;

        try
        {
            IntPtr current = Win32.GetWindowLongPtr(h, Win32.GWL_EXSTYLE);
            long updated = (current.ToInt64() | Win32.WS_EX_TOOLWINDOW) & ~Win32.WS_EX_APPWINDOW;
            Win32.SetWindowLongPtr(h, Win32.GWL_EXSTYLE, new IntPtr(updated));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ConsoleWindow style apply error: {ex.Message}");
        }
    }
}