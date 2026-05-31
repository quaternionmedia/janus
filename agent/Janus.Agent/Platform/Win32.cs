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