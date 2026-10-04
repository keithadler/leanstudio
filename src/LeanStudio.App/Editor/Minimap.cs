using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using LeanStudio.Core.Editing;
using LeanStudio.Lsp;

namespace LeanStudio.App.Editor;

/// <summary>
/// The minimap beside the editor: the whole file in miniature (each word a short bar, comments fainter), with the
/// lines shown in the editor framed, errors and warnings marked in red and amber, and slow declarations from the
/// profiler in orange. Click or drag to scroll there.
/// </summary>
public sealed class Minimap : Control
{
    /// <summary>The minimap's width, in pixels.</summary>
    public const double MapWidth = 90;

    private const double MaxLineHeight = 2, CharWidth = 1, Left = 6;

    private readonly TextEditor _editor;
    private RenderTargetBitmap? _picture;
    private (string? Text, double Height, bool Dark) _pictureOf;
    private string? _text;
    private IReadOnlyList<Diagnostic> _diagnostics = [];
    private IReadOnlyList<(int Line, int Heat)> _heat = [];
    private bool _dragging;

    /// <summary>A minimap of <paramref name="editor"/>.</summary>
    public Minimap(TextEditor editor)
    {
        _editor = editor;
        Width = MapWidth;
        ClipToBounds = true;
        Cursor = new Avalonia.Input.Cursor(StandardCursorType.Arrow);
    }

    /// <summary>The lines' height in the map: two pixels, less when the file is too long to fit.</summary>
    public double LineHeight => _text is null ? MaxLineHeight : Math.Min(MaxLineHeight, Math.Max(0.25, Bounds.Height / Math.Max(1, LineCount)));

    private int LineCount => _text is null ? 0 : _editor.Document?.LineCount ?? 0;

    /// <summary>The text changed: draw it again (lazily).</summary>
    public void TextChanged()
    {
        _text = null;
        InvalidateVisual();
    }

    /// <summary>The marks to draw over the text: Lean's errors and warnings, and slow declarations (0-based line, heat 1 or 2).</summary>
    public void SetMarks(IReadOnlyList<Diagnostic> diagnostics, IReadOnlyList<(int Line, int Heat)> heat)
    {
        _diagnostics = diagnostics;
        _heat = heat;
        InvalidateVisual();
    }

    /// <summary>The editor scrolled: move the frame.</summary>
    public void Scrolled() => InvalidateVisual();

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        if (_editor.Document is null || Bounds.Height <= 0)
        {
            return;
        }
        _text ??= _editor.Document.Text;
        bool dark = (_editor.Background as ISolidColorBrush)?.Color is Color bg && bg.R + bg.G + bg.B < 384;
        if (_picture is null || _pictureOf != (_text, Bounds.Height, dark))
        {
            _picture?.Dispose();
            _picture = Draw(_text, dark);
            _pictureOf = (_text, Bounds.Height, dark);
        }
        context.FillRectangle(_editor.Background ?? Brushes.Transparent, new Rect(Bounds.Size));
        if (_picture is not null)
        {
            context.DrawImage(_picture, new Rect(0, 0, _picture.PixelSize.Width, _picture.PixelSize.Height));
        }
        double h = LineHeight;
        foreach ((int line, int heat) in _heat)
        {
            context.FillRectangle(new SolidColorBrush(heat >= 2 ? Color.FromArgb(0xC0, 0xF1, 0x4C, 0x4C) : Color.FromArgb(0xA0, 0xE5, 0x9E, 0x2C)), new Rect(0, line * h, 3, Math.Max(2, h)));
        }
        foreach (Diagnostic d in _diagnostics)
        {
            if (d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            {
                Color c = d.Severity == DiagnosticSeverity.Error ? Color.FromRgb(0xF1, 0x4C, 0x4C) : Color.FromRgb(0xE5, 0x9E, 0x2C);
                context.FillRectangle(new SolidColorBrush(c), new Rect(MapWidth - 5, d.Range.Start.Line * h, 4, Math.Max(2, (d.Range.End.Line - d.Range.Start.Line + 1) * h)));
            }
        }
        // The frame: the lines the editor shows now.
        (double top, double height) = Viewport();
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(0x28, 0x80, 0x90, 0xA8)), new Rect(0, top, MapWidth, height));
        context.DrawRectangle(new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x90, 0xA8)), 1), new Rect(0.5, top + 0.5, MapWidth - 1, Math.Max(1, height - 1)));
    }

    /// <summary>Where the frame is, and how tall, in the map's pixels.</summary>
    public (double Top, double Height) Viewport()
    {
        TextView view = _editor.TextArea.TextView;
        double lineHeight = Math.Max(1, view.DefaultLineHeight);
        double first = view.ScrollOffset.Y / lineHeight;
        double shown = view.Bounds.Height / lineHeight;
        return (first * LineHeight, Math.Max(4, shown * LineHeight));
    }

    /// <summary>The text in miniature, as a picture: one bar per word, comments fainter.</summary>
    private RenderTargetBitmap? Draw(string text, bool dark)
    {
        int width = (int)MapWidth, height = (int)Math.Ceiling(Bounds.Height);
        if (height <= 0)
        {
            return null;
        }
        var bitmap = new RenderTargetBitmap(new PixelSize(width, height));
        bool[] code = LeanText.CodeMask(text);
        Color ink = dark ? Color.FromArgb(0x90, 0xC8, 0xCC, 0xD2) : Color.FromArgb(0x90, 0x40, 0x44, 0x4A);
        Color faint = dark ? Color.FromArgb(0x50, 0x7C, 0xA6, 0x6E) : Color.FromArgb(0x50, 0x40, 0x80, 0x40);
        IBrush inkBrush = new SolidColorBrush(ink), faintBrush = new SolidColorBrush(faint);
        double h = LineHeight;
        using (DrawingContext dc = bitmap.CreateDrawingContext())
        {
            int line = 0, col = 0, runStart = -1;
            bool runCode = true;
            void Flush(int end)
            {
                if (runStart >= 0 && runStart * CharWidth + Left < MapWidth)
                {
                    double x = Left + runStart * CharWidth, w = Math.Min((end - runStart) * CharWidth, MapWidth - x);
                    dc.FillRectangle(runCode ? inkBrush : faintBrush, new Rect(x, line * h, w, Math.Max(0.5, h * 0.75)));
                }
                runStart = -1;
            }
            for (int i = 0; i < text.Length && line * h < height; i++)
            {
                char c = text[i];
                if (c == '\n')
                {
                    Flush(col);
                    line++;
                    col = 0;
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    Flush(col);
                }
                else if (runStart < 0 || runCode != code[i])
                {
                    Flush(col);
                    runStart = col;
                    runCode = code[i];
                }
                col += c == '\t' ? 2 : 1;
            }
            Flush(col);
        }
        return bitmap;
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        e.Pointer.Capture(this);
        ScrollTo(e.GetPosition(this).Y);
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            ScrollTo(e.GetPosition(this).Y);
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    /// <summary>Scroll the editor so the line at <paramref name="y"/> in the map is in the middle of the view.</summary>
    public void ScrollTo(double y)
    {
        TextView view = _editor.TextArea.TextView;
        double line = y / Math.Max(0.01, LineHeight);
        double target = line * view.DefaultLineHeight - view.Bounds.Height / 2;
        _editor.ScrollToOffset(target);
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _picture?.Dispose();
        _picture = null;
    }
}
