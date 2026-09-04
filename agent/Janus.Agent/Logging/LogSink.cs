using Janus.Agent.Gui;

namespace Janus.Agent.Logging;

// In-process log sink: a bounded ring buffer of LogLine records plus a
// LineAdded event so the GUI can tail without polling.
//
// The only entry point is Write(LogLine), called by GuiSink after
// Serilog produces a LogEvent. There is no string-overload fallback
// anymore -- everything is routed through Serilog now, and the old
// Console.WriteLine-inference path (with its InferLevel heuristic)
// went away in commit 3 when TeeWriter was replaced.
//
// Static-only state: the agent has a single process-wide log stream.
// Multiple subscribers to LineAdded are supported; each invoke is
// wrapped in try/catch so a misbehaving subscriber can't take the
// logging path down. Subscriber exceptions are surfaced via
// Debug.WriteLine so they appear in a debugger's Output window
// without recursing back into the logging pipeline.
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

    /// <summary>Append a line to the buffer. Called by GuiSink from
    /// Serilog's emission path.</summary>
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
        catch (Exception ex)
        {
            // A failing subscriber must never break the logging path
            // (this is where errors get reported). Debug.WriteLine
            // surfaces in a debugger's Output window without looping
            // back into Serilog / LogSink and re-triggering the same
            // failure.
            System.Diagnostics.Debug.WriteLine($"LogSink subscriber error: {ex.Message}");
        }
    }

    /// <summary>Snapshot of all lines currently in the buffer, in
    /// insertion order. Used by the GUI at startup to seed its view
    /// with existing history before live tailing begins.</summary>
    public static LogLine[] Snapshot()
    {
        lock (_lock)
        {
            return _lines.ToArray();
        }
    }
}