using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using LeanStudio.Core.Editing;

namespace LeanStudio.App.Editor;

/// <summary>
/// Shows the math and the emphasis in Lean's comments typeset, in place: <c>$\sum_{k=1}^n k$</c> becomes a drawn sum,
/// <c>**Gauss**</c> becomes bold Gauss. The span under the caret shows its source again so it can be edited, and a
/// span is rendered only when it opens and closes on one line. The document is never changed: this only draws.
/// </summary>
public sealed class DocMarkupGenerator : VisualLineElementGenerator
{
    private TextDocument? _doc;
    private DocSpan[]? _spans;
    private int _caret = -1;

    /// <summary>Whether comments are typeset at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The colour of comment text, which the typeset math and emphasis take so they read as part of the comment.</summary>
    public IBrush Foreground { get; set; } = Brushes.Gray;

    /// <summary>The editor's font.</summary>
    public FontFamily FontFamily { get; set; } = FontFamily.Default;

    /// <summary>The editor's font size.</summary>
    public double FontSize { get; set; } = 14;

    /// <summary>The spans found in the document as it is now.</summary>
    public IReadOnlyList<DocSpan> Spans(TextDocument doc) => SpansOf(doc);

    private DocSpan[] SpansOf(TextDocument doc)
    {
        if (!ReferenceEquals(doc, _doc))
        {
            if (_doc is not null)
            {
                _doc.Changed -= OnChanged;
            }
            _doc = doc;
            doc.Changed += OnChanged;
            _spans = null;
        }
        return _spans ??= [.. DocMarkup.Find(doc.Text)];
    }

    private void OnChanged(object? sender, DocumentChangeEventArgs e) => _spans = null;

    /// <summary>
    /// Tell the generator where the caret is. Returns the spans whose state changed (shown now, or hidden now) so the
    /// caller can redraw just them; empty when nothing changed.
    /// </summary>
    public IReadOnlyList<DocSpan> MoveCaret(TextDocument doc, int caret)
    {
        if (!Enabled)
        {
            _caret = caret;
            return [];
        }
        DocSpan[] spans = SpansOf(doc);
        DocSpan? before = At(spans, _caret), after = At(spans, caret);
        _caret = caret;
        if (before == after)
        {
            return [];
        }
        return [.. new[] { before, after }.Where(s => s is not null).Select(s => s!.Value)];
    }

    /// <summary>The index of the first span starting at or after <paramref name="offset"/> (the length when none does).</summary>
    private static int LowerBound(DocSpan[] spans, int offset)
    {
        int lo = 0, hi = spans.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (spans[mid].Start < offset)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }
        return lo;
    }

    private static DocSpan? At(DocSpan[] spans, int offset)
    {
        int i = LowerBound(spans, offset + 1) - 1; // the last span starting at or before offset
        return i >= 0 && offset <= spans[i].End ? spans[i] : null;
    }

    /// <inheritdoc/>
    public override int GetFirstInterestedOffset(int startOffset)
    {
        if (!Enabled)
        {
            return -1;
        }
        int end = CurrentContext.VisualLine.LastDocumentLine.EndOffset;
        DocSpan[] spans = SpansOf(CurrentContext.Document);
        for (int i = LowerBound(spans, startOffset); i < spans.Length && spans[i].Start <= end; i++)
        {
            if (!(_caret >= spans[i].Start && _caret <= spans[i].End))
            {
                return spans[i].Start;
            }
        }
        return -1;
    }

    /// <inheritdoc/>
    public override VisualLineElement? ConstructElement(int offset)
    {
        DocSpan[] all = SpansOf(CurrentContext.Document);
        int index = LowerBound(all, offset);
        if (index < all.Length && all[index].Start == offset)
        {
            DocSpan s = all[index];
            string content = CurrentContext.Document.GetText(s.ContentStart, s.ContentLength);
            switch (s.Kind)
            {
                case DocSpanKind.InlineMath:
                case DocSpanKind.DisplayMath:
                {
                    bool display = s.Kind == DocSpanKind.DisplayMath;
                    var view = new MathView(TexMath.Parse(content), display ? FontSize * 1.2 : FontSize * 1.05, Foreground, display);
                    TextBlock.SetBaselineOffset(view, view.Ascent);
                    ToolTip.SetTip(view, content.Trim());
                    return new InlineObjectElement(s.Length, view);
                }
                default:
                {
                    bool bold = s.Kind == DocSpanKind.Bold;
                    var text = new TextBlock
                    {
                        Text = content,
                        FontFamily = FontFamily,
                        FontSize = FontSize,
                        FontWeight = bold ? FontWeight.Bold : FontWeight.Normal,
                        FontStyle = bold ? FontStyle.Normal : FontStyle.Italic,
                        Foreground = Foreground,
                    };
                    return new InlineObjectElement(s.Length, text);
                }
            }
        }
        return null;
    }
}
