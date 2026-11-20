using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using GitWorkspace.Core;

namespace GitWorkspace;

public sealed class RevisionDiffView : Grid
{
    public TextEditor Before { get; } = DocumentView.Editor();
    public TextEditor After { get; } = DocumentView.Editor();
    public IReadOnlyList<DiffLine> Rows { get; }
    private bool syncing, hooked;
    private ScrollViewer? beforeScroll, afterScroll;

    public RevisionDiffView(CommitFile file, RevisionDiff diff)
        : this(diff,
            $"{file.OldPath} · {(file.ParentHash == null ? "empty tree" : Short(file.ParentHash))}",
            $"{file.Path} · {Short(file.CommitHash)}") { }

    public RevisionDiffView(RevisionDiff diff, string beforeLabel, string afterLabel)
    {
        ColumnDefinitions.Add(new()); ColumnDefinitions.Add(new() { Width = new(5) }); ColumnDefinitions.Add(new());
        Rows = diff.Old.IsText && diff.New.IsText ? DiffAlignment.Align(diff.Old.Text, diff.New.Text, diff.Patch) : [];
        Setup(Before, true, diff.Old); Setup(After, false, diff.New);
        AddPane(Before, beforeLabel, 0, old: true);
        AddPane(After, afterLabel, 2, old: false);
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.DimGray };
        SetColumn(splitter, 1); Children.Add(splitter);
        // Reserve matching scrollbars so both viewports share the same height. Horizontal offsets are
        // intentionally not mirrored: a short side's max width must not rubber-band the long side.
        Before.HorizontalScrollBarVisibility = ScrollBarVisibility.Visible;
        After.HorizontalScrollBarVisibility = ScrollBarVisibility.Visible;
        Before.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
        After.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
        var first = Rows.ToList().FindIndex(r => r.Changed);
        Loaded += (_, _) =>
        {
            if (!hooked)
            {
                beforeScroll = FindScroll(Before); afterScroll = FindScroll(After);
                if (beforeScroll != null) beforeScroll.ScrollChanged += ScrollChanged;
                if (afterScroll != null) afterScroll.ScrollChanged += ScrollChanged;
                hooked = true;
            }
            if (first >= 0) Before.ScrollToLine(first + 1);
            MirrorVertical(Before, After);
        };
        Unloaded += (_, _) =>
        {
            if (!hooked) return;
            if (beforeScroll != null) beforeScroll.ScrollChanged -= ScrollChanged;
            if (afterScroll != null) afterScroll.ScrollChanged -= ScrollChanged;
            beforeScroll = afterScroll = null;
            hooked = false;
        };
    }

    private void ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Only lock vertical position. Horizontal extents differ per side; syncing them snaps the
        // longer pane back whenever ReleaseSync/Mirror copies the shorter side's max offset.
        if (syncing || e.VerticalChange == 0) return;
        if (sender == beforeScroll) MirrorVertical(Before, After);
        else if (sender == afterScroll) MirrorVertical(After, Before);
    }

    private void MirrorVertical(TextEditor from, TextEditor to)
    {
        if (NearlyEqual(from.VerticalOffset, to.VerticalOffset)) return;
        syncing = true;
        to.ScrollToVerticalOffset(from.VerticalOffset);
        // AvalonEdit may raise the follower's ScrollChanged after layout; keep the gate until then
        // so the follower cannot echo a stale offset back onto the leader.
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (!NearlyEqual(from.VerticalOffset, to.VerticalOffset))
                to.ScrollToVerticalOffset(from.VerticalOffset);
            syncing = false;
        }, DispatcherPriority.Background);
    }

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScroll(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) < 0.5;
    private static string Short(string hash) => hash[..Math.Min(8, hash.Length)];

    /// <summary>Scrolls to the changed row nearest <paramref name="relativeY"/> (0 = top, 1 = bottom).</summary>
    public bool JumpToChangeOverview(double relativeY, bool oldPane = false)
    {
        var index = NearestChangedIndex(Rows, Math.Clamp(relativeY, 0, 1));
        if (index < 0) return false;
        (oldPane ? Before : After).ScrollToLine(index + 1);
        return true;
    }

    private static int NearestChangedIndex(IReadOnlyList<DiffLine> rows, double relativeY)
    {
        if (rows.Count == 0) return -1;
        var estimate = rows.Count == 1 ? 0 : (int)Math.Round(relativeY * (rows.Count - 1));
        estimate = Math.Clamp(estimate, 0, rows.Count - 1);
        var nearest = -1;
        var best = int.MaxValue;
        for (var i = 0; i < rows.Count; i++)
        {
            if (!rows[i].Changed) continue;
            var distance = Math.Abs(i - estimate);
            if (distance >= best) continue;
            best = distance;
            nearest = i;
        }
        return nearest;
    }

    private void AddPane(TextEditor editor, string header, int column, bool old)
    {
        var panel = new DockPanel();
        var label = new TextBlock { Text = header, ToolTip = header, Margin = new(8), TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel.SetDock(label, Dock.Top);
        panel.Children.Add(label);
        var editorPane = new Grid();
        editorPane.Children.Add(editor);
        if (Rows.Any(row => row.Changed))
        {
            var markers = new ChangeMarkers(Rows, old, editor)
            {
                Width = 8,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new(0, 0, 16, 14),
                Cursor = Cursors.Hand,
                ToolTip = "Go to change"
            };
            editorPane.Children.Add(markers);
        }
        panel.Children.Add(editorPane);
        SetColumn(panel, column);
        Children.Add(panel);
    }
    private void Setup(TextEditor editor, bool old, FilePreview preview)
    {
        if (!preview.IsText || Rows.Count == 0) { editor.Text = preview.Text; return; }
        editor.Text = string.Join('\n', Rows.Select(r => old ? r.OldText : r.NewText));
        editor.ShowLineNumbers = false;
        editor.TextArea.LeftMargins.Insert(0, new Numbers(Rows, old));
        editor.TextArea.TextView.BackgroundRenderers.Add(new Shading(Rows, old));
    }
    private sealed class Shading(IReadOnlyList<DiffLine> rows, bool old) : IBackgroundRenderer
    {
        public KnownLayer Layer => KnownLayer.Background;
        public void Draw(TextView view, DrawingContext context)
        {
            if (!view.VisualLinesValid) return;
            foreach (var visual in view.VisualLines)
            {
                var index = visual.FirstDocumentLine.LineNumber - 1;
                if (index >= rows.Count) continue;
                var row = rows[index]; var gap = old ? row.OldNumber == null : row.NewNumber == null;
                var color = gap ? Color.FromRgb(45, 45, 45) : old ? Color.FromRgb(85, 30, 30) : Color.FromRgb(40, 70, 35);
                if (gap || row.Changed) context.DrawRectangle(new SolidColorBrush(color), null,
                    new Rect(0, visual.VisualTop - view.VerticalOffset, view.ActualWidth, visual.Height));
            }
        }
    }
    private sealed class ChangeMarkers(IReadOnlyList<DiffLine> rows, bool old, TextEditor editor) : FrameworkElement
    {
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (ActualHeight <= 0 || rows.Count == 0) return;
            var relativeY = Math.Clamp(e.GetPosition(this).Y, 0, ActualHeight) / ActualHeight;
            var index = NearestChangedIndex(rows, relativeY);
            if (index < 0) return;
            editor.ScrollToLine(index + 1);
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext context)
        {
            if (ActualHeight <= 0 || rows.Count == 0) return;
            var marker = old ? Color.FromRgb(210, 75, 75) : Color.FromRgb(105, 185, 95);
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(55, 55, 55)), null, new Rect(3, 0, 2, ActualHeight));
            for (var index = 0; index < rows.Count; index++)
            {
                if (!rows[index].Changed) continue;
                var top = rows.Count == 1 ? 0 : index * Math.Max(0, ActualHeight - 3) / (rows.Count - 1);
                context.DrawRectangle(new SolidColorBrush(marker), null, new Rect(0, top, 8, Math.Min(3, ActualHeight)));
            }
        }
    }
    private sealed class Numbers(IReadOnlyList<DiffLine> rows, bool old) : AbstractMargin
    {
        protected override Size MeasureOverride(Size availableSize) => new(52, 0);
        protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
        {
            if (oldTextView != null) oldTextView.VisualLinesChanged -= Refresh;
            base.OnTextViewChanged(oldTextView, newTextView);
            if (newTextView != null) newTextView.VisualLinesChanged += Refresh;
        }
        private void Refresh(object? sender, EventArgs e) => InvalidateVisual();
        protected override void OnRender(DrawingContext context)
        {
            if (TextView?.VisualLinesValid != true) return;
            foreach (var visual in TextView.VisualLines)
            {
                var index = visual.FirstDocumentLine.LineNumber - 1;
                if (index >= rows.Count) continue;
                var number = old ? rows[index].OldNumber : rows[index].NewNumber;
                if (number == null) continue;
                var label = new FormattedText(number.ToString()!, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Consolas"), 13, Brushes.Silver, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                context.DrawText(label, new Point(ActualWidth - label.Width - 8, visual.VisualTop - TextView.VerticalOffset));
            }
        }
    }
}
