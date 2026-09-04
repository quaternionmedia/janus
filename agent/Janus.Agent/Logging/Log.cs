using Serilog;

namespace Janus.Agent.Logging;

// Structured logging facade over Serilog. Two call styles supported:
//
//   Preferred:  Log.Serial.Info("Serial connected: {PortName}", portName);
//   Legacy:     Log.Info(LogCategory.Serial, "Serial connected: COM7");
//
// The legacy overloads exist so commit 3 (Serilog setup) leaves the
// build compilable while commit 4 sweeps every call site to the
// preferred style. Delete the legacy section at the end of commit 4.
//
// Each CategoryLogger holds a Serilog ILogger with the "Category"
// property attached via ForContext. That property flows through
// enrichers, appears in the JSONL file output, and is read by GuiSink
// to route events into the GUI's LogSink with the right LogCategory
// enum value.
//
// The wrapper deliberately mirrors your team's method-name convention
// (Info / Warn instead of Serilog's Information / Warning) so call
// sites match what was already written in sub-step 1b.

internal static class Log
{
    // Per-category loggers. Constructed once at class-init time; each
    // wraps a Serilog ILogger with Category attached.
    public static readonly CategoryLogger Serial    = new(LogCategory.Serial);
    public static readonly CategoryLogger Switch    = new(LogCategory.Switch);
    public static readonly CategoryLogger Clipboard = new(LogCategory.Clipboard);
    public static readonly CategoryLogger Keyboard  = new(LogCategory.Keyboard);
    public static readonly CategoryLogger Mouse     = new(LogCategory.Mouse);
    public static readonly CategoryLogger System    = new(LogCategory.System);

    // ---- DEBUG SHORTCUTS --------------------------------
    //
    // Quick-grab logging that skips category selection when you're
    // mid-debug and don't want to think about categories. 
    //
    // Use sparingly. The whole reason CategoryLogger exists is to make
    // categorization compile-time-required; these shortcuts opt out of
    // that, so they're for temporary debug lines rather than shipped
    // producer code.

    public static void Verbose(string template, params object?[] args) => System.Verbose(template, args);
    public static void Debug  (string template, params object?[] args) => System.Debug(template, args);
    public static void Info   (string template, params object?[] args) => System.Info(template, args);
    public static void Warn   (string template, params object?[] args) => System.Warn(template, args);
    public static void Error  (string template, params object?[] args) => System.Error(template, args);

    // Exception overloads follow Serilog's convention: exception first,
    // then template + args. Preserves the stack trace in structured
    // output rather than baking it into the message string.
    public static void Warn (Exception ex, string template, params object?[] args) => System.Warn(ex, template, args);
    public static void Error(Exception ex, string template, params object?[] args) => System.Error(ex, template, args);
}

internal sealed class CategoryLogger
{
    private readonly ILogger _log;

    public CategoryLogger(LogCategory category)
    {
        // ForContext attaches the Category property to every event
        // this logger emits. Serilog's global logger must be assigned
        // (Serilog.Log.Logger = ...) BEFORE any CategoryLogger is
        // constructed -- Program.cs handles that ordering.
        _log = Serilog.Log.ForContext("Category", category.ToString());
    }

    public void Verbose(string template, params object?[] args) => _log.Verbose(template, args);
    public void Debug  (string template, params object?[] args) => _log.Debug(template, args);
    public void Info   (string template, params object?[] args) => _log.Information(template, args);
    public void Warn   (string template, params object?[] args) => _log.Warning(template, args);
    public void Error  (string template, params object?[] args) => _log.Error(template, args);

    // Exception overloads follow Serilog's convention: exception first,
    // then template + args. Preserves the stack trace in structured
    // output rather than baking it into the message string.
    public void Warn (Exception ex, string template, params object?[] args) => _log.Warning(ex, template, args);
    public void Error(Exception ex, string template, params object?[] args) => _log.Error(ex, template, args);
}