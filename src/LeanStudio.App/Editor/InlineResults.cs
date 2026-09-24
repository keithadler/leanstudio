using System.Globalization;
using Avalonia;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using LeanStudio.Lsp;

namespace LeanStudio.App.Editor;

/// <summary>
/// Shows what <c>#eval</c>, <c>#check</c>, <c>#print</c> and <c>#reduce</c> produce at the end of their own line,
/// dimmed, the way a notebook or a live-coding editor does: a programmer sees results where the code is, without
/// looking across to another panel. Only the first line of the result is shown; the rest is in the hover.
/// </summary>
public sealed class InlineResults : IBackgroundRenderer
{
    private IReadOnlyList<Diagnostic> _diagnostics = [];
    private static readonly IBrush Brush = new SolidColorBrush(Color.FromArgb(0xA0, 0x8F, 0xB8, 0x8F));

    /// <summary>Whether results are drawn; the caller redraws the text view after changing it.</summary>
    public bool Enabled { get; set; } = true;

    /// <inheritdoc/>
    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>
    /// Replace the diagnostics results are taken from (the file's current ones). Only information messages on lines
    /// starting with <c>#</c> are shown. Does not redraw.
    /// </summary>
    public void Update(IReadOnlyList<Diagnostic> diagnostics) => _diagnostics = diagnostics;

    /// <inheritdoc/>
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!Enabled || _diagnostics.Count == 0 || !textView.VisualLinesValid || textView.Document is null)
        {
            return;
        }
        var byLine = _diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Information && !d.Message.StartsWith("Try this", StringComparison.Ordinal))
            .GroupBy(d => d.Range.Start.Line)
            .ToDictionary(g => g.Key, g => g.First());
        if (byLine.Count == 0)
        {
            return;
        }
        var typeface = new Typeface(textView.GetValue(TextElement.FontFamilyProperty), FontStyle.Italic);
        double size = textView.GetValue(TextElement.FontSizeProperty) - 1;
        foreach (VisualLine vl in textView.VisualLines)
        {
            int line = vl.FirstDocumentLine.LineNumber - 1;
            if (!byLine.TryGetValue(line, out Diagnostic? d))
            {
                continue;
            }
            string text = vl.FirstDocumentLine.Length > 0 ? textView.Document.GetText(vl.FirstDocumentLine) : "";
            if (!text.TrimStart().StartsWith('#'))
            {
                continue; // results of commands only; other messages stay as underlines
            }
            string first = d.Message.Trim().Split('\n')[0];
            if (first.Length > 120)
            {
                first = first[..120] + " …";
            }
            if (d.Message.Trim().Contains('\n', StringComparison.Ordinal))
            {
                first += "  …";
            }
            Point end = vl.GetVisualPosition(vl.VisualLengthWithEndOfLineMarker, VisualYPosition.TextTop);
            var ft = new FormattedText("   ⟶ " + first, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, Brush);
            drawingContext.DrawText(ft, new Point(end.X - textView.ScrollOffset.X + 8, end.Y - textView.ScrollOffset.Y));
        }
    }
}
