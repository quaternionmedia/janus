namespace Janus.Agent.Logging;

/// <summary>
/// Which process/device the log line originated from
/// </summary>
internal enum LogSource
{
    Agent,
    Controller,
    Bridge,
}