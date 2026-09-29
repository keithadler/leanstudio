using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;
using LeanStudio.App.Editor;
using LeanStudio.App.Services;
using LeanStudio.Core.Editing;
using TextMateSharp.Grammars;

namespace LeanStudio.App.Views;

/// <summary>Two versions of a file to compare side by side.</summary>
/// <param name="Title">What is compared, for the window's title.</param>
/// <param name="LeftTitle">The older version: <c>HEAD (abc1234)</c>, a saved version's date.</param>
/// <param name="Left">Its text.</param>
/// <param name="RightTitle">The newer version, usually <c>now</c>.</param>
/// <param name="Right">Its text.</param>
/// <param name="Path">The file, for its highlighting.</param>
public sealed record DiffRequest(string Title, string LeftTitle, string Left, string RightTitle, string Right, string Path);

/// <summary>
/// A side-by-side diff: the two versions in read-only editors that scroll together, each line level with its
/// counterpart, removed lines red on the left, added lines green on the right, and in a changed line the part that
/// changed brighter. Each side numbers its own lines. <b>Next change</b> and <b>Previous change</b> (F7, Shift+F7)
/// step through the differences.
/// </summary>
public sealed class DiffView : UserControl
{
    private readonly TextEditor _left, _right;
    private readonly IReadOnlyList<DiffRow> _rows;
    private readonly List<int> _changes = [];
    private readonly TextBlock _where = new() { FontSize = 12, Opacity = 0.75, VerticalAlignment = VerticalAlignment.Center };
    private bool _syncing;
    private int _current = -1;

