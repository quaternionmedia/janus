using Janus.Agent.Clipboard;
using Janus.Agent.Events;
using Janus.Agent.Gui;
using Janus.Agent.Platform;
using System.IO;

namespace Janus.Agent.Tray;

// Janus agent's system tray surface. NotifyIcon for the icon itself
// plus a Win32 popup menu (HMENU + TrackPopupMenuEx) for the
// right-click context menu.
//
// ---- Why Win32 popup menu instead of ContextMenuStrip --------------
// WinForms ContextMenuStrip is owner-drawn -- every pixel is painted
// in C#. That means:
//   * No matter how we theme it, it never matches the native Windows
//     dark-mode menu look (the one you see on USB-safe-remove, Explorer
//     right-clicks, etc.).
//   * WinForms' built-in placement has DPI-scaling bugs at >100% display
//     scale, leading to menus showing up in random positions around the
//     tray icon or half off-screen.
//
// Win32 popup menus are rendered by the Windows shell directly. With
// SetPreferredAppMode(AllowDark) called at startup (see Program.cs) and
// the owner window marked dark via DWMWA_USE_IMMERSIVE_DARK_MODE (see
// MessageWindow.cs), they render in dark mode when the system theme is
// dark. TrackPopupMenuEx places them correctly at any DPI scale.
//
// ---- Owner window --------------------------------------------------
// TrackPopupMenuEx requires an HWND that owns the menu. We use the
// hidden form maintained by MessageWindow -- it's already running an
// STA message pump on the same thread that NotifyIcon's events fire
// on, so the menu can use it without thread marshaling.
//
// ---- Command dispatch ---------------------------------------------
// Win32 popup menu items are identified by integer command IDs. With
// TPM_RETURNCMD flag, TrackPopupMenuEx returns the chosen command ID
// directly (or 0 if dismissed without selection). We map IDs to action
// callbacks via a small switch inside OnIconMouseUp.
//
// ---- Menu layout (top to bottom) ----------------------------------
//   - Header: "Janus.Agent (P)"                    [disabled label]
//   - Status: "Connected" or "Disconnected"        [disabled label]
//   - --- separator ---
//   - Switch to peer            -> Actions.SwitchToPeer("tray")
//   - Send clipboard to peer    -> ClipboardSync.Push("tray")
//   - --- separator ---
//   - Reconnect                 -> Serial.RequestReconnect()
//   - --- separator ---
//   - Show window / Hide window -> toggle GUI visibility
//   - --- separator ---
//   - Quit                      -> onQuit() callback

internal static class TrayIcon
{
    // Command IDs assigned to menu items. Must all be non-zero --
    // TrackPopupMenuEx returns 0 when the user dismisses without
    // picking anything, so 0 is reserved as "no selection".
    private const int CmdHeader        = 0x101;
    private const int CmdStatus        = 0x102;
    private const int CmdSwitch        = 0x103;
    private const int CmdClipboard     = 0x104;
    private const int CmdReconnect     = 0x105;
    private const int CmdToggleWindow  = 0x106;
    private const int CmdQuit          = 0x107;

    private static NotifyIcon? _icon;
    private static IntPtr _hMenu = IntPtr.Zero;
    private static Action? _onQuit;
    private static string _deviceId = "P";

    /// <summary>Start the tray icon. Must be called after
    /// MessageWindow.Start() (we marshal onto its STA thread and use
    /// its HWND as the popup menu's owner).</summary>
    public static void Start(string deviceId, Action onQuit)
    {
        if (_icon is not null) return;

        _deviceId = deviceId;
        _onQuit = onQuit;

        MessageWindow.Invoke(() =>
        {
            try
            {
                _hMenu = BuildMenu();

                _icon = new NotifyIcon
                {
                    Icon = LoadIcon(),
                    Text = $"Janus.Agent ({_deviceId})",
                    // Intentionally no ContextMenuStrip -- we drive a
                    // Win32 popup menu manually from MouseUp.
                    Visible = true,
                };
                _icon.MouseUp += OnIconMouseUp;
                _icon.DoubleClick += (_, _) => ToggleWindow();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Tray icon start error: {ex.Message}");
            }
        });
    }

    /// <summary>Hide and dispose the tray icon + destroy the popup
    /// menu. Safe to call if Start was never called or already stopped.</summary>
    public static void Stop()
    {
        if (_icon is null && _hMenu == IntPtr.Zero) return;

        MessageWindow.Invoke(() =>
        {
            try
            {
                if (_icon is not null)
                {
                    _icon.Visible = false;
                    _icon.Dispose();
                    _icon = null;
                }
                if (_hMenu != IntPtr.Zero)
                {
                    Win32.DestroyMenu(_hMenu);
                    _hMenu = IntPtr.Zero;
                }
            }
            catch { /* best-effort during shutdown */ }
        });
    }

    // ---- Menu construction -------------------------------------------

