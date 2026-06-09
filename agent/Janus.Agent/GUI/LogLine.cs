// Disambiguate the WinForms/WPF type collisions that ImplicitUsings
// pulls in by default. With UseWindowsForms=true and UseWPF=true both
// enabled, "Brush", "Color", and "SolidColorBrush" are ambiguous
// between System.Drawing and System.Windows.Media. These aliases
// pin them to the WPF types we want everywhere in this file.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Janus.Agent.Gui;

// LogLine: the model bound to each row of the log ListBox in the GUI.
// Holds the raw text plus a pre-resolved Brush so the XAML binds
// directly without needing a value converter.
//
// LogLineColors: the shared palette + a heuristic that picks a brush
// based on line content. Frozen brushes so they're safe to share
// across the WPF dispatcher thread and any background thread that
// might construct LogLines (the LogSink event fires on whichever
// thread called Console.WriteLine).

internal sealed class LogLine
{
    public string Text { get; }
    public Brush Foreground { get; }

    public LogLine(string text, Brush foreground)
    {
        Text = text;
        Foreground = foreground;
    }
}

internal static class LogLineColors
{
    // VS Code / Docker Desktop dark theme palette. Greyscale info as
    // default, blue for connection events, yellow for switch / target
    // changes, green for successful sync events, red for errors.

    public static readonly Brush Info       = Make(0xCC, 0xCC, 0xCC);
    public static readonly Brush Muted      = Make(0x85, 0x85, 0x85);
    public static readonly Brush Connection = Make(0x75, 0xBE, 0xFF);
    public static readonly Brush Switch     = Make(0xDC, 0xDC, 0xAA);
    public static readonly Brush Success    = Make(0x73, 0xC9, 0x91);
    public static readonly Brush Error      = Make(0xF4, 0x87, 0x71);

    private static Brush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Pick a foreground brush based on the line's content.
    /// First match wins; checks are ordered from highest-priority
    /// (error) to lowest (info default). Lightweight pattern matching
    /// on string content -- the agent doesn't have structured log
    /// levels and adding them isn't worth the churn just for this UI.
    /// </summary>
    public static Brush Categorize(string line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return Muted;
        }

        // Errors / failures first. Catches a wide net intentionally:
        // anything that contains "error" or "failed" is probably
        // something the user should see in red.
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("failed", StringComparison.OrdinalIgnoreCase)
            || line.Contains("refused", StringComparison.OrdinalIgnoreCase))
        {
            return Error;
        }

        // Active-target / switch changes. The "=== ACTIVE TARGET" line
        // is emitted by Serial; "switch (...)" lines are emitted by
        // Actions.SwitchToPeer.
        if (line.StartsWith("=== ACTIVE", StringComparison.Ordinal)
            || line.StartsWith("switch (", StringComparison.Ordinal))
        {
            return Switch;
        }

        // Connection events: serial port open/close, agent start/stop.
        if (line.StartsWith("Serial connected", StringComparison.Ordinal)
            || line.StartsWith("Serial disconnected", StringComparison.Ordinal)
            || line.StartsWith("Stopping agent", StringComparison.Ordinal)
            || line.StartsWith("Janus.Agent ", StringComparison.Ordinal))
        {
            return Connection;
        }

        // Successful clipboard / sync events.
        if (line.Contains(" sent (", StringComparison.Ordinal)
            || line.Contains(" received (", StringComparison.Ordinal)
            || line.Contains(" auto-sync sent", StringComparison.Ordinal))
        {
            return Success;
        }

        return Info;
    }
}