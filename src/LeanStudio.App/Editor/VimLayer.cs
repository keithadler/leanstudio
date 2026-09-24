using Avalonia;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using LeanStudio.Core.Editing;

namespace LeanStudio.App.Editor;

/// <summary>The Vim engine's view of the text editor: its document, caret, selection and undo.</summary>
public sealed class EditorVimHost(TextEditor editor, Func<string, bool> ex) : IVimHost
{
    private int _depth;

    /// <inheritdoc />
    public string Text => editor.Document.Text;

    /// <inheritdoc />
    public int Caret
    {
        get => editor.CaretOffset;
        set
        {
            editor.CaretOffset = Math.Clamp(value, 0, editor.Document.TextLength);
            editor.TextArea.Caret.BringCaretToView();
        }
    }

    /// <inheritdoc />
    public void Replace(int offset, int length, string text) => editor.Document.Replace(offset, length, text);

    /// <inheritdoc />
    public void Select(int start, int end)
    {
        if (end > start)
        {
            editor.Select(start, end - start);
        }
        else
        {
            editor.TextArea.ClearSelection();
        }
    }

    /// <inheritdoc />
    public void BeginChange()
    {
        if (_depth++ == 0)
        {
            editor.Document.BeginUpdate();
        }
    }

    /// <inheritdoc />
    public void EndChange()
    {
        if (_depth > 0 && --_depth == 0)
        {
            editor.Document.EndUpdate();
        }
    }

    /// <inheritdoc />
    public void Undo() => editor.Undo();

    /// <inheritdoc />
    public void Redo() => editor.Redo();

    /// <inheritdoc />
    public bool Ex(string command) => ex(command);
}

/// <summary>Vim's block cursor: in normal and visual modes, the character under the caret, boxed.</summary>
public sealed class VimBlockCaret(TextEditor editor) : IBackgroundRenderer
{
    private static readonly IBrush Fill = new SolidColorBrush(Color.FromArgb(0x70, 0x6F, 0xB3, 0xFF));

    /// <summary>Whether to draw it (Vim on, and not in insert mode).</summary>
    public bool Visible { get; set; }

    /// <inheritdoc />
    public KnownLayer Layer => KnownLayer.Selection;

    /// <inheritdoc />
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!Visible || !textView.VisualLinesValid || editor.Document is not TextDocument doc)
        {
            return;
        }
        int offset = editor.CaretOffset;
        int length = offset < doc.TextLength && doc.GetCharAt(offset) != '\n' ? 1 : 0;
        var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true };
        builder.AddSegment(textView, new TextSegment { StartOffset = offset, Length = length });
        Geometry? g = builder.CreateGeometry();
        if (g is null || length == 0)
        {
            // An empty line or the end of the text: a block the width of a space at the caret.
            Rect caret = editor.TextArea.Caret.CalculateCaretRectangle();
            drawingContext.FillRectangle(Fill, new Rect(caret.X - textView.ScrollOffset.X, caret.Y - textView.ScrollOffset.Y, editor.FontSize * 0.6, caret.Height));
            return;
        }
        drawingContext.DrawGeometry(Fill, null, g);
    }
}
