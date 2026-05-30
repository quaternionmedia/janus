namespace Janus.Agent.Platform;

// Janus agent's STA + message-only window. Hosts a hidden WinForms
// form on its own STA thread so it can receive WM_CLIPBOARDUPDATE and
// WM_HOTKEY messages from the OS. Subscribers register callbacks for
// the events they care about; the window dispatches each WndProc
// message to the matching callback(s).
//
// Why this lives in Platform: clipboard listening and global hotkeys
// both require a window handle and an STA message pump. Centralizing
// the infrastructure here keeps the clipboard module focused on
// clipboard logic and lets future subsystems (tray icon, custom
// shortcuts) plug in without owning their own STA thread.

internal static class MessageWindow
{
    private static HiddenForm? _form;
    private static Thread? _thread;
    private static readonly ManualResetEventSlim _ready = new(initialState: false);

    /// <summary>True once the underlying window handle exists and the form
    /// is still alive.</summary>
    public static bool IsHandleCreated =>
        _form is not null && !_form.IsDisposed && _form.IsHandleCreated;

    /// <summary>True if the calling thread is NOT the STA thread that owns
    /// the form.</summary>
    public static bool InvokeRequired => _form?.InvokeRequired == true;

    /// <summary>Start the STA thread and the message pump. Blocks until
    /// the window's handle has been created, so callers can register
    /// listeners immediately after this returns. Calling more than once
    /// is a no-op.</summary>
    public static void Start()
    {
        if (_thread is not null) return;

        _thread = new Thread(() =>
        {
            try
            {
                _form = new HiddenForm();
                _ = _form.Handle;       // force handle creation before signaling ready
                _ready.Set();
                Application.Run(_form);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MessageWindow thread error: {ex.Message}");
                _ready.Set();           // unblock waiters even on failure
            }
        })
        {
            IsBackground = true,
            Name = "MessageWindow",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(2000);
    }

    /// <summary>Close the window, end the message pump, and join the STA
    /// thread. Safe to call if Start() was never called.</summary>
    public static void Stop()
    {
        var form = _form;
        if (form is null) return;
        try
        {
            if (form.IsHandleCreated && !form.IsDisposed)
            {
                form.BeginInvoke(new Action(() =>
                {
                    try { form.Close(); } catch { }
                }));
            }
        }
        catch { }
        try { _thread?.Join(500); } catch { }
        _form = null;
        _thread = null;
    }

    /// <summary>Subscribe to WM_CLIPBOARDUPDATE. Only one listener is
    /// supported; calling again replaces the previous callback.</summary>
    public static void RegisterClipboardListener(Action onChange)
    {
        _form?.RegisterClipboardListener(onChange);
    }

    /// <summary>Register a global hotkey (works from any focused window).
    /// Returns a handle for UnregisterHotkey, or -1 if registration failed
    /// (e.g. another app owns the combo). MOD_NOREPEAT is always applied
    /// so holding the keys fires once, not repeatedly.</summary>
    public static int RegisterHotkey(
        bool ctrl, bool shift, bool alt, string key,
        Action onPressed, string label)
    {
        return _form?.RegisterHotkey(ctrl, shift, alt, key, onPressed, label) ?? -1;
    }

    public static void UnregisterHotkey(int id)
    {
        _form?.UnregisterHotkey(id);
    }

    /// <summary>Run <paramref name="work"/> on the STA thread. If the
    /// window isn't started (or has been stopped), runs synchronously on
    /// the calling thread -- clipboard callers should check
    /// IsHandleCreated first and fall back to their own one-off STA
    /// pattern in that case.</summary>
    public static void Invoke(Action work)
    {
        var form = _form;
        if (form is null || form.IsDisposed || !form.IsHandleCreated)
        {
            work();
            return;
        }
        if (form.InvokeRequired)
        {
            form.Invoke(work);
        }
        else
        {
            // Already on the STA thread; run directly to avoid a needless
            // marshal hop.
            work();
        }
    }

    // ---- The actual hidden form ------------------------------------------

    private sealed class HiddenForm : Form
    {
        private const int WmClipboardupdate = 0x031D;
        private const int WmHotkey = 0x0312;

        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModNorepeat = 0x4000;

        private const int HotkeyIdBase = 0xB000;
        private int _nextHotkeyId = HotkeyIdBase;

        private Action? _onClipboardChange;
        private readonly Dictionary<int, Action> _hotkeyCallbacks = new();
        private bool _clipboardListenerActive;

        public HiddenForm()
        {
            // Hidden, off-screen, zero opacity, never in taskbar. We're
            // really only here for the HWND and message pump.
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Location = new System.Drawing.Point(-2000, -2000);
            Size = new System.Drawing.Size(1, 1);
            Opacity = 0;
            Load += (_, _) => Hide();
        }

        public void RegisterClipboardListener(Action onChange)
        {
            _onClipboardChange = onChange;
            if (!_clipboardListenerActive)
            {
                Win32.AddClipboardFormatListener(Handle);
                _clipboardListenerActive = true;
            }
        }

        public int RegisterHotkey(
            bool ctrl, bool shift, bool alt, string key,
            Action onPressed, string label)
        {
            if (string.IsNullOrEmpty(key))
            {
                Console.WriteLine($"{label} hotkey enabled but no key configured; skipping.");
                return -1;
            }

            uint mods = ModNorepeat;
            if (ctrl) mods |= ModControl;
            if (shift) mods |= ModShift;
            if (alt) mods |= ModAlt;

            // For A-Z and 0-9 the VK code equals the ASCII code of the
            // uppercase character.
            uint vk = key[0];
            int id = _nextHotkeyId++;
            bool ok = Win32.RegisterHotKey(Handle, id, mods, vk);
            string combo =
                (ctrl ? "Ctrl+" : "")
                + (shift ? "Shift+" : "")
                + (alt ? "Alt+" : "")
                + key;
            if (ok)
            {
                _hotkeyCallbacks[id] = onPressed;
                Console.WriteLine($"{label} hotkey registered: {combo}");
                return id;
            }
            else
            {
                Console.WriteLine(
                    $"{label} hotkey registration failed ({combo}); another app may own this combo.");
                return -1;
            }
        }

        public void UnregisterHotkey(int id)
        {
            if (_hotkeyCallbacks.Remove(id))
            {
                Win32.UnregisterHotKey(Handle, id);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmClipboardupdate)
            {
                try
                {
                    _onClipboardChange?.Invoke();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Clipboard change handler error: {ex.Message}");
                }
            }
            else if (m.Msg == WmHotkey)
            {
                int id = m.WParam.ToInt32();
                if (_hotkeyCallbacks.TryGetValue(id, out var cb))
                {
                    try
                    {
                        cb();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Hotkey handler error (id {id:X}): {ex.Message}");
                    }
                }
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                foreach (int id in _hotkeyCallbacks.Keys.ToList())
                {
                    Win32.UnregisterHotKey(Handle, id);
                }
                _hotkeyCallbacks.Clear();

                if (_clipboardListenerActive)
                {
                    Win32.RemoveClipboardFormatListener(Handle);
                    _clipboardListenerActive = false;
                }
            }
            catch { }
            base.Dispose(disposing);
        }
    }
}