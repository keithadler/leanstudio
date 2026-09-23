using System.Globalization;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using LeanStudio.Core.Proofs;

namespace LeanStudio.App.Editor;

/// <summary>
/// The performance heat map in the editor: after a profile, each declaration that took measurable time gets its
/// time at the end of its first line, and the slow ones a tinted line, warmer the slower, so the expensive parts of
/// a file stand out while scrolling through it.
/// </summary>
public sealed class TimingRenderer(bool labels) : IBackgroundRenderer
{
    private Dictionary<int, DeclarationTiming> _byLine = [];

    private static readonly IBrush[] Text =
    [
        new SolidColorBrush(Color.FromArgb(0xB0, 0x8F, 0xA8, 0xB8)),
        new SolidColorBrush(Color.FromArgb(0xE0, 0xE5, 0x9E, 0x2C)),
        new SolidColorBrush(Color.FromArgb(0xF0, 0xF1, 0x4C, 0x4C)),
    ];

    private static readonly IBrush?[] Band =
    [
        null,
        new SolidColorBrush(Color.FromArgb(0x1C, 0xE5, 0x9E, 0x2C)),
        new SolidColorBrush(Color.FromArgb(0x26, 0xF1, 0x4C, 0x4C)),
    ];

    private static readonly IBrush Plate = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x2D, 0x33));

    /// <summary>The tint goes under the text; the labels over it, so a label pinned over a long line stays readable.</summary>
    public KnownLayer Layer => labels ? KnownLayer.Caret : KnownLayer.Background;

    public void Update(IReadOnlyList<DeclarationTiming> timings) => _byLine = timings.GroupBy(t => t.Line).ToDictionary(g => g.Key, g => g.First());

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_byLine.Count == 0 || !textView.VisualLinesValid || textView.Document is null)
        {
            return;
        }
        var typeface = new Typeface(textView.GetValue(TextElement.FontFamilyProperty), FontStyle.Italic);
        double size = textView.GetValue(TextElement.FontSizeProperty) - 1;
        foreach (VisualLine vl in textView.VisualLines)
        {
            int line = vl.FirstDocumentLine.LineNumber - 1;
            if (!_byLine.TryGetValue(line, out DeclarationTiming? t))
            {
                continue;
            }
            double top = vl.VisualTop - textView.ScrollOffset.Y;
            if (!labels)
            {
                if (Band[t.Heat] is IBrush band)
                {
                    drawingContext.FillRectangle(band, new Rect(0, top, textView.Bounds.Width, vl.Height));
                }
                continue;
            }
            Point end = vl.GetVisualPosition(vl.VisualLengthWithEndOfLineMarker, VisualYPosition.TextTop);
            string label = "⏱ " + t.Time + (t.HotSpot is string h && t.Heat > 0 ? $"  · slowest: {(h.Length > 40 ? h[..40] + "…" : h)}" : "");
            var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Text[t.Heat]);
            double x = end.X - textView.ScrollOffset.X + 24, y = end.Y - textView.ScrollOffset.Y;
            if (x + ft.Width > textView.Bounds.Width - 8)
            {
                // A long first line: pin the label to the right edge, on a plate so it reads over the code.
                x = Math.Max(0, textView.Bounds.Width - ft.Width - 12);
                drawingContext.FillRectangle(Plate, new Rect(x - 8, top, textView.Bounds.Width - x + 8, vl.Height), 3);
            }
            drawingContext.DrawText(ft, new Point(x, y));
        }
    }
}
