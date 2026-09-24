using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using LeanStudio.Lsp;

namespace LeanStudio.App.Editor;

/// <summary>
/// Lean's semantic highlighting over the grammar's: the grammar cannot tell a bound variable from a constant, or a
/// field from a function, and Lean can. Tokens are kept as offsets and moved with each edit, so colours stay put
/// while Lean re-checks, and are replaced when it has.
/// </summary>
public sealed class SemanticColorizer : DocumentColorizingTransformer
{
    private List<(int Offset, int Length, string Type, bool Deprecated)> _tokens = [];
    private bool _dark = true;

    /// <summary>Whether to draw at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Use colours for a dark or a light background.</summary>
    public void SetDark(bool dark) => _dark = dark;

    /// <summary>Take Lean's tokens for the document as it is now.</summary>
    public void Update(TextDocument doc, IReadOnlyList<SemanticToken> tokens)
    {
        var list = new List<(int, int, string, bool)>(tokens.Count);
        foreach (SemanticToken t in tokens)
        {
            if (t.Type is not ("variable" or "parameter" or "property") && !t.Modifiers.Contains("deprecated"))
            {
                continue; // keywords and the rest are the grammar's
            }
            if (t.Line >= doc.LineCount)
            {
                continue;
            }
            DocumentLine line = doc.GetLineByNumber(t.Line + 1);
            if (t.Start + t.Length > line.Length)
            {
                continue;
            }
            list.Add((line.Offset + t.Start, t.Length, t.Type, t.Modifiers.Contains("deprecated")));
        }
        _tokens = list;
    }

    /// <summary>Forget every token (a different document).</summary>
    public void Clear() => _tokens = [];

    /// <summary>How many tokens are coloured.</summary>
    public int Count => _tokens.Count;

    /// <summary>Move tokens after an edit, and drop the ones it touched.</summary>
    public void Shift(DocumentChangeEventArgs e)
    {
        if (_tokens.Count == 0)
        {
            return;
        }
        int delta = e.InsertionLength - e.RemovalLength;
        var next = new List<(int, int, string, bool)>(_tokens.Count);
        foreach (var t in _tokens)
        {
            if (t.Offset + t.Length <= e.Offset)
            {
                next.Add(t);
            }
            else if (t.Offset >= e.Offset + e.RemovalLength)
            {
                next.Add((t.Offset + delta, t.Length, t.Type, t.Deprecated));
            }
        }
        _tokens = next;
    }

    private IBrush Brush(string type) => (type, _dark) switch
    {
        ("property", true) => VariablesDark.Property,
        ("property", false) => VariablesLight.Property,
        (_, true) => VariablesDark.Variable,
        _ => VariablesLight.Variable,
    };

    private static readonly (IBrush Variable, IBrush Property) VariablesDark = (new SolidColorBrush(Color.FromRgb(0x9C, 0xDC, 0xFE)), new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)));
    private static readonly (IBrush Variable, IBrush Property) VariablesLight = (new SolidColorBrush(Color.FromRgb(0x00, 0x10, 0x80)), new SolidColorBrush(Color.FromRgb(0x26, 0x7F, 0x99)));

    /// <inheritdoc />
    protected override void ColorizeLine(DocumentLine line)
    {
        if (!Enabled || _tokens.Count == 0)
        {
            return;
        }
        int lo = 0, hi = _tokens.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_tokens[mid].Offset < line.Offset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        for (int i = lo; i < _tokens.Count && _tokens[i].Offset < line.EndOffset; i++)
        {
            var t = _tokens[i];
            int end = Math.Min(t.Offset + t.Length, line.EndOffset);
            if (end <= t.Offset)
            {
                continue;
            }
            IBrush brush = Brush(t.Type);
            bool deprecated = t.Deprecated;
            bool color = t.Type is "variable" or "parameter" or "property";
            ChangeLinePart(t.Offset, end, el =>
            {
                if (color)
                {
                    el.TextRunProperties.SetForegroundBrush(brush);
                }
                if (deprecated)
                {
                    el.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough);
                }
            });
        }
    }
}

