using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using LeanStudio.Core.Editing;
using Cursor = LeanStudio.Core.Editing.Cursor;
using Selection = AvaloniaEdit.Editing.Selection;

namespace LeanStudio.App.Editor;

/// <summary>
/// Several cursors in one editor. The editor's own caret and selection are the main cursor; the others are kept
/// here, drawn here, and edited together with it: typing, Backspace and Delete act at every one, as one undoable
/// change. ⌘D (Ctrl+D) adds the next occurrence of the selection, ⌘⇧L (Ctrl+Shift+L) selects every occurrence,
/// ⌘⌥↑/↓ (Ctrl+Alt+↑/↓) add a cursor on the line above or below, and ⌥-click (Alt+click) adds one where you click.
/// Escape, a click, or moving the caret goes back to one cursor.
/// </summary>
public sealed class MultiCursorLayer : IBackgroundRenderer
{
    private static readonly IBrush SelectionFill = new SolidColorBrush(Color.FromArgb(0x55, 0x26, 0x4F, 0x78));
    private static readonly IBrush CaretBrush = new SolidColorBrush(Color.FromRgb(0xAE, 0xAF, 0xAD));

    private readonly TextEditor _editor;
    private readonly List<Cursor> _extra = [];
    private bool _applying;
    private Cursor? _beforeAltClick;
    private Point _altClickAt;

    /// <summary>Attach to <paramref name="editor"/>.</summary>
    public MultiCursorLayer(TextEditor editor)
    {
        _editor = editor;
        editor.TextArea.TextView.BackgroundRenderers.Add(this);
        editor.TextArea.TextView.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        editor.TextArea.TextView.AddHandler(InputElement.PointerReleasedEvent, OnPointerReleased, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        editor.DocumentChanged += (_, _) => Clear();
    }

    /// <summary>A document the editor showed changed without this layer (Lean, undo, another side): one cursor again.</summary>
    public void DocumentChanged()
    {
        if (!_applying)
        {
            Clear();
        }
    }

    /// <inheritdoc />
    public KnownLayer Layer => KnownLayer.Selection;

    /// <summary>Whether there is more than one cursor.</summary>
    public bool IsActive => _extra.Count > 0;

    /// <summary>Every cursor, the main one included, in order.</summary>
    public IReadOnlyList<Cursor> Cursors => MultiCursor.Normalize([Main, .. _extra]);

    private Cursor Main
    {
        get
        {
            TextArea area = _editor.TextArea;
            int caret = area.Caret.Offset;
            if (area.Selection.IsEmpty || area.Selection is RectangleSelection)
            {
                return Cursor.At(caret);
            }
            ISegment s = area.Selection.SurroundingSegment;
            return caret == s.Offset ? new Cursor(s.EndOffset, s.Offset) : new Cursor(s.Offset, s.EndOffset);
        }
    }

    /// <summary>Back to the main cursor only.</summary>
    public void Clear()
    {
        if (_extra.Count > 0)
        {
            _extra.Clear();
            _editor.TextArea.TextView.InvalidateLayer(Layer);
        }
    }

    /// <summary>Make these the cursors: the last becomes the editor's own (it is the one scrolled to).</summary>
    public void Set(IReadOnlyList<Cursor> cursors)
    {
        IReadOnlyList<Cursor> all = MultiCursor.Normalize(cursors);
        if (all.Count == 0)
        {
            return;
        }
        Cursor main = all[^1];
        _extra.Clear();
        _extra.AddRange(all.Take(all.Count - 1));
        TextArea area = _editor.TextArea;
        area.Selection = main.IsEmpty ? Selection.Create(area, main.Position, main.Position) : Selection.Create(area, main.Anchor, main.Position);
        area.Caret.Offset = main.Position;
        area.Caret.BringCaretToView();
        area.TextView.InvalidateLayer(Layer);
    }

    /// <summary>⌘D: add the next occurrence of the selection (or select the word at the cursor).</summary>
    public void AddNextOccurrence() => Set(MultiCursor.AddNextOccurrence(_editor.Document.Text, [.. _extra, Main]));

    /// <summary>⌘⇧L: select every occurrence of the selection (or of the word at the cursor).</summary>
    public void SelectAllOccurrences() => Set(MultiCursor.AllOccurrences(_editor.Document.Text, Main));

    /// <summary>⌘⌥↑/↓: add a cursor on the line above the first cursor, or below the last.</summary>
    public void AddOnAdjacentLine(bool down)
    {
        IReadOnlyList<Cursor> all = Cursors;
        if (MultiCursor.OnAdjacentLine(_editor.Document.Text, down ? all[^1] : all[0], down) is Cursor c)
        {
            Set(down ? [.. all, c] : [c, .. all]);
        }
    }

    /// <summary>Handle a key the editor got first. Returns whether it was handled here.</summary>
    public bool KeyDown(KeyEventArgs e)
    {
        bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift), alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        switch (e.Key)
        {
            case Key.D when cmd && !shift && !alt:
                AddNextOccurrence();
                return true;
            case Key.L when cmd && shift && !alt:
                SelectAllOccurrences();
                return true;
            case Key.Up or Key.Down when cmd && alt && !shift:
                AddOnAdjacentLine(e.Key == Key.Down);
                return true;
        }
        if (!IsActive)
        {
            return false;
        }
        switch (e.Key)
        {
            case Key.Escape:
                Clear();
                return true;
            case Key.Back when !cmd && !alt:
                Edit(MultiCursor.Backspace(_editor.Document.Text, Cursors));
                return true;
            case Key.Delete when !cmd && !alt:
                Edit(MultiCursor.Delete(_editor.Document.Text, Cursors));
                return true;
            case Key.Enter or Key.Return when !cmd && !alt:
                Edit(MultiCursor.Type(Cursors, "\n"));
                return true;
            case Key.Tab when !cmd && !alt && !shift:
                Edit(MultiCursor.Type(Cursors, "  "));
                return true;
            case Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown:
                Clear(); // the editor moves its own caret; the others go
                return false;
        }
        return false;
    }

