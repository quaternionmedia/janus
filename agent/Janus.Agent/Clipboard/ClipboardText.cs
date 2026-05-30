using Janus.Agent.Platform;
using System.Security.Cryptography;
using System.Text;

namespace Janus.Agent.Clipboard;

// Clipboard text I/O + sync-loop dedup.
//
// All Clipboard.GetText / SetText calls are STA-only on Windows, so
// every public method here either runs on (or marshals to) the
// MessageWindow's STA thread. During startup/shutdown, when the window
// isn't yet ready, we fall back to a one-off STA thread per call.
//
// The hash dedup is what keeps the two-agent loop from bouncing
// forever in Auto outbound mode: when agent A applies an inbound
// clipboard payload, we record its hash BEFORE the SetText fires, so
// A's own monitor callback observes a hash-match and skips the
// re-broadcast.

internal static class ClipboardText
{
    // Hash of the last clipboard value this agent either sent out or
    // accepted inbound. Used to suppress the auto-sync feedback loop.
    private static string _lastSyncedHash = string.Empty;
    private static readonly object _hashLock = new();

    // ---- Text I/O -----------------------------------------------------

    public static string GetText()
    {
        if (!MessageWindow.IsHandleCreated)
        {
            // Window not ready (startup race) or disposed (shutdown).
            // Fall back to a one-off STA thread to preserve correctness.
            return GetTextOnFreshSTAThread();
        }

        string result = string.Empty;
        try
        {
            MessageWindow.Invoke(() =>
            {
                try
                {
                    if (System.Windows.Forms.Clipboard.ContainsText())
                    {
                        result = System.Windows.Forms.Clipboard.GetText();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Clipboard get error: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Clipboard get marshal error: {ex.Message}");
        }
        return result;
    }

    public static void SetText(string text)
    {
        if (!MessageWindow.IsHandleCreated)
        {
            SetTextOnFreshSTAThread(text);
            return;
        }

        Exception? capturedException = null;
        try
        {
            MessageWindow.Invoke(() =>
            {
                try
                {
                    System.Windows.Forms.Clipboard.SetText(text ?? string.Empty);
                }
                catch (Exception ex)
                {
                    capturedException = ex;
                }
            });
        }
        catch (Exception ex)
        {
            capturedException = ex;
        }

        if (capturedException is not null)
        {
            throw capturedException;
        }
    }

    // ---- One-off STA fallbacks (startup/shutdown only) ----------------

    private static string GetTextOnFreshSTAThread()
    {
        string result = string.Empty;
        Exception? capturedException = null;

        Thread thread = new(() =>
        {
            try
            {
                if (System.Windows.Forms.Clipboard.ContainsText())
                {
                    result = System.Windows.Forms.Clipboard.GetText();
                }
            }
            catch (Exception ex)
            {
                capturedException = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (capturedException is not null)
        {
            Console.WriteLine($"Clipboard get error: {capturedException.Message}");
        }

        return result;
    }

    private static void SetTextOnFreshSTAThread(string text)
    {
        Exception? capturedException = null;

        Thread thread = new(() =>
        {
            try
            {
                System.Windows.Forms.Clipboard.SetText(text ?? string.Empty);
            }
            catch (Exception ex)
            {
                capturedException = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (capturedException is not null)
        {
            throw capturedException;
        }
    }

    // ---- Hash dedup ---------------------------------------------------

    private static string ComputeHash(byte[] data)
    {
        byte[] hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }

    /// <summary>Record that <paramref name="rawBytes"/> was just sent or
    /// accepted. Subsequent monitor callbacks observing the same hash
    /// will be treated as a sync echo and not re-broadcast.</summary>
    public static void UpdateSyncedHash(byte[] rawBytes)
    {
        string hash = ComputeHash(rawBytes);
        lock (_hashLock)
        {
            _lastSyncedHash = hash;
        }
    }

    /// <summary>True if <paramref name="rawBytes"/> matches the most
    /// recently synced clipboard value.</summary>
    public static bool IsAlreadySynced(byte[] rawBytes)
    {
        string hash = ComputeHash(rawBytes);
        lock (_hashLock)
        {
            return string.Equals(hash, _lastSyncedHash, StringComparison.Ordinal);
        }
    }

    /// <summary>Seed the synced-hash tracking with whatever's on the
    /// clipboard right now. Best-effort: failures are silent because the
    /// first monitor event will just do one redundant send, which is
    /// harmless.</summary>
    public static void SeedHash()
    {
        try
        {
            string text = GetText();
            UpdateSyncedHash(Encoding.UTF8.GetBytes(text));
        }
        catch
        {
            // best-effort
        }
    }
}