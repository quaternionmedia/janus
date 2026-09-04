using Janus.Agent.Gui;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using System.Globalization;
using System.IO;

namespace Janus.Agent.Logging;

// Serilog sink that translates LogEvent into LogLine and pushes it to
// LogSink. GuiViewModel (via LogSink.LineAdded) is the ultimate
// consumer; keeping that decoupling means the WPF layer never sees
// Serilog types directly.
//
// Mapping decisions:
//   * Timestamp -- LogEvent.Timestamp is DateTimeOffset; we take .DateTime.
//   * Level     -- Fatal collapses to Error (we don't distinguish Fatal
//                  in the GUI; a fatal Serilog event still gets flagged
//                  red and, in practice, the process is about to die).
//   * Category  -- read from the "Category" property attached by
//                  CategoryLogger. Missing or unparseable falls back to
//                  System, since anything reaching GuiSink without a
//                  Category is either a stray writeline or a direct
//                  Serilog.Log call bypassing our wrapper.
//   * Source    -- always LogSource.Agent in Phase 1. Phase 4 will read
//                  a "Source" property here to distinguish Controller/
//                  Bridge lines arriving over UART.
//   * Message   -- fully rendered via RenderMessage. Structured
//                  properties collapse to their string form in the GUI;
//                  the file sink preserves them separately as JSON.
//
// Emit runs on whichever thread called Log.X (Serilog is synchronous
// by default). LogSink is thread-safe, so no marshalling needed here.

internal sealed class GuiSink : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        LogCategory category = ExtractCategory(logEvent);
        LogLevel level = MapLevel(logEvent.Level);
        string message = RenderMessageLiterally(logEvent);

        LogSink.Write(new LogLine(
            Timestamp: logEvent.Timestamp.DateTime,
            Level: level,
            Category: category,
            Source: LogSource.Agent,
            Message: message));
    }

    private static LogCategory ExtractCategory(LogEvent logEvent)
    {
        if (logEvent.Properties.TryGetValue("Category", out LogEventPropertyValue? prop)
            && prop is ScalarValue { Value: string categoryString }
            && Enum.TryParse(categoryString, ignoreCase: false, out LogCategory parsed))
        {
            return parsed;
        }
        return LogCategory.System;
    }

    private static LogLevel MapLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Fatal       => LogLevel.Error,
        LogEventLevel.Error       => LogLevel.Error,
        LogEventLevel.Warning     => LogLevel.Warn,
        LogEventLevel.Information => LogLevel.Info,
        LogEventLevel.Debug       => LogLevel.Debug,
        _                         => LogLevel.Verbose,
    };

    private static string RenderMessageLiterally(LogEvent logEvent)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        foreach (MessageTemplateToken token in logEvent.MessageTemplate.Tokens)
        {
            if (token is PropertyToken propToken
                && logEvent.Properties.TryGetValue(propToken.PropertyName, out LogEventPropertyValue? value))
            {
                // ScalarValue with a string payload: render raw (no quotes).
                // Everything else: fall through to Serilog's default renderer,
                // which handles numbers, structured objects, dictionaries, etc.
                if (value is ScalarValue { Value: string s })
                {
                    writer.Write(s);
                }
                else
                {
                    value.Render(writer, propToken.Format, CultureInfo.InvariantCulture);
                }
            }
            else
            {
                token.Render(logEvent.Properties, writer, CultureInfo.InvariantCulture);
            }
        }
        return writer.ToString();
    }
}