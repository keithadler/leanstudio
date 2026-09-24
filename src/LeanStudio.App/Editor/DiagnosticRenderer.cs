using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using LeanStudio.Lsp;

namespace LeanStudio.App.Editor;

/// <summary>Squiggly underlines under Lean's errors, warnings and information messages.</summary>
public sealed class DiagnosticRenderer : IBackgroundRenderer
{
    private IReadOnlyList<Diagnostic> _diagnostics = [];

    /// <inheritdoc/>
    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>The color of errors, also used for their markers elsewhere.</summary>
    public static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xF1, 0x4C, 0x4C));
    /// <summary>The color of warnings.</summary>
    public static readonly IBrush WarningBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00));
    /// <summary>The color of information messages and hints, which are underlined with dots rather than a squiggle.</summary>
    public static readonly IBrush InfoBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x9C, 0xFF));

    /// <summary>Replace the diagnostics underlined (the file's current ones, with LSP positions). Does not redraw.</summary>
    public void Update(IReadOnlyList<Diagnostic> diagnostics) => _diagnostics = diagnostics;

    /// <inheritdoc/>
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        TextDocument? doc = textView.Document;
        if (doc is null || _diagnostics.Count == 0 || !textView.VisualLinesValid)
        {
            return;
        }
        int firstLine = textView.VisualLines.First().FirstDocumentLine.LineNumber;
        int lastLine = textView.VisualLines.Last().LastDocumentLine.LineNumber;
        // Draw the least severe first so errors end up on top.
        foreach (Diagnostic d in _diagnostics.OrderByDescending(d => d.Severity))
        {
            if (d.Range.End.Line + 1 < firstLine || d.Range.Start.Line + 1 > lastLine)
            {
                continue;
            }
            int start = Offset(doc, d.Range.Start);
            int end = Offset(doc, d.Range.End);
            if (end <= start)
            {
                // Lean sometimes reports an empty range (e.g. end of input); underline one character.
                end = Math.Min(doc.TextLength, start + 1);
                if (end <= start && start > 0)
                {
                    start--;
                }
            }
            IBrush brush = d.Severity switch
            {
                DiagnosticSeverity.Error => ErrorBrush,
                DiagnosticSeverity.Warning => WarningBrush,
                _ => InfoBrush,
            };
            var segment = new TextSegment { StartOffset = start, EndOffset = end };
            foreach (Rect r in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
            {
                if (d.Severity >= DiagnosticSeverity.Information)
                {
                    DrawDots(drawingContext, r, brush);
                }
                else
                {
                    DrawSquiggle(drawingContext, r, brush);
                }
            }
        }
    }

    private static void DrawSquiggle(DrawingContext dc, Rect r, IBrush brush)
    {
        const double h = 2.5, w = 3;
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            double y = r.Bottom - 1;
            ctx.BeginFigure(new Point(r.Left, y), false);
            bool up = true;
            for (double x = r.Left + w; x <= r.Right + w; x += w)
            {
                ctx.LineTo(new Point(Math.Min(x, r.Right), up ? y - h : y));
                up = !up;
            }
            ctx.EndFigure(false);
        }
        dc.DrawGeometry(null, new Pen(brush, 1.1), geometry);
    }

    private static void DrawDots(DrawingContext dc, Rect r, IBrush brush)
    {
        var pen = new Pen(brush, 1.2, new DashStyle([1, 2], 0));
        dc.DrawLine(pen, new Point(r.Left, r.Bottom - 1), new Point(r.Right, r.Bottom - 1));
    }

    /// <summary>An LSP position (UTF-16 column) as a document offset, clamped to the document.</summary>
    public static int Offset(TextDocument doc, Position p)
    {
        if (p.Line >= doc.LineCount)
        {
            return doc.TextLength;
        }
        DocumentLine line = doc.GetLineByNumber(p.Line + 1);
        return line.Offset + Math.Min(p.Character, line.Length);
    }
}
