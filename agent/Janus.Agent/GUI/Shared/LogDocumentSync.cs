using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Janus.Agent.Gui.Shared;

// Bridges an ObservableCollection<LogLine> to a RichTextBox's
// FlowDocument. Handles three concerns:
//
//   1. Sync   -- CollectionChanged events on the source collection
//                translate to Paragraph adds/removes on the document,
//                respecting the current filter predicate.
//   2. Filter -- external Rebuild() call rebuilds the visible set
//                (called by the view when filter state changes).
//   3. Scroll -- always pins to bottom when new content arrives IF
//                the user was at the bottom. If the user has
//                scrolled up, new content lands off-screen and the
//                view stays put. Standard Notepad++ / VS Code
//                output behavior. When at-bottom state flips
//                (either direction), ScrollStateChanged fires so
//                the view can show/hide its jump-to-bottom pill.
//
// No AutoScrollEnabled toggle -- the pin-to-bottom-if-at-bottom
// behavior is universal. The view surfaces a manual "jump to
// bottom" pill that becomes visible when the user is scrolled up.

internal sealed class LogDocumentSync
{
    private readonly RichTextBox _rtb;
    private readonly FlowDocument _doc;
    private readonly ObservableCollection<LogLine> _source;
    private readonly Func<LogLine, bool> _filter;
    private readonly Brush _prefixBrush;

    private ScrollViewer? _scrollViewer;
    private bool _wasAtBottom = true;

    /// <summary>Fires whenever the user's scroll position crosses
    /// the "at bottom" threshold. Argument is the new IsAtBottom
    /// state (true = at bottom, false = scrolled up).</summary>
    public event Action<bool>? ScrollStateChanged;

    public bool IsAtBottom => _wasAtBottom;

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

    public bool WordWrapEnabled
    {
        get => double.IsNaN(_doc.PageWidth);
        set => _doc.PageWidth = value ? double.NaN : 9999;
    }

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
        if (_wasAtBottom)
        {
            _rtb.ScrollToEnd();
        }
    }

    /// <summary>Force scroll to the bottom. Called by the view when
    /// the user clicks the jump-to-bottom pill.</summary>
    public void ScrollToEnd()
    {
        _rtb.ScrollToEnd();
        // The subsequent ScrollChanged will set _wasAtBottom = true
        // and fire ScrollStateChanged, causing the pill to hide.
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
                if (_wasAtBottom)
                {
                    _rtb.ScrollToEnd();
                }
                break;

            case NotifyCollectionChangedAction.Remove when e.OldItems != null:
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
            // New content added. Snap to bottom iff the user was
            // already there; otherwise let the new content land
            // off-screen so the user's reading position is preserved.
            if (_wasAtBottom)
            {
                _rtb.ScrollToEnd();
            }
        }
        else if (e.VerticalChange != 0)
        {
            bool nowAtBottom = ComputeIsAtBottom();
            if (nowAtBottom != _wasAtBottom)
            {
                _wasAtBottom = nowAtBottom;
                ScrollStateChanged?.Invoke(nowAtBottom);
            }
        }
    }

    private bool ComputeIsAtBottom()
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

    /// <summary>Full rebuild with a forced snap to bottom regardless of
    /// prior scroll position. Called by the view on date change so a
    /// fresh day always lands at the newest events.</summary>
    public void RebuildAndScrollToEnd()
    {
        _wasAtBottom = true;
        Rebuild();
        // Rebuild's ScrollToEnd already fires because _wasAtBottom is
        // now true, but call it again explicitly in case the previous
        // scroll state left the underlying ScrollViewer wonky after
        // the doc rebuild.
        _rtb.ScrollToEnd();
        ScrollStateChanged?.Invoke(true);
    }
}