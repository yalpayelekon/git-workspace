using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Rendering;
using GitWorkspace.Core;

namespace GitWorkspace;

public sealed class DocumentView : TabControl
{
    private readonly TextEditor file = FileEditor(), staged = Editor(), unstaged = Editor();
    private readonly DiagnosticRenderer diagnostics = new();
    private bool suppressTextChanged;
    private string cleanText = "";
    private string cleanHash = HashText("");
    private DateTime cleanWriteTimeUtc = DateTime.MinValue;
    public string RelativePath { get; private set; }
    public bool IsDirty { get; private set; }
    public bool CanEdit { get; private set; }
    public bool DiskChangedWhileDirty { get; private set; }
    public string CleanHash => cleanHash;
    public DateTime CleanWriteTimeUtc => cleanWriteTimeUtc;
    public string CurrentText => file.Text;
    public TextEditor FileEditorControl => file;
    public event Action? DirtyChanged;
    public event Action? ContentChanged;

    public DocumentView(string path)
    {
        RelativePath = path;
        file.TextArea.TextView.BackgroundRenderers.Add(diagnostics);
        file.TextChanged += (_, _) =>
        {
            if (suppressTextChanged || !CanEdit) return;
            var dirty = !string.Equals(file.Text, cleanText, StringComparison.Ordinal);
            if (dirty != IsDirty)
            {
                IsDirty = dirty;
                if (!dirty) DiskChangedWhileDirty = false;
                DirtyChanged?.Invoke();
            }
            if (IsDirty) ContentChanged?.Invoke();
        };
        Items.Add(new TabItem { Header = "File", Content = file });
        Items.Add(new TabItem { Header = "Staged diff", Content = staged });
        Items.Add(new TabItem { Header = "Working tree diff", Content = unstaged });
        staged.TextArea.TextView.LineTransformers.Add(new DiffColors());
        unstaged.TextArea.TextView.LineTransformers.Add(new DiffColors());
    }

    public void Update(FilePreview preview, string stagedText, string unstagedText, bool selectDiff, DateTime writeTimeUtc = default)
    {
        staged.Text = stagedText.Replace("\r\n", "\n");
        unstaged.Text = unstagedText.Replace("\r\n", "\n");
        if (selectDiff) SelectedIndex = stagedText is not "No staged changes." && !stagedText.StartsWith("Untracked") ? 1 : 2;

        // Callers refresh diffs above; skip replacing the File buffer while dirty.
        if (IsDirty) return;

        ApplyFilePreview(preview, writeTimeUtc);
    }

    public void UpdateDiffsOnly(string stagedText, string unstagedText)
    {
        staged.Text = stagedText.Replace("\r\n", "\n");
        unstaged.Text = unstagedText.Replace("\r\n", "\n");
    }

    public void NoteDiskChangedWhileDirty()
    {
        if (!IsDirty || DiskChangedWhileDirty) return;
        DiskChangedWhileDirty = true;
        DirtyChanged?.Invoke();
    }

    public void MarkClean(string savedText, DateTime writeTimeUtc)
    {
        var normalized = savedText.Replace("\r\n", "\n");
        suppressTextChanged = true;
        try
        {
            if (!string.Equals(file.Text, normalized, StringComparison.Ordinal))
                file.Text = normalized;
            cleanText = normalized;
            cleanHash = HashText(normalized);
            cleanWriteTimeUtc = writeTimeUtc;
            CanEdit = true;
            file.IsReadOnly = false;
            IsDirty = false;
            DiskChangedWhileDirty = false;
        }
        finally { suppressTextChanged = false; }
        DirtyChanged?.Invoke();
    }

    public void RevertToDisk(FilePreview preview, DateTime writeTimeUtc)
    {
        IsDirty = false;
        DiskChangedWhileDirty = false;
        ApplyFilePreview(preview, writeTimeUtc);
        DirtyChanged?.Invoke();
    }

