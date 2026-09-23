using System.Globalization;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using LeanStudio.Core.Verification;
using LeanStudio.Lsp;

namespace LeanStudio.App.Editor;

/// <summary>
/// The gutter strip next to the line numbers. An amber bar marks lines Lean is still elaborating (red where it
/// gave up), and a badge marks each declaration with Tenet's verdict from the last verification:
/// ✓ verified, ◐ rests on sorry or a project axiom, ✗ rejected by Tenet's kernel.
/// </summary>
public sealed class StatusMargin : AbstractMargin
{
    private IReadOnlyList<LeanFileProgressRange> _processing = [];
    private IReadOnlyDictionary<int, DeclarationVerdict> _verdicts = new Dictionary<int, DeclarationVerdict>();

    private static readonly IBrush ProcessingBrush = new SolidColorBrush(Color.FromArgb(0xD0, 0xE5, 0x9E, 0x2C));
    private static readonly IBrush FatalBrush = new SolidColorBrush(Color.FromArgb(0xD0, 0xE0, 0x40, 0x40));
    public static readonly IBrush VerifiedBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    public static readonly IBrush ConditionalBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x9E, 0x2C));
    public static readonly IBrush RejectedBrush = new SolidColorBrush(Color.FromRgb(0xF1, 0x4C, 0x4C));

    private IReadOnlyList<Core.Git.LineChange> _changes = [];
    private HashSet<int> _bulbs = [];
    private static readonly IBrush BulbBrush = new SolidColorBrush(Color.FromRgb(0xF2, 0xC9, 0x4C));
    private static readonly IBrush BulbBase = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A));

    /// <summary>Lines (1-based) where Lean offers a fix: a lightbulb is drawn there.</summary>
    public void SetBulbs(IEnumerable<int> oneBasedLines)
    {
        _bulbs = [.. oneBasedLines];
        InvalidateVisual();
    }

    public bool HasBulb(int oneBasedLine) => _bulbs.Contains(oneBasedLine);
    private static readonly IBrush AddedBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly IBrush ModifiedBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x8E, 0xEA));
    private static readonly IBrush DeletedBrush = new SolidColorBrush(Color.FromRgb(0xF1, 0x4C, 0x4C));

    public void Update(IReadOnlyList<LeanFileProgressRange> processing, IReadOnlyDictionary<int, DeclarationVerdict> verdicts, IReadOnlyList<Core.Git.LineChange> changes)
    {
        _processing = processing;
        _verdicts = verdicts;
        _changes = changes;
        InvalidateVisual();
    }

    public DeclarationVerdict? VerdictAtLine(int oneBasedLine) => _verdicts.TryGetValue(oneBasedLine, out DeclarationVerdict? v) ? v : null;

    protected override Size MeasureOverride(Size availableSize) => new(20, 0);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null)
        {
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;
        }
        base.OnTextViewChanged(oldTextView, newTextView);
        if (newTextView is not null)
        {
            newTextView.VisualLinesChanged += OnVisualLinesChanged;
        }
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        TextView? view = TextView;
        if (view is null || !view.VisualLinesValid)
        {
            return;
        }
        double width = Bounds.Width;
        foreach (VisualLine vl in view.VisualLines)
        {
            int line = vl.FirstDocumentLine.LineNumber; // 1-based
            double top = vl.VisualTop - view.VerticalOffset;
            double height = vl.Height;

            foreach (LeanFileProgressRange p in _processing)
            {
                if (p.Range.Start.Line + 1 <= line && line <= p.Range.End.Line + 1)
                {
                    context.FillRectangle(p.Kind == LeanFileProgressKind.FatalError ? FatalBrush : ProcessingBrush, new Rect(width - 4, top, 4, height));
                    break;
                }
            }

            // Lines changed since the last commit: a thin bar at the left edge, as in most editors.
            foreach (Core.Git.LineChange c in _changes)
            {
                if (c.Kind == Core.Git.LineChangeKind.Deleted && c.StartLine == line)
                {
                    context.FillRectangle(DeletedBrush, new Rect(0, top + height - 2, 6, 3));
                }
                else if (c.Kind != Core.Git.LineChangeKind.Deleted && c.StartLine <= line && line < c.StartLine + c.LineCount)
                {
                    context.FillRectangle(c.Kind == Core.Git.LineChangeKind.Added ? AddedBrush : ModifiedBrush, new Rect(0, top, 2.5, height));
                }
            }

            if (_bulbs.Contains(line))
            {
                double cx = 10, cy = top + height / 2 - 1.5, r = Math.Min(4.5, height / 3.2);
                context.DrawEllipse(BulbBrush, null, new Point(cx, cy), r, r);
                context.FillRectangle(BulbBase, new Rect(cx - r / 2, cy + r - 0.5, r, 2.5));
                continue;
            }

            if (_verdicts.TryGetValue(line, out DeclarationVerdict? v))
            {
                (string glyph, IBrush brush) = v.Status switch
                {
                    VerificationStatus.Verified => ("✓", VerifiedBrush),
                    VerificationStatus.RestsOnAssumption => ("◐", ConditionalBrush),
                    _ => ("✗", RejectedBrush),
                };
                var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, Math.Min(13, height * 0.8), brush);
                context.DrawText(text, new Point(4, top + (height - text.Height) / 2));
            }
        }
    }
}
