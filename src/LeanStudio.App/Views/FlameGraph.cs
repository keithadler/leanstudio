using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LeanStudio.Core.Proofs;

namespace LeanStudio.App.Views;

/// <summary>
/// A flame graph of Lean's trace profile (drawn as an icicle, the root at the top): each step a box as wide as its
/// share of the cost, the steps inside it underneath, coloured by what kind of work it is. Hover a box to read it
/// in full; click one to zoom in so it fills the width, and click one of the boxes above it (or press Escape) to
/// zoom back out.
/// </summary>
public sealed class FlameGraph : Control
{
    private const double RowHeight = 20, InfoHeight = 38, LegendHeight = 22, Gap = 1;

    private ProfileNode? _root;
    private readonly List<ProfileNode> _zoom = []; // from the root down to the node that fills the width
    private readonly List<(Rect Box, ProfileNode Node)> _boxes = [];
    private ProfileNode? _hover;
    private int _depth;
    private IReadOnlyList<string> _kinds = [];
    private bool _anyFailed;

    /// <summary>Create an empty graph; set <see cref="Root"/> to draw one.</summary>
    public FlameGraph()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>The tree drawn; setting it zooms all the way out.</summary>
    public static readonly DirectProperty<FlameGraph, ProfileNode?> RootProperty =
        AvaloniaProperty.RegisterDirect<FlameGraph, ProfileNode?>(nameof(Root), g => g.Root, (g, v) => g.Root = v);

    /// <summary>What the values count, for the labels.</summary>
    public static readonly StyledProperty<ProfileUnit> UnitProperty = AvaloniaProperty.Register<FlameGraph, ProfileUnit>(nameof(Unit));

    /// <summary>The tree drawn; setting it zooms all the way out.</summary>
    public ProfileNode? Root
    {
        get => _root;
        set
        {
            if (SetAndRaise(RootProperty, ref _root, value))
            {
                _zoom.Clear();
                if (value is not null)
                {
                    _zoom.Add(value);
                }
                _hover = null;
                _depth = value is null ? 0 : Depth(value);
                var present = value?.DescendantsAndSelf().Select(n => n.Kind).ToHashSet() ?? [];
                _kinds = [.. Kinds.Where(present.Contains), .. present.Where(k => !Kinds.Contains(k) && k != "file").Order(StringComparer.Ordinal).Take(3)];
                _anyFailed = value?.DescendantsAndSelf().Any(n => n.Failed) == true;
                InvalidateMeasure();
                InvalidateVisual();
            }
        }
    }