    private void ApplyFilePreview(FilePreview preview, DateTime writeTimeUtc)
    {
        var normalized = preview.Text.Replace("\r\n", "\n");
        suppressTextChanged = true;
        try
        {
            CanEdit = preview.IsText;
            file.IsReadOnly = !CanEdit;
            if (!string.Equals(file.Text, normalized, StringComparison.Ordinal))
                file.Text = normalized;
            cleanText = CanEdit ? normalized : "";
            cleanHash = HashText(cleanText);
            cleanWriteTimeUtc = writeTimeUtc == default ? DateTime.MinValue : writeTimeUtc;
            IsDirty = false;
            DiskChangedWhileDirty = false;
        }
        finally { suppressTextChanged = false; }
        file.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    public void SetDiagnostics(IReadOnlyList<Problem> problems)
    {
        diagnostics.Problems = problems;
        file.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    public void GoTo(int line, int column)
    {
        SelectedIndex = 0;
        line = Math.Clamp(line, 1, file.Document.LineCount);
        column = Math.Clamp(column, 1, file.Document.GetLineByNumber(line).Length + 1);
        file.TextArea.Caret.Location = new(line, column);
        file.ScrollTo(line, column); file.Focus();
    }

    public void Retarget(string path) => RelativePath = path;

    public static string HashText(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n")));
        return Convert.ToHexString(bytes);
    }

    private static TextEditor FileEditor() => new()
    {
        Text = "", IsReadOnly = true, ShowLineNumbers = true, FontFamily = new("Consolas"), FontSize = 13,
        Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)), Foreground = new SolidColorBrush(Color.FromRgb(212, 212, 212)),
        LineNumbersForeground = new SolidColorBrush(Color.FromRgb(176, 176, 176)),
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        WordWrap = false, Padding = new(8)
    };

    public static TextEditor Editor(string text = "") => new()
    {
        Text = text.Replace("\r\n", "\n"), IsReadOnly = true, ShowLineNumbers = true, FontFamily = new("Consolas"), FontSize = 13,
        Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)), Foreground = new SolidColorBrush(Color.FromRgb(212, 212, 212)),
        LineNumbersForeground = new SolidColorBrush(Color.FromRgb(176, 176, 176)),
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        WordWrap = false, Padding = new(8)
    };

    public static TextEditor DiffEditor(string text)
    {
        var editor = Editor(text);
        editor.TextArea.TextView.LineTransformers.Add(new DiffColors());
        return editor;
    }

    private sealed class DiffColors : DocumentColorizingTransformer
    {
        protected override void ColorizeLine(ICSharpCode.AvalonEdit.Document.DocumentLine line)
        {
            var text = CurrentContext.Document.GetText(line);
            var brush = text.StartsWith('+') ? Brushes.LightGreen : text.StartsWith('-') ? Brushes.LightCoral : text.StartsWith("@@") ? Brushes.LightSkyBlue : null;
            if (brush != null) ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(brush));
        }
    }

    private sealed class DiagnosticRenderer : IBackgroundRenderer
    {
        public IReadOnlyList<Problem> Problems { get; set; } = Array.Empty<Problem>();
        public KnownLayer Layer => KnownLayer.Selection;
        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Problems.Count == 0 || textView.Document == null) return;
            textView.EnsureVisualLines();
            foreach (var problem in Problems)
            {
                var lineNumber = Math.Clamp(problem.Line, 1, textView.Document.LineCount);
                var line = textView.Document.GetLineByNumber(lineNumber);
                var column = Math.Clamp(problem.Column, 1, line.Length + 1);
                var start = line.Offset + column - 1;
                var end = Math.Min(line.EndOffset, start + Math.Max(1, Math.Min(24, line.EndOffset - start)));
                if (end <= start) end = Math.Min(line.EndOffset, start + 1);
                if (start >= line.EndOffset && line.Length > 0) { start = line.EndOffset - 1; end = line.EndOffset; }
                var brush = problem.Severity.Equals("error", StringComparison.OrdinalIgnoreCase) ? Brushes.IndianRed
                    : problem.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase) ? Brushes.Goldenrod
                    : Brushes.DodgerBlue;
                foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, new ICSharpCode.AvalonEdit.Document.TextSegment { StartOffset = start, EndOffset = end }))
                {
                    var y = rect.Bottom - 1.5;
                    var geometry = new StreamGeometry();
                    using (var context = geometry.Open())
                    {
                        context.BeginFigure(new Point(rect.Left, y), false, false);
                        var up = true;
                        for (var x = rect.Left; x < rect.Right; x += 3)
                        {
                            context.LineTo(new Point(x, up ? y - 2 : y + 1), true, false);
                            up = !up;
                        }
                    }
                    geometry.Freeze();
                    drawingContext.DrawGeometry(null, new Pen(brush, 1.2), geometry);
                }
            }
        }
    }
}
