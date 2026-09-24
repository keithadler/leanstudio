using System.Globalization;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using LeanStudio.Lsp;

namespace LeanStudio.App.Editor;

/// <summary>
/// The end of each proof, marked at the end of its last line: a quiet ✔ where Lean says the goals are accomplished,
/// and "⊢ goals left" where it reports unsolved goals, so an unfinished proof stands out while scrolling.
/// </summary>
public sealed class ProofMarksRenderer : IBackgroundRenderer
{
    private static readonly IBrush Done = new SolidColorBrush(Color.FromArgb(0xB0, 0x3F, 0xB9, 0x50));
    private static readonly IBrush Left = new SolidColorBrush(Color.FromRgb(0xE5, 0xB9, 0x3A));
    private IReadOnlyList<ProofMark> _marks = [];

    /// <summary>Whether marks are drawn (Preferences); the caller redraws the text view after changing it.</summary>
    public bool Enabled { get; set; } = true;

    /// <inheritdoc/>
    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>Replace the marks (the file's current ones). Does not redraw.</summary>
    public void Update(IReadOnlyList<ProofMark> marks) => _marks = marks;

    /// <inheritdoc/>
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!Enabled || _marks.Count == 0 || !textView.VisualLinesValid || textView.Document is null)
        {
            return;
        }
        var byLine = _marks.ToDictionary(m => m.Line, m => m.Done);
        var typeface = new Typeface(textView.GetValue(TextElement.FontFamilyProperty));
        double size = textView.GetValue(TextElement.FontSizeProperty) - 1;
        foreach (VisualLine vl in textView.VisualLines)
        {
            if (!byLine.TryGetValue(vl.LastDocumentLine.LineNumber - 1, out bool done))
            {
                continue;
            }
            Point end = vl.GetVisualPosition(vl.VisualLengthWithEndOfLineMarker, VisualYPosition.TextTop);
            var ft = new FormattedText(done ? "  ✔" : "   ⊢ goals left", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, done ? Done : Left);
            drawingContext.DrawText(ft, new Point(end.X - textView.ScrollOffset.X + 6, end.Y - textView.ScrollOffset.Y));
        }
    }
}
