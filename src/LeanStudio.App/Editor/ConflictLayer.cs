using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using LeanStudio.Core.Editing;

namespace LeanStudio.App.Editor;

/// <summary>
/// Merge conflicts in the editor: this branch's side tinted green and the other side blue (as a background renderer
/// of the text view), and on each <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> line the choices git leaves to a person:
/// <b>Keep mine</b>, <b>Take theirs</b>, <b>Keep both</b> (as an overlay over the editor, with no background, so
/// clicks elsewhere reach the text). Each settles the conflict as one edit, which undo takes back.
/// </summary>
public sealed class ConflictLayer : Canvas, IBackgroundRenderer
{
    private readonly TextEditor _editor;
    private IReadOnlyList<ConflictBlock> _blocks = [];

    private static readonly IBrush MineBrush = new SolidColorBrush(Color.FromArgb(0x26, 0x4C, 0xAF, 0x50));
    private static readonly IBrush TheirsBrush = new SolidColorBrush(Color.FromArgb(0x26, 0x42, 0x8B, 0xF4));
    private static readonly IBrush MarkerBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80));

    /// <summary>Show the conflicts of <paramref name="editor"/>'s text; add it as a background renderer, and over the editor.</summary>
    public ConflictLayer(TextEditor editor)
    {
        _editor = editor;
        ClipToBounds = true;
    }

    /// <summary>The conflicts in the text, in order.</summary>
    public IReadOnlyList<ConflictBlock> Blocks => _blocks;

    /// <summary>A conflict was settled (the document has already been edited).</summary>
    public event Action<ConflictChoice>? Resolved;

    /// <summary>The tint goes under the text.</summary>
    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>Find the conflicts again; call when the text changes.</summary>
    public void Update()
    {
        TextDocument? doc = _editor.Document;
        // Cheap before the full search: most files have no markers at all.
        _blocks = doc is null || doc.IndexOf("<<<<<<<", 0, doc.TextLength, StringComparison.Ordinal) < 0 ? [] : MergeConflicts.Find(doc.Text);
        Place();
    }

    /// <summary>Settle conflict <paramref name="index"/> as <paramref name="choice"/>, as one undoable edit.</summary>
    public void Resolve(int index, ConflictChoice choice)
    {
        if (index < 0 || index >= _blocks.Count || _editor.Document is not TextDocument doc)
        {
            return;
        }
        ConflictBlock b = _blocks[index];
        string settled = MergeConflicts.Resolve(doc.Text, b, choice);
        // Replace only the block's lines, so the rest of the document (and its undo history) is untouched.
        DocumentLine first = doc.GetLineByNumber(b.Start + 1), last = doc.GetLineByNumber(b.End + 1);
        int keptLines = choice switch
        {
            ConflictChoice.Mine => b.Mine.To - b.Mine.From,
            ConflictChoice.Theirs => b.Theirs.To - b.Theirs.From,
            _ => b.Mine.To - b.Mine.From + b.Theirs.To - b.Theirs.From,
        };
        string[] lines = settled.Split('\n');
        string replacement = string.Join('\n', lines.Skip(b.Start).Take(keptLines));
        int end = last.EndOffset;
        // Take the line break after the block too when nothing is kept, so no blank line is left.
        if (keptLines == 0 && last.NextLine is not null)
        {
            end = last.NextLine.Offset;
        }
        doc.Replace(first.Offset, end - first.Offset, replacement);
        Resolved?.Invoke(choice);
    }

    /// <inheritdoc/>
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_blocks.Count == 0 || !textView.VisualLinesValid)
        {
            return;
        }
        foreach (VisualLine vl in textView.VisualLines)
        {
            int line = vl.FirstDocumentLine.LineNumber - 1;
            IBrush? brush = null;
            foreach (ConflictBlock b in _blocks)
            {
                if (line == b.Start || line == b.Separator || line == b.End || line == b.Base)
                {
                    brush = MarkerBrush;
                }
                else if (line >= b.Mine.From && line < b.Mine.To)
                {
                    brush = MineBrush;
                }
                else if (line >= b.Theirs.From && line < b.Theirs.To)
                {
                    brush = TheirsBrush;
                }
            }
            if (brush is not null)
            {
                drawingContext.FillRectangle(brush, new Rect(0, vl.VisualTop - textView.ScrollOffset.Y, textView.Bounds.Width, vl.Height));
            }
        }
    }

    /// <summary>
    /// Put the choices on each visible <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> line, after its text. Call when the lines shown
    /// change (a scroll, an edit).
    /// </summary>
    public void Place()
    {
        Children.Clear();
        TextView view = _editor.TextArea.TextView;
        if (_blocks.Count == 0 || !view.VisualLinesValid || _editor.Document is null)
        {
            return;
        }
        for (int i = 0; i < _blocks.Count; i++)
        {
            ConflictBlock b = _blocks[i];
            if (b.Start >= _editor.Document.LineCount || view.GetVisualLine(b.Start + 1) is not VisualLine vl)
            {
                continue;
            }
            Point end = vl.GetVisualPosition(vl.VisualLengthWithEndOfLineMarker, VisualYPosition.LineTop);
            // From the text view's coordinates to this overlay's.
            if (view.TranslatePoint(new Point(end.X - view.ScrollOffset.X + 16, end.Y - view.ScrollOffset.Y + 1), this) is not Point at
                || at.Y < 0 || at.Y > Bounds.Height)
            {
                continue;
            }
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            int index = i;
            foreach ((string label, ConflictChoice choice, string tip) in new[]
            {
                ("Keep mine", ConflictChoice.Mine, $"Keep {Side(b.MineLabel, "this branch")}'s side"),
                ("Take theirs", ConflictChoice.Theirs, $"Take {Side(b.TheirsLabel, "the other branch")}'s side"),
                ("Keep both", ConflictChoice.Both, "Keep both sides, this branch's first"),
            })
            {
                var button = new Button
                {
                    Content = label,
                    FontSize = 11,
                    Padding = new Thickness(6, 0),
                    MinHeight = 0,
                    Height = Math.Max(14, vl.Height - 2),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Classes = { "chip" },
                };
                ToolTip.SetTip(button, tip);
                button.Click += (_, _) => Resolve(index, choice);
                bar.Children.Add(button);
            }
            SetLeft(bar, at.X);
            SetTop(bar, at.Y);
            Children.Add(bar);
        }
    }

    private static string Side(string label, string otherwise) => label.Length > 0 ? label : otherwise;
}
