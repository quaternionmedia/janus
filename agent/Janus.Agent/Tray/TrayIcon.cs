using Janus.Agent.Clipboard;
using Janus.Agent.Events;
using Janus.Agent.Platform;

namespace Janus.Agent.Tray;

// Janus agent's system tray surface. Owns a single NotifyIcon and its
// context menu; provides the only always-visible UI handle on the
// agent once the console window is hidden.
//
// Lifetime / threading: the NotifyIcon and ContextMenuStrip both need
// a thread with a running message pump. Rather than spin up another
// STA thread for ourselves, we piggyback on MessageWindow's
// (Application.Run runs there for the clipboard listener + global
// hotkeys). Every UI mutation goes through MessageWindow.Invoke so we
// never touch WinForms state from the main composition thread.
//
// Menu layout (top to bottom):
//   - Header: "Janus.Agent (P)" or "(W)"           [disabled label]
//   - Status: "Connected" or "Disconnected"        [disabled label]
//   - --- separator ---
//   - Switch to peer            -> Actions.SwitchToPeer("tray")
//   - Send clipboard to peer    -> ClipboardSync.Push("tray")
//   - --- separator ---
//   - Reconnect                 -> Serial.RequestReconnect()
//   - --- separator ---
//   - Show window / Hide window -> toggle console visibility
//   - --- separator ---
//   - Quit                      -> onQuit() callback (typically cts.Cancel)
//
// Double-clicking the tray icon toggles console visibility (same as
// the "Show window"/"Hide window" item).

internal static class TrayIcon
{
    private static NotifyIcon? _icon;
    private static ContextMenuStrip? _menu;
    private static Action? _onQuit;
    private static string _deviceId = "P";

    // Items we mutate on menu open (status text, header tooltip,
    // toggle-window label). Held as fields so RefreshMenuState can
    // address them without walking the Items collection.
    private static ToolStripMenuItem? _headerItem;
    private static ToolStripMenuItem? _statusItem;
    private static ToolStripMenuItem? _toggleWindowItem;

    /// <summary>Start the tray icon. Must be called after
    /// MessageWindow.Start() (we marshal onto its STA thread).
    /// <paramref name="onQuit"/> fires when the user clicks Quit;
    /// typically this is cts.Cancel from Program. Calling more than
    /// once is a no-op.</summary>
    public static void Start(string deviceId, Action onQuit)
    {
        if (_icon is not null) return;

        _deviceId = deviceId;
        _onQuit = onQuit;

        MessageWindow.Invoke(() =>
        {
            try
            {
                _menu = BuildMenu();
                _icon = new NotifyIcon
                {
                    Icon = LoadIcon(),
                    Text = $"Janus.Agent ({_deviceId})",
                    ContextMenuStrip = _menu,
                    Visible = true,
                };
                _icon.DoubleClick += (_, _) => ToggleWindow();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Tray icon start error: {ex.Message}");
            }
        });
    }

    /// <summary>Hide and dispose the tray icon. Safe to call if Start
    /// was never called or already stopped.</summary>
    public static void Stop()
    {
        if (_icon is null) return;

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
                _menu?.Dispose();
                _menu = null;
            }
            catch
            {
                // Best-effort cleanup during shutdown.
            }
        });
    }

    // ---- Menu construction -------------------------------------------

    private static ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        // Refresh dynamic labels (status, show/hide) every time the
        // menu opens. Cheap; runs once per user gesture.
        menu.Opening += (_, _) => RefreshMenuState();

        _headerItem = new ToolStripMenuItem($"Janus.Agent ({_deviceId})")
        {
            Enabled = false,
        };

        _statusItem = new ToolStripMenuItem("Disconnected")
        {
            Enabled = false,
        };

        var switchItem = new ToolStripMenuItem(
            "Switch to peer",
            image: null,
            onClick: (_, _) => Actions.SwitchToPeer("tray"));

        var clipboardItem = new ToolStripMenuItem(
            "Send clipboard to peer",
            image: null,
            onClick: (_, _) => ClipboardSync.Push("tray"));

        var reconnectItem = new ToolStripMenuItem(
            "Reconnect",
            image: null,
            onClick: (_, _) => Serial.RequestReconnect())
        {
            ToolTipText = "Reconnect the serial port (close and reopen)",
        };

        _toggleWindowItem = new ToolStripMenuItem(
            "Show window",
            image: null,
            onClick: (_, _) => ToggleWindow());

        var quitItem = new ToolStripMenuItem(
            "Quit",
            image: null,
            onClick: (_, _) => _onQuit?.Invoke());

        menu.Items.Add(_headerItem);
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(switchItem);
        menu.Items.Add(clipboardItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(reconnectItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_toggleWindowItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quitItem);

        return menu;
    }

    // ---- Dynamic state refresh ---------------------------------------

    private static void RefreshMenuState()
    {
        bool isConnected = Serial.ActivePort?.IsOpen == true;
        string statusText = isConnected ? "Connected" : "Disconnected";

        if (_statusItem is not null)
        {
            _statusItem.Text = statusText;
        }

        if (_toggleWindowItem is not null)
        {
            _toggleWindowItem.Text = ConsoleWindow.IsVisible ? "Hide window" : "Show window";
        }

        // Tooltip is what shows on hover (no menu open). Update it so a
        // mouseover communicates the same status the menu would.
        if (_icon is not null)
        {
            // NotifyIcon.Text has a 127-character hard limit. Our format
            // is well under that, but truncate defensively.
            string tip = $"Janus.Agent ({_deviceId}) — {statusText}";
            _icon.Text = tip.Length > 127 ? tip[..127] : tip;
        }
    }

    private static void ToggleWindow()
    {
        if (ConsoleWindow.IsVisible)
        {
            ConsoleWindow.Hide();
        }
        else
        {
            ConsoleWindow.Show();
        }
    }

    // ---- Icon load ---------------------------------------------------

    private static Icon LoadIcon()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Resources", "janus.ico");
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