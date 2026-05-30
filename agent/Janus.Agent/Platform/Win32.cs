using System.Runtime.InteropServices;

namespace Janus.Agent.Platform;

/// <summary>
/// Native interop layer. Pure P/Invoke wrappers and the value types that
/// appear in their signatures. No logic lives here -- callers translate
/// raw Win32 calls into agent-level behavior.
/// </summary>
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