    /// <summary>Show <paramref name="request"/>.</summary>
    public DiffView(DiffRequest request)
    {
        Request = request;
        string[] a = TextDiff.Lines(request.Left), b = TextDiff.Lines(request.Right);
        _rows = TextDiff.SideBySide(a, b);
        // Level the two sides: a row missing on one side is a blank line there.
        string leftText = string.Join('\n', _rows.Select(r => r.Left is int l ? a[l] : ""));
        string rightText = string.Join('\n', _rows.Select(r => r.Right is int l ? b[l] : ""));
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Kind != DiffKind.Same && (i == 0 || _rows[i - 1].Kind == DiffKind.Same))
            {
                _changes.Add(i); // the first row of each run of changes
            }
        }
        _left = Side(leftText, request.Path, isLeft: true);
        _right = Side(rightText, request.Path, isLeft: false);
        _left.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_left, _right);
        _right.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_right, _left);

        (int added, int removed) = TextDiff.Count(_rows);
        Summary = _changes.Count == 0 ? "No differences." : $"{_changes.Count} change{(_changes.Count == 1 ? "" : "s")}: +{added} −{removed} lines";
        var prev = new Button { Content = "↑ Previous", Classes = { "chip" } };
        var next = new Button { Content = "↓ Next change", Classes = { "chip" } };
        ToolTip.SetTip(prev, "Previous change (Shift+F7)");
        ToolTip.SetTip(next, "Next change (F7)");
        prev.Click += (_, _) => Step(-1);
        next.Click += (_, _) => Step(1);
        var bar = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto,Auto"), Margin = new Thickness(12, 8) };
        bar.Children.Add(new TextBlock { Text = Summary, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(_where);
        buttons.Children.Add(prev);
        buttons.Children.Add(next);
        Grid.SetColumn(buttons, 2);
        bar.Children.Add(buttons);

        var titles = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,4,*") };
        titles.Children.Add(Title(request.LeftTitle, 0));
        titles.Children.Add(Title(request.RightTitle, 2));
        var panes = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,4,*") };
        panes.Children.Add(_left);
        var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 1);
        panes.Children.Add(splitter);
        Grid.SetColumn(_right, 2);
        panes.Children.Add(_right);
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(titles, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(titles);
        dock.Children.Add(panes);
        Content = dock;
        Focusable = true;
    }

    /// <summary>What is shown.</summary>
    public DiffRequest Request { get; }

    /// <summary>The rows, level across the two sides.</summary>
    public IReadOnlyList<DiffRow> Rows => _rows;

    /// <summary>How many separate changes there are, and how many lines they add and remove, in words.</summary>
    public string Summary { get; }

    /// <summary>The first row of each change.</summary>
    public IReadOnlyList<int> Changes => _changes;

    /// <summary>The change shown (an index into <see cref="Changes"/>), or -1 before stepping.</summary>
    public int CurrentChange => _current;

    /// <summary>The left side's editor (for checks).</summary>
    public TextEditor LeftEditor => _left;

    /// <summary>The right side's editor (for checks).</summary>
    public TextEditor RightEditor => _right;

    private static Border Title(string text, int column)
    {
        var b = new Border
        {
            Padding = new Thickness(12, 4),
            Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
        };
        Grid.SetColumn(b, column);
        return b;
    }

    private TextEditor Side(string text, string path, bool isLeft)
    {
        var editor = new TextEditor
        {
            Document = new TextDocument(text),
            IsReadOnly = true,
            ShowLineNumbers = false,
            FontFamily = new FontFamily("JuliaMono, Cascadia Code, SF Mono, Menlo, Consolas, DejaVu Sans Mono, monospace"),
            FontSize = 13,
            WordWrap = false,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
        };
        var options = new LeanRegistryOptions(ThemeName.DarkPlus);
        TextMate.Installation tm = editor.InstallTextMate(options);
        try
        {
            tm.SetGrammar(System.IO.Path.GetExtension(path) == ".lean" ? LeanRegistryOptions.LeanScope : options.ScopeForExtension(System.IO.Path.GetExtension(path)));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Without highlighting, the diff still reads.
        }
        if (tm.TryGetThemeColor("editor.background", out string? bg) && Color.TryParse(bg, out Color c))
        {
            editor.Background = new SolidColorBrush(c);
        }
        if (tm.TryGetThemeColor("editor.foreground", out string? fg) && Color.TryParse(fg, out Color f))
        {
            editor.Foreground = new SolidColorBrush(f);
        }
        editor.TextArea.LeftMargins.Insert(0, new NumberMargin(_rows, isLeft, editor));
        editor.TextArea.TextView.BackgroundRenderers.Add(new Tint(_rows, isLeft, TextDiff.Lines(Request?.Left ?? ""), TextDiff.Lines(Request?.Right ?? "")));
        return editor;
    }

    private void Sync(TextEditor from, TextEditor to)
    {
        if (_syncing)
        {
            return;
        }
        _syncing = true;
        Vector at = from.TextArea.TextView.ScrollOffset;
        to.ScrollToOffset(at.Y, at.X);
        _syncing = false;
    }

    /// <summary>Go to the next change (<paramref name="direction"/> 1) or the previous one (-1), wrapping around.</summary>
    public void Step(int direction)
    {
        if (_changes.Count == 0)
        {
            return;
        }
        _current = _current < 0 ? (direction > 0 ? 0 : _changes.Count - 1) : (_current + direction + _changes.Count) % _changes.Count;
        int row = _changes[_current];
        _left.ScrollTo(row + 1, 0);
        _right.ScrollTo(row + 1, 0);
        _left.TextArea.Caret.Line = row + 1;
        _where.Text = $"change {_current + 1} of {_changes.Count}";
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.F7)
        {
            Step(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            e.Handled = true;
        }
    }

    /// <summary>Each side's own line numbers, blank on the rows it has no line in.</summary>
    private sealed class NumberMargin(IReadOnlyList<DiffRow> rows, bool isLeft, TextEditor editor) : AbstractMargin
    {
        protected override Size MeasureOverride(Size availableSize) => new(48, 0);

        public override void Render(DrawingContext context)
        {
            TextView view = TextView;
            if (view is null || !view.VisualLinesValid)
            {
                return;
            }
            var face = new Typeface(editor.FontFamily);
            IBrush fg = new SolidColorBrush(Color.FromArgb(0x90, 0x85, 0x8C, 0x96));
            foreach (VisualLine vl in view.VisualLines)
            {
                int row = vl.FirstDocumentLine.LineNumber - 1;
                if (row >= rows.Count || (isLeft ? rows[row].Left : rows[row].Right) is not int n)
                {
                    continue;
                }
                var t = new FormattedText((n + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, editor.FontSize - 1, fg);
                context.DrawText(t, new Point(40 - t.Width, vl.VisualTop - view.ScrollOffset.Y + (vl.Height - t.Height) / 2));
            }
        }
    }

    /// <summary>The colours: removed and changed lines on the left, added and changed on the right, the changed part brighter.</summary>
    private sealed class Tint(IReadOnlyList<DiffRow> rows, bool isLeft, string[] a, string[] b) : IBackgroundRenderer
    {
        private static readonly IBrush Removed = new SolidColorBrush(Color.FromArgb(0x38, 0xF1, 0x4C, 0x4C));
        private static readonly IBrush RemovedPart = new SolidColorBrush(Color.FromArgb(0x70, 0xF1, 0x4C, 0x4C));
        private static readonly IBrush Added = new SolidColorBrush(Color.FromArgb(0x32, 0x3F, 0xB9, 0x50));
        private static readonly IBrush AddedPart = new SolidColorBrush(Color.FromArgb(0x6A, 0x3F, 0xB9, 0x50));
        private static readonly IBrush Blank = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80));

        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            foreach (VisualLine vl in textView.VisualLines)
            {
                int row = vl.FirstDocumentLine.LineNumber - 1;
                if (row >= rows.Count)
                {
                    continue;
                }
                DiffRow r = rows[row];
                double top = vl.VisualTop - textView.ScrollOffset.Y;
                var line = new Rect(0, top, textView.Bounds.Width, vl.Height);
                bool here = isLeft ? r.Left is not null : r.Right is not null;
                if (r.Kind == DiffKind.Same)
                {
                    continue;
                }
                if (!here)
                {
                    drawingContext.FillRectangle(Blank, line);
                    continue;
                }
                drawingContext.FillRectangle(isLeft ? Removed : Added, line);
                if (r.Kind == DiffKind.Changed && r.Left is int li && r.Right is int ri && li < a.Length && ri < b.Length)
                {
                    // The part that changed: between the longest common start and the longest common end.
                    string x = a[li], y = b[ri];
                    int start = 0;
                    while (start < x.Length && start < y.Length && x[start] == y[start])
                    {
                        start++;
                    }
                    int end = 0;
                    while (end < x.Length - start && end < y.Length - start && x[^(end + 1)] == y[^(end + 1)])
                    {
                        end++;
                    }
                    string s = isLeft ? x : y;
                    int from = start, to = s.Length - end;
                    if (to > from)
                    {
                        double x0 = vl.GetVisualPosition(from, VisualYPosition.LineTop).X - textView.ScrollOffset.X;
                        double x1 = vl.GetVisualPosition(Math.Min(to, vl.VisualLength), VisualYPosition.LineTop).X - textView.ScrollOffset.X;
                        drawingContext.FillRectangle(isLeft ? RemovedPart : AddedPart, new Rect(x0, top, Math.Max(2, x1 - x0), vl.Height));
                    }
                }
            }
        }
    }
}

/// <summary>A window holding a <see cref="DiffView"/>.</summary>
public sealed class DiffWindow : Window
{
    /// <summary>Show <paramref name="request"/>.</summary>
    public DiffWindow(DiffRequest request)
    {
        Title = request.Title;
        Width = 1300;
        Height = 820;
        MinWidth = 640;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Bind(BackgroundProperty, this.GetResourceObservable("PanelBackground"));
        View = new DiffView(request);
        Content = View;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };
    }

    /// <summary>The diff shown.</summary>
    public DiffView View { get; }
}
