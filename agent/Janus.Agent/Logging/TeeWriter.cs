using System.IO;
using System.Text;

namespace Janus.Agent.Logging;

// TextWriter that forwards every Write/WriteLine to both:
//   * the original Console.Out (so the hidden console still receives
//     all output -- source of truth for Console.WriteLine semantics,
//     and "Show console" debug hatches can surface it)
//   * the LogSink, line-buffered, as the fallback path for raw
//     Console.WriteLine calls (unmigrated producers, third-party code,
//     exception messages). Lines routed through LogSink.WriteLine(string)
//     get Level inferred from text and Category defaulted to System.
//
// Program.cs installs an instance via Console.SetOut very early, so
// every existing Console.WriteLine in the agent flows through here
// transparently. The instance also publishes its primary writer as a
// static property (InstalledPrimary) so Log.X can mirror structured
// lines to stdout without triggering a duplicate LogSink entry.
//
// Thread safety: Write(char)/Write(string) can interleave across
// threads; the in-flight line buffer is mutex-guarded and each completed
// line is pushed atomically.

internal sealed class TeeWriter : TextWriter
{
    private readonly TextWriter _primary;
    private readonly StringBuilder _lineBuffer = new();
    private readonly object _bufferLock = new();

    // The primary writer of the most recently constructed TeeWriter.
    // Log.X reads this to mirror structured lines to stdout. Null when
    // no TeeWriter has been installed (test host, other embedding).
    private static TextWriter? _installedPrimary;
    public static TextWriter? InstalledPrimary => _installedPrimary;

    public TeeWriter(TextWriter primary)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _installedPrimary = _primary;
    }

    public override Encoding Encoding => _primary.Encoding;

    public override void Write(char value)
    {
        _primary.Write(value);
        lock (_bufferLock)
        {
            AppendCharLocked(value);
        }
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        _primary.Write(value);
        lock (_bufferLock)
        {
            foreach (char c in value)
            {
                AppendCharLocked(c);
            }
        }
    }

    public override void WriteLine()
    {
        _primary.WriteLine();
        lock (_bufferLock)
        {
            FlushLineLocked();
        }
    }

    public override void WriteLine(string? value)
    {
        _primary.WriteLine(value);
        lock (_bufferLock)
        {
            if (value is not null)
            {
                foreach (char c in value)
                {
                    // Don't recurse into AppendCharLocked for embedded
                    // newlines here -- WriteLine semantics treat the
                    // whole string as one line, then add a final newline.
                    if (c != '\r' && c != '\n')
                    {
                        _lineBuffer.Append(c);
                    }
                }
            }
            FlushLineLocked();
        }
    }

    private void AppendCharLocked(char c)
    {
        if (c == '\n')
        {
            FlushLineLocked();
        }
        else if (c != '\r')
        {
            // CR alone (from \r\n pair, or bare \r) is dropped --
            // we'll see the \n next and flush there.
            _lineBuffer.Append(c);
        }
    }

    private void FlushLineLocked()
    {
        string line = _lineBuffer.ToString();
        _lineBuffer.Clear();
        // Push outside the lock would be ideal to avoid holding it
        // across event subscribers, but doing so would require copying
        // the line and re-acquiring lock state. Lock-hold is bounded
        // by LogSink's own work which is just an O(1) list operation
        // + subscriber invocation. Acceptable.
        LogSink.WriteLine(line);
    }
}