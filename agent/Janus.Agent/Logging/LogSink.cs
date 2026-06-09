namespace Janus.Agent.Logging;

// In-process log sink: a bounded ring buffer of every line written
// through Console.WriteLine (after Program.cs swaps in the TeeWriter)
// plus a LineAdded event so the GUI can tail new lines without
// polling.
//
// Static-only state: the agent has a single process-wide stream of
// log lines. No reason to multiplex. The LineAdded event can have
// multiple subscribers (GUI, future file logger, etc.); each invoke
// is wrapped in try/catch so a single misbehaving subscriber can't
// take the whole logging path down.
//
// Capacity defaults to 5000 lines, intentionally generous: a session
// might produce a few hundred lines per hour during normal use, so
// 5000 gives the user multiple-hour visibility into past activity
// without unbounded growth. At ~100 chars per line average, that's
// ~500 KB of live string references -- negligible.

internal static class LogSink
{
    private const int MaxLines = 5000;

    private static readonly LinkedList<string> _lines = new();
    private static readonly object _lock = new();

    /// <summary>Raised after a line has been appended. Subscribers may
    /// receive callbacks on any thread (whichever thread called
    /// Console.WriteLine). UI subscribers must marshal back to their
    /// own dispatcher.</summary>
    public static event Action<string>? LineAdded;

    /// <summary>Append a line to the buffer. Trims the oldest line if
    /// over capacity. Then raises LineAdded.</summary>
    public static void WriteLine(string line)
    {
        lock (_lock)
        {
            _lines.AddLast(line);
            while (_lines.Count > MaxLines)
            {
                _lines.RemoveFirst();
            }
        }

        try
        {
            LineAdded?.Invoke(line);
        }
        catch
        {
            // A failing subscriber must never break the logging path
            // (which is the only place errors get reported in the
            // first place). Swallow and move on.
        }
    }

    /// <summary>Snapshot of all lines currently in the buffer, in
    /// insertion order. Used by the GUI at startup to seed its view
    /// with existing history before live tailing begins.</summary>
    public static string[] Snapshot()
    {
        lock (_lock)
        {
            return _lines.ToArray();
        }
    }
}