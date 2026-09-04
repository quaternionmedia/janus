using Janus.Agent.Gui;

namespace Janus.Agent.Logging;

// In-process log sink: a bounded ring buffer of LogLine records plus a
// LineAdded event so the GUI can tail without polling.
//
// Two entry points:
//   * Write(LogLine)    -- structured path used by Log.X
//   * WriteLine(string) -- legacy fallback used by TeeWriter when raw
//                          Console.WriteLine calls reach it (unmigrated
//                          producers, third-party code, exception
//                          messages). Wraps the string into a LogLine
//                          with Level inferred from message text and
//                          category defaulted to System.
//
// Static-only state: the agent has a single process-wide log stream.
// Multiple subscribers to LineAdded are supported; each invoke is
// try/catch-guarded so a misbehaving subscriber can't take down the
// logging path.
//
// Capacity default 5000 lines. Typical session produces a few hundred
// lines/hour; 5000 gives multi-hour history. LogLine is a small record;
// 5000 of them plus their string Messages is well under 1 MB.

internal static class LogSink
{
    private const int MaxLines = 5000;

    private static readonly LinkedList<LogLine> _lines = new();
    private static readonly object _lock = new();

    /// <summary>Raised after a line has been appended. Subscribers may
    /// receive callbacks on any thread. UI subscribers must marshal to
    /// their own dispatcher.</summary>
    public static event Action<LogLine>? LineAdded;

    /// <summary>Structured append. Called by Log.X.</summary>
    public static void Write(LogLine line)
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

    /// <summary>Legacy fallback append. Called by TeeWriter for raw
    /// Console.WriteLine output. Infers Level from message text so error
    /// lines still render red until every producer migrates to Log.X;
    /// once migration is complete, the InferLevel heuristic can be
    /// deleted and this overload can go with it.</summary>
    public static void WriteLine(string line)
    {
        Write(new LogLine(
            Timestamp: DateTime.Now,
            Level: InferLevel(line),
            Category: LogCategory.System,
            Source: LogSource.Agent,
            Message: line ?? string.Empty));
    }

    /// <summary>Snapshot of all lines currently in the buffer, in
    /// insertion order. Used by the GUI at startup to seed its view
    /// with existing history before live tailing begins.</summary>
    public static LogLine[] Snapshot()
    {
        lock (_lock)
        {
            return [.. _lines];
        }
    }

    // ---- Fallback level inference (transient) ------------------------
    //
    // Keeps error/failure lines rendered red in the GUI even before
    // producers are migrated to Log.Error. Anything else gets Info.
    // Delete this method once sub-step 1b is finished.

    private static LogLevel InferLevel(string? line)
    {
        if (string.IsNullOrEmpty(line)) return LogLevel.Info;
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("refused", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Error;
        }
        return LogLevel.Info;
    }
}