using System.ComponentModel.DataAnnotations;

namespace Janus.Agent.Logging;

/// <summary>
/// Which process/device the log line originated from
/// </summary>
internal enum LogSource
{
    [Display(Name="Agt")] Agent,
    [Display(Name="Crt")] Controller,
    [Display(Name="Brg")] Bridge,
}