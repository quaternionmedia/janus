using System.IO;
using System.Text.Json;

namespace Janus.Agent.Gui.Shared;

// Reads and parses log events for a local date into LogLine records.
// Called by DiagnosticsViewModel both on initial date change (full
// load) and on periodic refresh while viewing today (incremental
// load, reading only lines appended since last time).
//
// TIMEZONE HANDLING.
// Serilog's File sink writes UTC-timestamped filenames and UTC @t
// values -- no config option changes this. A single LOCAL day can
// therefore span up to two UTC-named files. LoadAsync computes the
// UTC range for a local date and reads whichever of the up-to-two
// candidate files exist, filtering each line by UTC timestamp.
//
// INCREMENTAL READS.
// The caller (DiagnosticsViewModel) hands back the LineIndicesAfter
// dictionary from the previous call. LoadAsync skips that many
// lines per file, only parsing new content. Serilog's shared-mode
// file locking guarantees whole-line atomicity, so mid-line reads
// aren't a concern.
//
// Load semantics:
//   AnyFileExists = false  -- neither candidate file was on disk.
//                             Caller shows the "no logs" hint.
//   AnyFileExists = true,
//   Lines.Count == 0        -- files exist but nothing in the
//                             requested UTC range (or nothing new
//                             since the last incremental read).
//                             Caller does nothing / shows hint only
//                             on initial load.

internal static class HistoricalLogLoader
{
    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Janus", "logs");

    public static string PathForUtcDate(DateTime utcDate) =>
        Path.Combine(LogDirectory, $"agent-{utcDate:yyyyMMdd}.jsonl");

    /// <summary>Load events for the given local date.</summary>
    /// <param name="localDate">The local calendar date to load.</param>
    /// <param name="lineIndices">Optional map of file path -> line
    /// count already consumed. Pass null (or an empty dict) for a
    /// full load. Pass the previous call's result for an incremental
    /// load that only returns lines appended since.</param>
    public static async Task<HistoricalLoadResult> LoadAsync(
        DateTime localDate,
        Dictionary<string, long>? lineIndices = null)
    {
        DateTime localStart = DateTime.SpecifyKind(localDate.Date, DateTimeKind.Local);
        DateTime localEnd = localStart.AddDays(1);
        DateTime utcStart = localStart.ToUniversalTime();
        DateTime utcEnd = localEnd.ToUniversalTime();

        var candidateUtcDates = new List<DateTime> { utcStart.Date };
        if (utcEnd.Date != utcStart.Date)
        {
            candidateUtcDates.Add(utcEnd.Date);
        }

        var updatedIndices = new Dictionary<string, long>(
            lineIndices ?? new Dictionary<string, long>());
        var lines = new List<LogLine>();
        bool anyFileExists = false;

        foreach (DateTime utcDate in candidateUtcDates)
        {
            string path = PathForUtcDate(utcDate);
            if (!File.Exists(path)) continue;
            anyFileExists = true;

            long startIndex = updatedIndices.GetValueOrDefault(path, 0);
            (List<LogLine> fromFile, long endIndex) = await Task.Run(
                () => ParseFromLineIndex(path, utcStart, utcEnd, startIndex));

            lines.AddRange(fromFile);
            updatedIndices[path] = endIndex;
        }

        return new HistoricalLoadResult
        {
            Lines = lines,
            LineIndicesAfter = updatedIndices,
            AnyFileExists = anyFileExists,
        };
    }

    private static (List<LogLine> lines, long endLineIndex) ParseFromLineIndex(
        string path,
        DateTime utcStart,
        DateTime utcEnd,
        long startLineIndex)
    {
        var lines = new List<LogLine>();
        long lineIndex = 0;

        // FileShare.ReadWrite required: Serilog holds the current
        // day's file open for writing. Without shared write access,
        // Windows refuses to open it for reading.
        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);

        string? raw;
        while ((raw = reader.ReadLine()) != null)
        {
            if (lineIndex >= startLineIndex)
            {
                if (TryParseLine(raw, utcStart, utcEnd, out LogLine line))
                {
                    lines.Add(line);
                }
            }
            lineIndex++;
        }

        return (lines, lineIndex);
    }

    private static bool TryParseLine(
        string raw,
        DateTime utcStart,
        DateTime utcEnd,
        out LogLine line)
    {
        line = default!;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(raw);
            JsonElement root = doc.RootElement;

            if (!root.TryGetProperty("@t", out JsonElement tsEl)) return false;
            DateTime utcTimestamp = tsEl.GetDateTime();

            if (utcTimestamp < utcStart || utcTimestamp >= utcEnd) return false;

            string levelStr = "Information";
            if (root.TryGetProperty("@l", out JsonElement lvlEl))
            {
                levelStr = lvlEl.GetString() ?? "Information";
            }

            string categoryStr = "System";
            if (root.TryGetProperty("Category", out JsonElement catEl))
            {
                categoryStr = catEl.GetString() ?? "System";
            }

            string sourceStr = "Agent";
            if (root.TryGetProperty("Source", out JsonElement srcEl))
            {
                sourceStr = srcEl.GetString() ?? "Agent";
            }

            string message = string.Empty;
            if (root.TryGetProperty("@m", out JsonElement mEl))
            {
                message = mEl.GetString() ?? string.Empty;
            }
            else if (root.TryGetProperty("@mt", out JsonElement mtEl))
            {
                message = mtEl.GetString() ?? string.Empty;
            }

            // Append exception if present -- surfaces stack traces
            // in the diag view instead of just the one-line error.
            if (root.TryGetProperty("@x", out JsonElement xEl))
            {
                string exText = xEl.GetString() ?? string.Empty;
                if (!string.IsNullOrEmpty(exText))
                {
                    message += Environment.NewLine + exText;
                }
            }

            line = new LogLine(
                Timestamp: utcTimestamp.ToLocalTime(),
                Level:     ParseLevel(levelStr),
                Category:  ParseCategory(categoryStr),
                Source:    ParseSource(sourceStr),
                Message:   message);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static LogLevel ParseLevel(string s) => s switch
    {
        "Verbose"     => LogLevel.Verbose,
        "Debug"       => LogLevel.Debug,
        "Information" => LogLevel.Info,
        "Warning"     => LogLevel.Warn,
        "Error"       => LogLevel.Error,
        "Fatal"       => LogLevel.Error,
        _             => LogLevel.Info,
    };

    private static LogCategory ParseCategory(string s) =>
        Enum.TryParse<LogCategory>(s, ignoreCase: false, out var v) ? v : LogCategory.System;

    private static LogSource ParseSource(string s) =>
        Enum.TryParse<LogSource>(s, ignoreCase: false, out var v) ? v : LogSource.Agent;
}

internal sealed class HistoricalLoadResult
{
    public required List<LogLine> Lines { get; init; }
    public required Dictionary<string, long> LineIndicesAfter { get; init; }
    public required bool AnyFileExists { get; init; }
}