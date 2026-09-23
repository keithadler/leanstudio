using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace LeanStudio.App.Editor;

/// <summary>Outlines the bracket at the caret and its partner, so nested ⟨…⟩ and (…) are easy to read.</summary>
public sealed class BracketHighlighter : IBackgroundRenderer
{
    private int _a = -1, _b = -1;
    private static readonly IPen Pen = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x88, 0x88, 0x88)), 1);
    private static readonly IBrush Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x88, 0x88, 0x88));

    public KnownLayer Layer => KnownLayer.Selection;

    public void Update(int a, int b)
    {
        _a = a;
        _b = b;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_a < 0 || _b < 0 || textView.Document is not TextDocument doc)
        {
            return;
        }
        foreach (int o in new[] { _a, _b })
        {
            if (o >= doc.TextLength)
            {
                continue;
            }
            foreach (Rect r in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = o, EndOffset = o + 1 }))
            {
                drawingContext.DrawRectangle(Fill, Pen, r);
            }
        }
    }
}
