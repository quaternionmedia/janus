using System.Runtime.InteropServices;

namespace Janus.Agent.Platform;

// Native interop layer. Pure P/Invoke wrappers and the value types that
// appear in their signatures. No logic lives here -- callers translate
// raw Win32 calls into agent-level behavior.

internal static class Win32
{
    // ---- Hotkey registration (user32) -------------------------------------

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---- Clipboard change notification (user32) ---------------------------

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    // ---- Cursor / display geometry (user32) -------------------------------

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetrics(SystemMetric smIndex);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    // ---- Session lock/unlock notification (wtsapi32) ----------------------
    //
    // Subscribe a window to WM_WTSSESSION_CHANGE messages. wParam carries
    // the event: 0x7 = lock, 0x8 = unlock (plus a handful for RDP /
    // console connect-disconnect that we don't care about). Used by
    // MessageWindow to fire a callback when the workstation locks.

    [DllImport("wtsapi32.dll", SetLastError = true)]
    internal static extern bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    internal static extern bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    internal const uint NotifyForThisSession = 0;
    internal const int WmWtssessionChange = 0x02B1;
    internal const int WtsSessionLock = 0x7;
    internal const int WtsSessionUnlock = 0x8;

    // ---- Console window (kernel32 + user32) -------------------------------
    //
    // Used by ConsoleWindow to hide/show the agent's own console and
    // apply WS_EX_TOOLWINDOW so it doesn't appear in the taskbar.

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    // GetWindowLongPtr / SetWindowLongPtr dispatch by process bitness.
    // On 64-bit Windows the user32 export is "GetWindowLongPtrW"; on
    // 32-bit it's "GetWindowLongW" and the *Ptr name is just a C macro
    // alias. We pick the right entry point at runtime so the same
    // build works on either bitness.

    internal static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, nIndex)
            : new IntPtr(GetWindowLong32(hWnd, nIndex));
    }

    internal static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        return IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, dwNewLong.ToInt32()));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    internal const int SW_HIDE = 0;
    internal const int SW_SHOW = 5;
    internal const int SW_RESTORE = 9;

    internal const int GWL_EXSTYLE = -20;
    internal const long WS_EX_TOOLWINDOW = 0x00000080L;
    internal const long WS_EX_APPWINDOW = 0x00040000L;

    // ---- Types used in the signatures above -------------------------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    internal enum SystemMetric
    {
        SM_XVIRTUALSCREEN = 76,
        SM_YVIRTUALSCREEN = 77,
        SM_CXVIRTUALSCREEN = 78,
        SM_CYVIRTUALSCREEN = 79,
    }
}