    private static IntPtr BuildMenu()
    {
        IntPtr hMenu = Win32.CreatePopupMenu();
        if (hMenu == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreatePopupMenu failed.");
        }

        // Disabled header / status items. MF_GRAYED gives them the
        // muted appearance and prevents them from being clickable.
        // Win32.AppendMenuW(hMenu, Win32.MF_STRING | Win32.MF_GRAYED,
        //     (UIntPtr)CmdHeader, $"Janus.Agent ({_deviceId})");
        // Win32.AppendMenuW(hMenu, Win32.MF_STRING | Win32.MF_GRAYED,
        //     (UIntPtr)CmdStatus, "Disconnected");
        // Win32.AppendMenuW(hMenu, Win32.MF_SEPARATOR, UIntPtr.Zero, null);

        Win32.AppendMenuW(hMenu, Win32.MF_STRING,
            (UIntPtr)CmdToggleWindow, "Show window");

        Win32.AppendMenuW(hMenu, Win32.MF_SEPARATOR, UIntPtr.Zero, null);

        Win32.AppendMenuW(hMenu, Win32.MF_STRING,
            (UIntPtr)CmdSwitch, "Switch to peer");
        Win32.AppendMenuW(hMenu, Win32.MF_STRING,
            (UIntPtr)CmdClipboard, "Send clipboard to peer");

        Win32.AppendMenuW(hMenu, Win32.MF_SEPARATOR, UIntPtr.Zero, null);

        Win32.AppendMenuW(hMenu, Win32.MF_STRING,
            (UIntPtr)CmdReconnect, "Reconnect");

        Win32.AppendMenuW(hMenu, Win32.MF_SEPARATOR, UIntPtr.Zero, null);

        Win32.AppendMenuW(hMenu, Win32.MF_STRING,
            (UIntPtr)CmdQuit, "Quit");

        return hMenu;
    }

    // ---- Dynamic state refresh ---------------------------------------

    /// <summary>Update the menu items whose text changes between shows:
    /// the connection status, the show/hide-window label, and the tray
    /// icon's hover tooltip. Called right before each TrackPopupMenuEx
    /// invocation.</summary>
    private static void RefreshDynamicItems()
    {
        if (_hMenu == IntPtr.Zero) return;

        bool isConnected = Serial.ActivePort?.IsOpen == true;
        string statusText = isConnected ? "Connected" : "Disconnected";

        // ModifyMenuW with MF_BYCOMMAND looks up the item by its command
        // ID (regardless of position) and rewrites its label. Cheaper
        // than rebuilding the whole menu.
        Win32.ModifyMenuW(_hMenu, (uint)CmdStatus,
            Win32.MF_BYCOMMAND | Win32.MF_STRING | Win32.MF_GRAYED,
            (UIntPtr)CmdStatus, statusText);

        Win32.ModifyMenuW(_hMenu, (uint)CmdToggleWindow,
            Win32.MF_BYCOMMAND | Win32.MF_STRING,
            (UIntPtr)CmdToggleWindow,
            GuiHost.IsVisible ? "Hide window" : "Show window");

        // Hover tooltip on the tray icon. 127-char hard limit on
        // NotifyIcon.Text; truncate defensively even though our format
        // is comfortably under it.
        if (_icon is not null)
        {
            string tip = $"Janus.Agent ({_deviceId}) — {statusText}";
            _icon.Text = tip.Length > 127 ? tip[..127] : tip;
        }
    }

    // ---- Right-click handler -----------------------------------------

    private static void OnIconMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        if (_hMenu == IntPtr.Zero) return;

        IntPtr ownerHwnd = MessageWindow.Handle;
        if (ownerHwnd == IntPtr.Zero) return;

        try
        {
            RefreshDynamicItems();

            // SetForegroundWindow before TrackPopupMenuEx is the
            // documented incantation that lets the menu auto-close
            // when the user clicks outside it. Without this the agent
            // (whose only HWND is the hidden form) isn't in the
            // foreground, and the menu can stick open.
            Win32.SetForegroundWindow(ownerHwnd);

            if (!Win32.GetCursorPos(out Win32.POINT pt)) return;

            int cmd = Win32.TrackPopupMenuEx(
                _hMenu,
                Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD,
                pt.X, pt.Y,
                ownerHwnd,
                IntPtr.Zero);

            // PostMessage(WM_NULL) after the menu dismisses is part of
            // the same KB article's recipe -- it wakes the owner's
            // message loop so subsequent menu invocations behave.
            Win32.PostMessage(ownerHwnd, Win32.WM_NULL,
                IntPtr.Zero, IntPtr.Zero);

            if (cmd == 0) return;   // user dismissed without selecting

            switch (cmd)
            {
                case CmdSwitch:       Actions.SwitchToPeer("tray"); break;
                case CmdClipboard:    ClipboardSync.Push("tray"); break;
                case CmdReconnect:    Serial.RequestReconnect(); break;
                case CmdToggleWindow: ToggleWindow(); break;
                case CmdQuit:         _onQuit?.Invoke(); break;
                // CmdHeader and CmdStatus are MF_GRAYED, never returned.
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Tray menu error: {ex.Message}");
        }
    }

    private static void ToggleWindow()
    {
        if (GuiHost.IsVisible) GuiHost.Hide();
        else GuiHost.Show();
    }

    // ---- Icon load ---------------------------------------------------

    private static Icon LoadIcon()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Resources", "janus_sm.ico");
            if (File.Exists(path))
            {
                return new Icon(path);
            }
            Console.WriteLine($"Tray icon not found at {path}; using system default.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Tray icon load error: {ex.Message}; using system default.");
        }
        return SystemIcons.Application;
    }
}