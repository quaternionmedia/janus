using System.IO;
using System.Text;

namespace Janus.Agent.Logging;

// TextWriter installed on Console.Out (via Console.SetOut) to catch
// anything that bypasses Serilog and writes directly to standard
// output -- third-party libraries, framework diagnostics, unhandled
// exception dumps, or forgotten producer sites.
//
// Each completed line becomes a Warn under Category.System with a
// distinctive "stray Console.WriteLine:" prefix so a Diag view search
// finds them quickly.
//
// MUST NOT be installed when Serilog's Console sink is active --
// that sink also writes to Console.Out, which would loop through here
// back into Serilog and produce recursive log entries. Program.cs
// only installs this in Production; Development uses the Console sink
// instead, and any strays there are still visible in the terminal.
//
// Buffering rationale: Console API can call Write(char) or Write(string)
// with partial lines. We accumulate until a newline arrives, then
// flush the complete line to Serilog. Same pattern the old TeeWriter
// used -- differences are (a) destination is Serilog not LogSink,
// (b) severity is Warn not Info-fallback, (c) marked as a stray so
// it stands out.

internal sealed class SerilogConsoleTee : TextWriter
{
    private readonly StringBuilder _lineBuffer = new();
    private readonly object _bufferLock = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_bufferLock)
        {
            AppendCharLocked(value);
        }
    }

    public override void Write(string? value)
    {
        if (value is null) return;
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
        lock (_bufferLock)
        {
            FlushLineLocked();
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_bufferLock)
        {
            if (value is not null)
            {
                foreach (char c in value)
                {
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
            _lineBuffer.Append(c);
        }
    }

    private void FlushLineLocked()
    {
        if (_lineBuffer.Length == 0) return;
        string line = _lineBuffer.ToString();
        _lineBuffer.Clear();
        // Warn (not Info) so strays visibly stand out as "something
        // bypassed the logger" -- easy to grep and act on.
        Log.System.Warn("stray Console.WriteLine: {StrayText}", line);
    }
}