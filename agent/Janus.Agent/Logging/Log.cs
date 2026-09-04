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

    // ---- LEGACY SHIMS (deleted at end of commit 4) -----------------
    //
    // These forward the sub-step-1b syntax to the new wrappers so
    // producer files continue to compile between commits 3 and 4.

    public static void Verbose(LogCategory category, string message) => Get(category).Verbose(message);
    public static void Debug  (LogCategory category, string message) => Get(category).Debug(message);
    public static void Info   (LogCategory category, string message) => Get(category).Info(message);
    public static void Warn   (LogCategory category, string message) => Get(category).Warn(message);
    public static void Error  (LogCategory category, string message) => Get(category).Error(message);

    private static CategoryLogger Get(LogCategory category) => category switch
    {
        LogCategory.Serial    => Serial,
        LogCategory.Switch    => Switch,
        LogCategory.Clipboard => Clipboard,
        LogCategory.Keyboard  => Keyboard,
        LogCategory.Mouse     => Mouse,
        _                     => System,
    };
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