    /// <summary>What the values count, for the labels.</summary>
    public ProfileUnit Unit
    {
        get => GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    /// <summary>The node that fills the width now (the root when not zoomed), or null when there is no tree.</summary>
    public ProfileNode? Focused => _zoom.Count > 0 ? _zoom[^1] : null;

    /// <summary>Zoom in on <paramref name="node"/>, if it is in the tree.</summary>
    public void ZoomTo(ProfileNode node)
    {
        if (_root is null || PathTo(_root, node) is not { } path)
        {
            return;
        }
        _zoom.Clear();
        _zoom.AddRange(path);
        InvalidateVisual();
    }

    private static List<ProfileNode>? PathTo(ProfileNode from, ProfileNode target)
    {
        if (ReferenceEquals(from, target))
        {
            return [from];
        }
        foreach (ProfileNode c in from.Children)
        {
            if (PathTo(c, target) is { } rest)
            {
                rest.Insert(0, from);
                return rest;
            }
        }
        return null;
    }

    /// <summary>The indices in the zoom path of the ancestors drawn above the zoomed node: the root and the parent.</summary>
    private IEnumerable<int> Ancestors() => _zoom.Count switch
    {
        1 => [],
        2 => [0],
        _ => [0, _zoom.Count - 2],
    };

    private static int Depth(ProfileNode n) => 1 + (n.Children.Count == 0 ? 0 : n.Children.Max(Depth));

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize) =>
        new(0, _root is null ? 0 : InfoHeight + (_depth + 2) * RowHeight + LegendHeight);

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UnitProperty)
        {
            InvalidateVisual();
        }
    }

    private static readonly Typeface Face = new("JuliaMono, Cascadia Code, SF Mono, Menlo, Consolas, monospace");
    private static readonly Typeface UiFace = new("Inter, Segoe UI, SF Pro Text, Helvetica, sans-serif");
    private static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(0x1B, 0x1E, 0x23));
    private static readonly IPen HoverPen = new Pen(new SolidColorBrush(Color.FromRgb(0x1B, 0x1E, 0x23)), 1.5);
    private static readonly IPen FailedPen = new Pen(new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)), 1.5);

    /// <summary>The colour of a kind of work (see <see cref="ProfileNode.Kind"/>); light enough for dark text in both themes.</summary>
    public static Color ColorOf(string kind) => kind switch
    {
        "file" => Color.FromRgb(0xB0, 0xBE, 0xC5),
        "declaration" or "command" or "proof" or "definition" => Color.FromRgb(0xE8, 0xC8, 0x72),
        "elaboration" => Color.FromRgb(0xF2, 0xA6, 0x5A),
        "instances" => Color.FromRgb(0xB3, 0x9D, 0xDB),
        "simp" => Color.FromRgb(0x7F, 0xC8, 0xA9),
        "unification" => Color.FromRgb(0x82, 0xB1, 0xFF),
        "reduction" => Color.FromRgb(0x80, 0xDE, 0xEA),
        "kernel" => Color.FromRgb(0xA5, 0xD6, 0xA7),
        "compiler" => Color.FromRgb(0xBC, 0xAA, 0xA4),
        "meta" => Color.FromRgb(0x90, 0xCA, 0xF9),
        _ => Color.FromRgb(0xCF, 0xD8, 0xDC),
    };

    /// <summary>The kinds of work, in the order the legend lists them.</summary>
    public static IReadOnlyList<string> Kinds { get; } = ["declaration", "elaboration", "instances", "simp", "unification", "reduction", "kernel", "compiler", "meta"];

    /// <summary>A kind's colour as a brush, for the legend.</summary>
    public static IBrush BrushOf(string kind) => new SolidColorBrush(ColorOf(kind));

    /// <summary>Vary a colour a little by the text, so neighbouring boxes of one kind can be told apart.</summary>
    private static Color Shade(Color c, string text)
    {
        int h = 0;
        foreach (char ch in text)
        {
            h = (h * 31 + ch) & 0xFFFF;
        }
        double f = 0.9 + (h % 13) / 100.0;
        static byte Clamp(double v) => (byte)Math.Clamp(v, 0, 255);
        return Color.FromRgb(Clamp(c.R * f), Clamp(c.G * f), Clamp(c.B * f));
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        _boxes.Clear();
        if (_root is null || _zoom.Count == 0)
        {
            return;
        }
        double width = Bounds.Width;
        ProfileNode focus = _zoom[^1];
        double scale = focus.Value > 0 ? width / focus.Value : 0;
        double top = InfoHeight;
        // The zoomed-out ancestors, full width, faded: clicking one zooms back out to it. Only the root and the
        // parent, so a deep zoom leaves the room to what it zoomed in on.
        foreach (int i in Ancestors())
        {
            DrawBox(context, new Rect(0, top, width, RowHeight - Gap), _zoom[i], faded: true);
            top += RowHeight;
        }
        Draw(context, focus, 0, top, scale);
        DrawInfo(context, width);
        DrawLegend(context);
    }

    /// <summary>A swatch and a name for each kind of work in the tree, along the bottom.</summary>
    private void DrawLegend(DrawingContext context)
    {
        double x = 2, y = Bounds.Height - LegendHeight + 6;
        IBrush fg = TextBrush();
        using var _ = context.PushOpacity(0.75);
        foreach (string kind in _anyFailed ? [.. _kinds, "failed"] : _kinds)
        {
            var ft = new FormattedText(kind, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiFace, 10, fg);
            if (x + 14 + ft.Width > Bounds.Width)
            {
                break;
            }
            var swatch = new Rect(x, y + 1, 10, 10);
            if (kind == "failed")
            {
                context.DrawRectangle(null, FailedPen, swatch.Deflate(0.75), 2, 2);
            }
            else
            {
                context.FillRectangle(new SolidColorBrush(ColorOf(kind)), swatch, 2);
            }
            context.DrawText(ft, new Point(x + 14, y));
            x += 14 + ft.Width + 12;
        }
    }

    private void Draw(DrawingContext context, ProfileNode n, double x, double y, double scale)
    {
        double w = n.Value * scale;
        if (w < 0.5)
        {
            return;
        }
        DrawBox(context, new Rect(x, y, Math.Max(0, w - Gap), RowHeight - Gap), n, faded: false);
        double cx = x;
        foreach (ProfileNode c in n.Children)
        {
            Draw(context, c, cx, y + RowHeight, scale);
            cx += c.Value * scale;
        }
    }

    private void DrawBox(DrawingContext context, Rect box, ProfileNode n, bool faded)
    {
        _boxes.Add((box, n));
        Color c = Shade(ColorOf(n.Kind), n.Text);
        if (faded)
        {
            c = Color.FromArgb(0x90, c.R, c.G, c.B);
        }
        context.FillRectangle(new SolidColorBrush(c), box, 2);
        if (n.Failed)
        {
            context.DrawRectangle(null, FailedPen, box.Deflate(0.75), 2, 2);
        }
        if (ReferenceEquals(n, _hover))
        {
            context.DrawRectangle(null, HoverPen, box.Deflate(0.75), 2, 2);
        }
        if (box.Width < 28)
        {
            return;
        }
        string label = (n.Failed ? "✗ " : "") + n.Label;
        var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 11, Ink)
        {
            MaxTextWidth = Math.Max(1, box.Width - 8),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        context.DrawText(ft, new Point(box.X + 4, box.Y + (box.Height - ft.Height) / 2));
    }

    private void DrawInfo(DrawingContext context, double width)
    {
        ProfileNode? n = _hover ?? _zoom[^1];
        IBrush fg = TextBrush();
        double total = _root!.Value;
        string head = n.Label.Length == 0 ? n.Category : n.Label;
        string share = (total > 0 ? n.Value / total : 0).ToString("P1", CultureInfo.InvariantCulture);
        string numbers = $"{n.Kind} · {n.Category} · {DeclarationTiming.Format(n.Value, Unit)} ({share} of all) · itself {DeclarationTiming.Format(n.Self, Unit)}"
            + (n.Failed ? " · failed, its work thrown away" : "")
            + (_hover is null ? (_zoom.Count > 1 ? " · click a faded bar or press Escape to zoom out" : " · hover a box to read it, click to zoom in") : "");
        var a = new FormattedText(head, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 12, fg)
        {
            MaxTextWidth = Math.Max(1, width - 8),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        var b = new FormattedText(numbers, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, UiFace, 11, fg)
        {
            MaxTextWidth = Math.Max(1, width - 8),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        context.DrawText(a, new Point(2, 2));
        using (context.PushOpacity(0.7))
        {
            context.DrawText(b, new Point(2, 19));
        }
    }

    private static readonly IBrush LightText = new SolidColorBrush(Color.FromRgb(0x1B, 0x1E, 0x23));
    private static readonly IBrush DarkText = new SolidColorBrush(Color.FromRgb(0xE3, 0xE6, 0xEA));

    private IBrush TextBrush() => ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light ? LightText : DarkText;

    private ProfileNode? HitTest(Point p)
    {
        for (int i = _boxes.Count - 1; i >= 0; i--)
        {
            if (_boxes[i].Box.Contains(p))
            {
                return _boxes[i].Node;
            }
        }
        return null;
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        ProfileNode? n = HitTest(e.GetPosition(this));
        if (!ReferenceEquals(n, _hover))
        {
            _hover = n;
            Cursor = n is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
            InvalidateVisual();
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is not null)
        {
            _hover = null;
            InvalidateVisual();
        }
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (HitTest(e.GetPosition(this)) is not ProfileNode n)
        {
            return;
        }
        int ancestor = _zoom.IndexOf(n);
        if (ancestor >= 0 && ancestor < _zoom.Count - 1)
        {
            _zoom.RemoveRange(ancestor + 1, _zoom.Count - ancestor - 1);
        }
        else if (!ReferenceEquals(n, _zoom[^1]) && _root is not null && PathTo(_root, n) is { } path)
        {
            _zoom.Clear();
            _zoom.AddRange(path);
        }
        e.Handled = true;
        InvalidateVisual();
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _zoom.Count > 1)
        {
            _zoom.RemoveAt(_zoom.Count - 1);
            e.Handled = true;
            InvalidateVisual();
        }
    }
}
