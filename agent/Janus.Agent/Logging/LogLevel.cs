using System.ComponentModel.DataAnnotations;

namespace Janus.Agent.Logging;

/// <summary>
/// Severity of a log line. Producers pick one at emission time.
/// The GUI uses it for coloring and filtering.
/// </summary>
internal enum LogLevel
{
    [Display(Name="TRACE")] Verbose,
    [Display(Name="DEBUG")] Debug,
    [Display(Name="INFO")]  Info,
    [Display(Name="WARN")]  Warn,
    [Display(Name="ERROR")] Error,
}