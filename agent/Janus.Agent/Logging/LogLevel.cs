namespace Janus.Agent.Logging;

/// <summary>
/// Severity of a log line. Producers pick one at emission time; the GUI
/// uses it for coloring and (in the diagnostics view later) filtering.
/// </summary>
internal enum LogLevel
{
    // Ordering is semantic, from noisiest to quietest -- Verbose is the most
    // chatty (mouse cursor sends, keepalives, TARGET echoes), Error is the
    // rarest and most attention-worthy. If a "greater than or equal to" style
    // threshold ever gets added, sort with Error highest.
    Verbose,
    Debug,
    Info,
    Warn,
    Error,
}