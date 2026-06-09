using System.IO;
using System.Text;

namespace Janus.Agent.Logging;

// TextWriter that forwards every Write/WriteLine to both:
//   * the original Console.Out (so the hidden console still receives
//     all output -- it's the source of truth for Console.WriteLine
//     semantics, and "Show console" debug hatches can surface it)
//   * the LogSink, line-buffered (so we hand the GUI complete lines,
//     not partial fragments)
//
// Program.cs installs an instance via Console.SetOut very early, so
// every existing Console.WriteLine in the agent flows through here
// transparently -- no other module needs to know about it.
//
// Thread safety: Write(char) and Write(string) can interleave with
// each other across threads (Console doesn't synchronize beyond
// per-call basis), so the in-flight line buffer is mutex-guarded.
// Each completed line is pushed to LogSink atomically.

internal sealed class TeeWriter : TextWriter
{
    private readonly TextWriter _primary;
    private readonly StringBuilder _lineBuffer = new();
    private readonly object _bufferLock = new();

    public TeeWriter(TextWriter primary)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
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