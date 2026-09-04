using Janus.Agent.Gui;

namespace Janus.Agent.Logging;

// Structured logging entry point. Every producer should call one of the
// four static methods here (Error, Warn, Info, Debug, Verbose) instead of
// Console.WriteLine. The category names the subsystem; the message is
// the actual line.
//
// Each call:
//   1. Builds a LogLine with DateTime.Now and LogSource.Agent.
//   2. Pushes it to LogSink (which fans out to the GUI's LineAdded
//      subscribers on whichever thread called us).
//   3. Also mirrors a formatted plain-text version to the raw stdout
//      (the TextWriter TeeWriter captured at construction), so anyone
//      tailing the process output on a real console still sees it.
//
// Step 3 deliberately bypasses TeeWriter. TeeWriter's role is to catch
// raw Console.WriteLine calls (from third-party code, unrefactored
// producers, or exception messages) and route them into LogSink as
// Info/System fallbacks. If Log.X went through Console.WriteLine, every
// entry would end up in LogSink twice.
//
// Thread safety: all five methods are safe to call from any thread.
// LogSink guards its buffer; TextWriter.WriteLine on the raw stdout is
// atomic for a single line.

internal static class Log
{
    public static void Error(LogCategory category, string message)
        => Emit(LogLevel.Error, category, message);

    public static void Warn(LogCategory category, string message)
        => Emit(LogLevel.Warn, category, message);

    public static void Info(LogCategory category, string message)
        => Emit(LogLevel.Info, category, message);

    public static void Debug(LogCategory category, string message)
        => Emit(LogLevel.Debug, category, message);

    public static void Verbose(LogCategory category, string message)
        => Emit(LogLevel.Verbose, category, message);

    private static void Emit(LogLevel level, LogCategory category, string message)
    {
        LogLine line = new(
            Timestamp: DateTime.Now,
            Level: level,
            Category: category,
            Source: LogSource.Agent,
            Message: message ?? string.Empty);

        LogSink.Write(line);

        // Mirror to stdout via the raw writer captured by TeeWriter.
        // Bypasses TeeWriter's own intercept path to avoid a duplicate
        // LogSink entry. If no TeeWriter is installed (unit test, other
        // host), the mirror is silently skipped -- LogSink still received
        // the structured line above.
        TeeWriter.InstalledPrimary?.WriteLine(line.ToConsoleLine());
    }
}