/// <summary>Every occurrence of the symbol under the cursor, boxed, as Lean finds them (not a text search).</summary>
public sealed class OccurrenceHighlighter : IBackgroundRenderer
{
    private IReadOnlyList<(int Offset, int Length)> _ranges = [];
    private static readonly IBrush Fill = new SolidColorBrush(Color.FromArgb(0x38, 0x80, 0x9C, 0xC8));
    private static readonly IPen Outline = new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x9C, 0xC8)), 1);

    /// <inheritdoc />
    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>Show these occurrences (ranges converted against <paramref name="doc"/>).</summary>
    public void Update(TextDocument doc, IReadOnlyList<Lsp.Range> ranges)
    {
        var list = new List<(int, int)>();
        foreach (Lsp.Range r in ranges)
        {
            if (r.Start.Line >= doc.LineCount || r.End.Line >= doc.LineCount)
            {
                continue;
            }
            int s = doc.GetLineByNumber(r.Start.Line + 1).Offset + r.Start.Character;
            int e = doc.GetLineByNumber(r.End.Line + 1).Offset + r.End.Character;
            if (e > s && e <= doc.TextLength)
            {
                list.Add((s, e - s));
            }
        }
        // One occurrence is just the symbol itself: not worth a box.
        _ranges = list.Count > 1 ? list : [];
    }

    /// <summary>Show nothing.</summary>
    public void Clear() => _ranges = [];

    /// <summary>Whether anything is shown.</summary>
    public bool HasAny => _ranges.Count > 0;

    /// <inheritdoc />
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_ranges.Count == 0 || !textView.VisualLinesValid)
        {
            return;
        }
        foreach ((int offset, int length) in _ranges)
        {
            var builder = new BackgroundGeometryBuilder { CornerRadius = 2 };
            builder.AddSegment(textView, new TextSegment { StartOffset = offset, Length = length });
            if (builder.CreateGeometry() is Geometry g)
            {
                drawingContext.DrawGeometry(Fill, Outline, g);
            }
        }
    }
}

/// <summary>
/// Lean's inlay hints, drawn in the text where they belong, dimmed and boxed so they are never mistaken for code:
/// for example the <c>{α : Type u_1}</c> that Lean binds automatically for an unknown type name.
/// </summary>
public sealed class InlayHintGenerator : VisualLineElementGenerator
{
    private List<(int Offset, string Label)> _hints = [];

    /// <summary>Whether to show hints at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The editor's font, for the hints.</summary>
    public FontFamily FontFamily { get; set; } = FontFamily.Default;

    /// <summary>The editor's font size; hints are a little smaller.</summary>
    public double FontSize { get; set; } = 14;

    /// <summary>Take Lean's hints for the document as it is now.</summary>
    public void Update(TextDocument doc, IReadOnlyList<InlayHint> hints)
    {
        var list = new List<(int, string)>();
        foreach (InlayHint h in hints)
        {
            if (h.Position.Line >= doc.LineCount)
            {
                continue;
            }
            DocumentLine line = doc.GetLineByNumber(h.Position.Line + 1);
            if (h.Position.Character > line.Length)
            {
                continue;
            }
            list.Add((line.Offset + h.Position.Character, (h.PaddingLeft ? " " : "") + h.Label.Trim() + (h.PaddingRight ? " " : "")));
        }
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        _hints = list;
    }

    /// <summary>Show no hints.</summary>
    public void Clear() => _hints = [];

    /// <summary>The hints shown, in order.</summary>
    public IReadOnlyList<string> Labels => _hints.Select(h => h.Label.Trim()).ToList();

    /// <summary>Move hints after an edit, and drop the ones inside it.</summary>
    public void Shift(DocumentChangeEventArgs e)
    {
        int delta = e.InsertionLength - e.RemovalLength;
        _hints = _hints
            .Where(h => h.Offset <= e.Offset || h.Offset >= e.Offset + e.RemovalLength)
            .Select(h => h.Offset > e.Offset ? (h.Offset + delta, h.Label) : h)
            .ToList();
    }

    /// <inheritdoc />
    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (!Enabled)
        {
            return -1;
        }
        int end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
        foreach ((int offset, _) in _hints)
        {
            if (offset >= startOffset && offset <= end)
            {
                return offset;
            }
        }
        return -1;
    }

    /// <inheritdoc />
    public override VisualLineElement? ConstructElement(int offset)
    {
        string? label = null;
        foreach ((int o, string l) in _hints)
        {
            if (o == offset)
            {
                label = label is null ? l : label + l;
            }
        }
        if (label is null)
        {
            return null;
        }
        var box = new Border
        {
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(3, 0),
            Margin = new Thickness(1, 0),
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0x80, 0x80, 0x80)),
            Child = new TextBlock
            {
                Text = label.Trim(),
                FontFamily = FontFamily,
                FontSize = FontSize - 2,
                Opacity = 0.7,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            },
        };
        ToolTip.SetTip(box, "Lean adds this for you: an inlay hint, not part of the file");
        return new InlineObjectElement(0, box);
    }
}
