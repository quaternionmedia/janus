using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Janus.Agent.Gui.Shared;

// Bridges an ObservableCollection<LogLine> to a RichTextBox's
// FlowDocument. Handles three concerns the built-in
// ItemsControl+CollectionView pattern would give us for free, but
// which we lose by moving to RichTextBox (needed for text
// selection):
//
//   1. Sync   -- CollectionChanged events on the source collection
//                translate to Paragraph adds/removes on the document,
//                respecting the current filter predicate.
//   2. Filter -- external Refilter() call rebuilds the visible set
//                (called by the view when the VM's SearchText or,
//                later, other filter state changes).
//   3. Scroll -- tracks whether the user has scrolled up. Autoscroll,
//                when enabled, snaps to bottom on new content only if
//                the user hadn't manually scrolled up.
//
// Why not ScrollViewer.ScrollToEnd bound to a property? RichTextBox
// doesn't expose its inner ScrollViewer directly -- it's inside the
// control template as PART_ContentHost. FindDescendant<ScrollViewer>
// after Loaded is the standard way to grab it.
//
// Remove handling: MainViewModel/DiagnosticsViewModel trim the
// oldest entry when the buffer exceeds MaxLines. Each RemoveAt(0)
// fires a Remove event; we pop the first paragraph iff the removed
// LogLine had passed the filter (in which case it was the first
// paragraph in the document, matching insertion order). Correct and
// O(1) instead of the O(n) rebuild-on-remove approach.

internal sealed class LogDocumentSync
{
    private readonly RichTextBox _rtb;
    private readonly FlowDocument _doc;
    private readonly ObservableCollection<LogLine> _source;
    private readonly Func<LogLine, bool> _filter;
    private readonly Brush _prefixBrush;

    private ScrollViewer? _scrollViewer;
    private bool _wasAtBottom = true;

    public bool AutoScrollEnabled { get; set; } = true;
    public bool WordWrapEnabled
    {
        get => double.IsNaN(_doc.PageWidth);
        set
        {
            // NaN = wrap to viewport width. Large finite value = no wrap
            // (horizontal scrollbar takes over instead).
            _doc.PageWidth = value ? double.NaN : 9999;
        }
    }
    
    public LogDocumentSync(
        RichTextBox richTextBox,
        ObservableCollection<LogLine> source,
        Func<LogLine, bool> filter,
        Brush prefixBrush)
    {
        _rtb = richTextBox;
        _source = source;
        _filter = filter;
        _prefixBrush = prefixBrush;

        _doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = _rtb.FontFamily,
            FontSize = _rtb.FontSize,
        };
        _rtb.Document = _doc;

        // ScrollViewer isn't in the visual tree until the RichTextBox
        // has been laid out. Loaded is the earliest reliable hook.
        _rtb.Loaded += (_, _) => AttachScrollViewer();

        _source.CollectionChanged += OnSourceChanged;

        Rebuild();
    }

    public void Detach()
    {
        try { _source.CollectionChanged -= OnSourceChanged; } catch { }
        if (_scrollViewer != null)
        {
            try { _scrollViewer.ScrollChanged -= OnScrollChanged; } catch { }
        }
    }

    /// <summary>Rebuild the entire visible document from the source
    /// collection. Called on filter changes. Preserves auto-scroll
    /// pin state.</summary>
    public void Rebuild()
    {
        _doc.Blocks.Clear();
        foreach (LogLine line in _source)
        {
            if (_filter(line))
            {
                _doc.Blocks.Add(BuildParagraph(line));
            }
        }
        if (AutoScrollEnabled)
        {
            _rtb.ScrollToEnd();
        }
    }

    /// <summary>Force scroll to the bottom regardless of auto-scroll
    /// state. Called when the user re-enables the auto-scroll
    /// checkbox.</summary>
    public void ScrollToEnd()
    {
        _rtb.ScrollToEnd();
        _wasAtBottom = true;
    }

    // ---- Source -> document sync -----------------------------------

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems != null:
                foreach (LogLine line in e.NewItems)
                {
                    if (_filter(line))
                    {
                        _doc.Blocks.Add(BuildParagraph(line));
                    }
                }
                if (AutoScrollEnabled && _wasAtBottom)
                {
                    _rtb.ScrollToEnd();
                }
                break;

            case NotifyCollectionChangedAction.Remove when e.OldItems != null:
                // Source only ever removes at index 0 (buffer trim).
                // Corresponding paragraph is the first one in the doc,
                // iff the removed line had passed the filter.
                foreach (LogLine line in e.OldItems)
                {
                    if (_filter(line) && _doc.Blocks.FirstBlock != null)
                    {
                        _doc.Blocks.Remove(_doc.Blocks.FirstBlock);
                    }
                }
                break;

            case NotifyCollectionChangedAction.Reset:
                Rebuild();
                break;
        }
    }

    private Paragraph BuildParagraph(LogLine line)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(0),
            TextIndent = 0,
            LineHeight = double.NaN,
        };
        p.Inlines.Add(new Run(line.Prefix) { Foreground = _prefixBrush });
        p.Inlines.Add(new Run(line.Message) { Foreground = line.Foreground });
        return p;
    }

    // ---- Scroll tracking -------------------------------------------

    private void AttachScrollViewer()
    {
        if (_scrollViewer != null) return;
        _scrollViewer = FindDescendant<ScrollViewer>(_rtb);
        if (_scrollViewer != null)
        {
            _scrollViewer.ScrollChanged += OnScrollChanged;
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange > 0)
        {
            if (AutoScrollEnabled && _wasAtBottom)
            {
                _rtb.ScrollToEnd();
            }
        }
        else if (e.VerticalChange != 0)
        {
            _wasAtBottom = IsAtBottom();
        }
    }

    private bool IsAtBottom()
    {
        if (_scrollViewer == null) return true;
        if (_scrollViewer.ScrollableHeight <= 0) return true;
        return _scrollViewer.VerticalOffset >= _scrollViewer.ScrollableHeight - 1;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) return typed;
            T? found = FindDescendant<T>(child);
            if (found != null) return found;
        }
        return null;
    }
}