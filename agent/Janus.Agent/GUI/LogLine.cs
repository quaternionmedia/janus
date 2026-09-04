using Janus.Agent.Logging;
using System.Globalization;
// Disambiguate the WinForms/WPF type collisions that ImplicitUsings
// pulls in by default. With UseWindowsForms=true and UseWPF=true both
// enabled, "Brush", "Color", and "SolidColorBrush" are ambiguous
// between System.Drawing and System.Windows.Media. These aliases
// pin them to the WPF types we want everywhere in this file.
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Janus.Agent.Gui;

// LogLine: the structured model bound to each row of the log view.
// Producers build these via Log.X (Log.Info, Log.Warn, etc.) or, as a
// legacy fallback path, via LogSink.WriteLine(string) when raw
// Console.WriteLine calls reach TeeWriter.
//
// Fields:
//   Timestamp -- captured at emission via DateTime.Now
//   Level     -- severity, drives color and (Phase 3) filter-by-level
//   Category  -- subsystem, drives the main-view default filter
//   Source    -- which process the line came from (Agent, Controller,
//                Bridge). Only Agent is populated in Phase 1.
//   Message   -- the actual text
//
// Backward-compat computed properties (Text, Foreground) exist so the
// current GuiWindow.xaml bindings still work in commit 1. Commit 2
// rewrites the ItemTemplate to bind to Timestamp/Level/Category/
// Source/Message directly and colors the message segment via a
// converter -- at which point these two computed properties can go.

internal sealed record LogLine(
    DateTime Timestamp,
    LogLevel Level,
    LogCategory Category,
    LogSource Source,
    string Message)
{
    /// <summary>Backward-compat property: today's XAML ItemTemplate
    /// binds to Text. Just the message; timestamp/level/category will
    /// join it in the tabular layout in commit 2.</summary>
    public string Text => Message;

    /// <summary>Backward-compat property: today's XAML ItemTemplate
    /// binds Foreground to this. Color derives from Level only --
    /// old text-inference (Categorize) is dead.</summary>
    public Brush Foreground => LogLineColors.ForLevel(Level);

    /// <summary>Plain-text render for stdout mirroring by Log.X.
    /// Fixed-width columns padded so a real console displays them
    /// aligned. Format matches the mockup: TIME | LEVEL | CATEGORY |
    /// SOURCE : Message.</summary>
    public string ToConsoleLine()
    {
        string ts = Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        string lvl = Level.ToString().ToLowerInvariant().PadRight(7);
        string cat = Category.ToString().ToLowerInvariant().PadRight(9);
        string src = Source.ToString().ToLowerInvariant().PadRight(10);
        return $"{ts} | {lvl} | {cat} | {src} : {Message}";
    }
}

// Palette + Level -> Brush mapping. The old message-text-inference
// Categorize() is gone -- producers tag Level explicitly via Log.X, and
// the fallback path (LogSink.WriteLine(string)) infers Level from
// message text just enough to keep error lines red until every producer
// is migrated.

internal static class LogLineColors
{
    // VS Code / Docker Desktop dark theme palette. Frozen brushes so
    // they're safe to share across threads.
    public static readonly Brush Info    = Make(0xCC, 0xCC, 0xCC);
    public static readonly Brush Verbose = Make(0x85, 0x85, 0x85);
    public static readonly Brush Warn    = Make(0xDC, 0xDC, 0xAA);
    public static readonly Brush Error   = Make(0xF4, 0x87, 0x71);
    public static readonly Brush Success = Make(0x73, 0xC9, 0x91);
    public static readonly Brush Muted   = Verbose;

    public static Brush ForLevel(LogLevel level) => level switch
    {
        LogLevel.Error   => Error,
        LogLevel.Warn    => Warn,
        LogLevel.Debug   => Warn,
        LogLevel.Verbose => Verbose,
        _                => Info,
    };

    private static SolidColorBrush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}