    /// <summary>Text typed while there are several cursors: it goes in at each. Returns whether it was handled.</summary>
    public bool TextEntering(string text)
    {
        if (!IsActive || text.Length == 0)
        {
            return false;
        }
        Edit(MultiCursor.Type(Cursors, text));
        return true;
    }

    private void Edit((IReadOnlyList<Replacement> Edits, IReadOnlyList<Cursor> Cursors) result)
    {
        TextDocument doc = _editor.Document;
        _applying = true;
        doc.BeginUpdate();
        try
        {
            foreach (Replacement r in result.Edits)
            {
                doc.Replace(r.Offset, r.Length, r.Text);
            }
        }
        finally
        {
            doc.EndUpdate();
            _applying = false;
        }
        Set(result.Cursors);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            // Remember the cursors: a click (not a drag, which selects a column) adds a cursor to them.
            _beforeAltClick = Main;
            _altClickAt = e.GetPosition(_editor.TextArea.TextView);
            return;
        }
        _beforeAltClick = null;
        Clear();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_beforeAltClick is not Cursor before)
        {
            return;
        }
        _beforeAltClick = null;
        Point at = e.GetPosition(_editor.TextArea.TextView);
        if (Math.Abs(at.X - _altClickAt.X) > 3 || Math.Abs(at.Y - _altClickAt.Y) > 3)
        {
            return; // a drag: the editor's column selection
        }
        Set([.. _extra, before, Cursor.At(_editor.TextArea.Caret.Offset)]);
    }

    /// <inheritdoc />
    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_extra.Count == 0 || !textView.VisualLinesValid)
        {
            return;
        }
        int length = textView.Document?.TextLength ?? 0;
        foreach (Cursor c in _extra)
        {
            if (!c.IsEmpty)
            {
                var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
                builder.AddSegment(textView, new TextSegment { StartOffset = Math.Min(c.Start, length), EndOffset = Math.Min(c.End, length) });
                if (builder.CreateGeometry() is Geometry g)
                {
                    drawingContext.DrawGeometry(SelectionFill, null, g);
                }
            }
            TextViewPosition pos = new(textView.Document!.GetLocation(Math.Min(c.Position, length)));
            Point p = textView.GetVisualPosition(pos, VisualYPosition.LineTop) - textView.ScrollOffset;
            Point bottom = textView.GetVisualPosition(pos, VisualYPosition.LineBottom) - textView.ScrollOffset;
            drawingContext.FillRectangle(CaretBrush, new Rect(p.X, p.Y, 2, bottom.Y - p.Y));
        }
    }
}
