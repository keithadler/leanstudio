using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using LeanStudio.App.Services;
using XTerm.Buffer;
using TermKey = XTerm.Input.Key;
using TermMods = XTerm.Input.KeyModifiers;

namespace LeanStudio.App.Views;

/// <summary>
/// The integrated terminal's screen: the emulator's cells drawn in the editor's font (colours, bold, italics,
/// underline, inverse, wide characters, the cursor), keystrokes sent to the shell, the wheel scrolling back through
/// what has scrolled off, and a mouse-drag selection copied with ⌘C (Ctrl+Shift+C). The screen's size follows the
/// view's, and the shell is told.
/// </summary>
public sealed class TerminalView : Control
{
    private TerminalSession? _session;
    private double _cellWidth = 8, _cellHeight = 17;
    private (int Col, int Row)? _selStart, _selEnd; // rows are absolute buffer lines
    private bool _selecting;

    /// <summary>A terminal view with no session yet.</summary>
    public TerminalView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Avalonia.Input.Cursor(StandardCursorType.Ibeam);
    }

    /// <summary>The editor's font, which the terminal uses too.</summary>
    public FontFamily FontFamily { get; set; } = new("JuliaMono, Cascadia Code, SF Mono, Menlo, Consolas, DejaVu Sans Mono, monospace");

    /// <summary>The font size.</summary>
    public double FontSize { get; set; } = 13;

    /// <summary>Defines <see cref="Session"/>.</summary>
    public static readonly DirectProperty<TerminalView, TerminalSession?> SessionProperty =
        AvaloniaProperty.RegisterDirect<TerminalView, TerminalSession?>(nameof(Session), v => v.Session, (v, s) => v.Session = s);

    /// <summary>The session shown; setting it redraws and fits the session to the view.</summary>
    public TerminalSession? Session
    {
        get => _session;
        set
        {
            TerminalSession? old = _session;
            if (!SetAndRaise(SessionProperty, ref _session, value))
            {
                return;
            }
            if (old is not null)
            {
                old.Changed -= InvalidateVisual;
            }
            _selStart = _selEnd = null;
            if (_session is not null)
            {
                _session.Changed += InvalidateVisual;
                Fit();
            }
            InvalidateVisual();
        }
    }

    /// <summary>How many columns and rows fit in the view, in the current font.</summary>
    public (int Cols, int Rows) FitSize()
    {
        Measure();
        return ((int)Math.Max(10, Math.Floor((Bounds.Width - 8) / _cellWidth)), (int)Math.Max(2, Math.Floor((Bounds.Height - 4) / _cellHeight)));
    }

    private void Measure()
    {
        var m = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily), FontSize, Brushes.White);
        _cellWidth = Math.Max(1, m.Width / 10);
        _cellHeight = Math.Max(1, Math.Ceiling(m.Height * 1.15));
    }

    private void Fit()
    {
        if (_session is not null && Bounds.Width > 0 && Bounds.Height > 0)
        {
            (int cols, int rows) = FitSize();
            _session.Resize(cols, rows);
        }
    }

    /// <inheritdoc/>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Fit();
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        IBrush background = Brush("EditorBackground") ?? Brushes.Black;
        context.FillRectangle(background, new Rect(Bounds.Size));
        if (_session is null)
        {
            return;
        }
        Measure();
        XTerm.Terminal term = _session.Term;
        TerminalBuffer buffer = term.Buffer;
        Color defaultFg = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light ? Color.FromRgb(0x1B, 0x1E, 0x23) : Color.FromRgb(0xD4, 0xD4, 0xD4);
        var face = new Typeface(FontFamily);
        var bold = new Typeface(FontFamily, FontStyle.Normal, FontWeight.Bold);
        var italic = new Typeface(FontFamily, FontStyle.Italic);
        const double left = 4, top = 2;
        for (int row = 0; row < term.Rows; row++)
        {
            int absolute = buffer.YDisp + row;
            BufferLine? line = absolute < buffer.Lines.Length ? buffer.GetLine(absolute) : null;
            if (line is null)
            {
                continue;
            }
            double y = top + row * _cellHeight;
            int col = 0;
            while (col < Math.Min(line.Length, term.Cols))
            {
                // A run of cells that look the same, drawn as one piece of text.
                BufferCell first = line[col];
                AttributeData attr = first.Attributes;
                int start = col;
                var text = new StringBuilder();
                while (col < Math.Min(line.Length, term.Cols) && line[col].Attributes.Equals(attr))
                {
                    BufferCell c = line[col];
                    if (c.Width > 0)
                    {
                        text.Append(c.Content.Length == 0 ? " " : c.Content);
                        if (c.Width == 2)
                        {
                            col++; // a wide character covers the next cell too
                        }
                    }
                    col++;
                }
                (Color fg, Color? bg) = Colors(attr, term, defaultFg);
                var rect = new Rect(left + start * _cellWidth, y, (col - start) * _cellWidth, _cellHeight);
                if (bg is Color b)
                {
                    context.FillRectangle(new SolidColorBrush(b), rect);
                }
                string s = text.ToString();
                if (s.Trim().Length > 0 && !attr.IsInvisible())
                {
                    var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        attr.IsBold() ? bold : attr.IsItalic() ? italic : face, FontSize, new SolidColorBrush(attr.IsDim() ? Color.FromArgb(0xA0, fg.R, fg.G, fg.B) : fg));
                    if (attr.IsUnderline() || attr.IsStrikethrough())
                    {
                        ft.SetTextDecorations(attr.IsUnderline() ? TextDecorations.Underline : TextDecorations.Strikethrough);
                    }
                    context.DrawText(ft, new Point(rect.X, y + (_cellHeight - ft.Height) / 2));
                }
            }
            DrawSelection(context, absolute, y, left);
        }
        // The cursor, when the view is at the bottom (where the shell is).
        if (term.CursorVisible && buffer.YDisp == buffer.YBase && _session.ExitCode is null)
        {
            var at = new Rect(left + buffer.X * _cellWidth, top + buffer.Y * _cellHeight, _cellWidth, _cellHeight);
            IBrush accent = Brush("GoalAccent") ?? Brushes.CornflowerBlue;
            if (IsFocused)
            {
                context.FillRectangle(new SolidColorBrush(Color.FromArgb(0xB0, 0x6F, 0xB3, 0xFF)), at);
            }
            else
            {
                context.DrawRectangle(null, new Pen(accent, 1), at.Deflate(0.5));
            }
        }
    }

    private void DrawSelection(DrawingContext context, int absolute, double y, double left)
    {
        if (Selection() is not ((int c0, int r0), (int c1, int r1)) || absolute < r0 || absolute > r1)
        {
            return;
        }
        int from = absolute == r0 ? c0 : 0, to = absolute == r1 ? c1 : _session!.Term.Cols;
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(0x60, 0x6F, 0xB3, 0xFF)), new Rect(left + from * _cellWidth, y, Math.Max(0, to - from) * _cellWidth, _cellHeight));
    }

    /// <summary>The selection, start before end, or null.</summary>
    private ((int Col, int Row) From, (int Col, int Row) To)? Selection()
    {
        if (_selStart is not { } a || _selEnd is not { } b || a == b)
        {
            return null;
        }
        return a.Row < b.Row || (a.Row == b.Row && a.Col <= b.Col) ? (a, b) : (b, a);
    }

    /// <summary>The selected text, lines joined with newlines; empty with no selection.</summary>
    public string SelectedText()
    {
        if (_session is null || Selection() is not ((int c0, int r0), (int c1, int r1)))
        {
            return "";
        }
        var lines = new List<string>();
        TerminalBuffer buffer = _session.Term.Buffer;
        for (int r = r0; r <= r1 && r < buffer.Lines.Length; r++)
        {
            if (buffer.GetLine(r) is BufferLine line)
            {
                lines.Add(line.TranslateToString(true, r == r0 ? c0 : 0, r == r1 ? c1 : line.Length));
            }
        }
        return string.Join('\n', lines);
    }

    /// <summary>The colours of a cell: the emulator's palette for indexed colours, the theme's for the default ones.</summary>
    private static (Color Fg, Color? Bg) Colors(AttributeData attr, XTerm.Terminal term, Color defaultFg)
    {
        Color? Of(int value, int mode, bool isFg)
        {
            if (mode == 1)
            {
                return Color.FromRgb((byte)(value >> 16), (byte)(value >> 8), (byte)value); // 24-bit
            }
            if (value is >= 0 and < 256)
            {
                int rgb = term.Colors[value];
                return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            }
            return null; // the default colour
        }
        Color fg = Of(attr.GetFgColor(), attr.GetFgColorMode(), true) ?? defaultFg;
        Color? bg = Of(attr.GetBgColor(), attr.GetBgColorMode(), false);
        if (attr.IsInverse())
        {
            return (bg ?? Color.FromRgb(0x1E, 0x1E, 0x1E), fg);
        }
        return (fg, bg);
    }

    private (int Col, int Row) CellAt(Point p)
    {
        TerminalBuffer buffer = _session!.Term.Buffer;
        int col = Math.Clamp((int)((p.X - 4) / _cellWidth), 0, _session.Term.Cols);
        int row = Math.Clamp((int)((p.Y - 2) / _cellHeight), 0, _session.Term.Rows - 1);
        return (col, buffer.YDisp + row);
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_session is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _selStart = _selEnd = CellAt(e.GetPosition(this));
            _selecting = true;
            e.Pointer.Capture(this);
            InvalidateVisual();
        }
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_selecting && _session is not null)
        {
            _selEnd = CellAt(e.GetPosition(this));
            InvalidateVisual();
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _selecting = false;
        e.Pointer.Capture(null);
    }

    /// <inheritdoc/>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_session is not null)
        {
            _session.Term.ScrollLines((int)Math.Round(-e.Delta.Y * 3));
            InvalidateVisual();
            e.Handled = true;
        }
    }

    /// <inheritdoc/>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_session is not null && e.Text is { Length: > 0 } text)
        {
            Typed(text);
            e.Handled = true;
        }
    }

    /// <summary>Send typed text to the shell, back at the bottom of the scrollback, with the selection cleared.</summary>
    private void Typed(string data)
    {
        _selStart = _selEnd = null;
        _session!.Term.ScrollToBottom();
        _session.Input(data);
    }

    /// <inheritdoc/>
    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_session is null)
        {
            return;
        }
        bool mac = OperatingSystem.IsMacOS();
        bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift), alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        // Copy and paste: ⌘C and ⌘V on macOS, Ctrl+Shift+C and Ctrl+Shift+V elsewhere (Ctrl+C belongs to the shell).
        bool copy = e.Key == Key.C && (mac ? cmd : ctrl && shift);
        bool paste = e.Key == Key.V && (mac ? cmd : ctrl && shift);
        if (copy)
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb && SelectedText() is { Length: > 0 } sel)
            {
                await ClipboardExtensions.SetValueAsync(cb, DataFormat.Text, sel);
            }
            e.Handled = true;
            return;
        }
        if (paste)
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb && await ClipboardExtensions.TryGetValueAsync(cb, DataFormat.Text) is string text)
            {
                _session.Term.Paste(text);
            }
            e.Handled = true;
            return;
        }
        if (cmd && mac)
        {
            return; // the app's own shortcuts (⌘J, ⌘P…) go on working
        }
        var mods = (shift ? TermMods.Shift : TermMods.None) | (alt ? TermMods.Alt : TermMods.None) | (ctrl ? TermMods.Control : TermMods.None);
        TermKey? special = e.Key switch
        {
            Key.Enter => TermKey.Enter,
            Key.Tab => TermKey.Tab,
            Key.Back => TermKey.Backspace,
            Key.Escape => TermKey.Escape,
            Key.Up => TermKey.UpArrow,
            Key.Down => TermKey.DownArrow,
            Key.Left => TermKey.LeftArrow,
            Key.Right => TermKey.RightArrow,
            Key.Home => TermKey.Home,
            Key.End => TermKey.End,
            Key.PageUp => TermKey.PageUp,
            Key.PageDown => TermKey.PageDown,
            Key.Insert => TermKey.Insert,
            Key.Delete => TermKey.Delete,
            >= Key.F1 and <= Key.F12 => TermKey.F1 + (e.Key - Key.F1),
            _ => null,
        };
        if (special is TermKey k)
        {
            Typed(_session.Term.GenerateKeyInput(k, mods));
            e.Handled = true;
        }
        else if (ctrl && !shift && e.Key is >= Key.A and <= Key.Z)
        {
            Typed(((char)(e.Key - Key.A + 1)).ToString()); // Ctrl+C is ETX, Ctrl+D EOT…
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.OemOpenBrackets)
        {
            Typed("\u001b");
            e.Handled = true;
        }
    }

    private IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out object? r) ? r as IBrush : null;
}
