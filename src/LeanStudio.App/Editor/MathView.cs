using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LeanStudio.Core.Editing;

namespace LeanStudio.App.Editor;

/// <summary>
/// A formula drawn with the app's own text drawing, no web view: stacked fractions, large operators with limits above
/// and below, sub- and superscripts, roots and brackets that grow with what they hold. It is laid out as boxes (width,
/// height above the baseline, depth below it) the way TeX does, so a formula sits on the text's baseline.
/// </summary>
public sealed class MathView : Control
{
    private static readonly FontFamily MathFont = new("STIX Two Text, STIX Two Math, Cambria Math, Latin Modern Math, Times New Roman, Times, serif");

    private Box? _box;
    private TexNode _node = new TexRow([]);
    private IBrush _brush = Brushes.Gray;
    private double _size = 14;
    private bool _display;

    /// <summary>A formula to draw at <paramref name="size"/>; <paramref name="display"/> sets limits above and below large operators.</summary>
    public MathView(TexNode node, double size, IBrush brush, bool display)
    {
        (_node, _size, _brush, _display) = (node, size, brush, display);
        IsHitTestVisible = false;
    }

    /// <summary>Where the baseline is, from the top of the view.</summary>
    public double Ascent => Layout().Ascent;

    /// <summary>The formula this view draws, as plain text (for tooltips and checks).</summary>
    public string PlainText => TexMath.Flatten(_node);

    private Box Layout() => _box ??= new Layouter(_size, _brush).Build(_node, _size, _display);

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        Box b = Layout();
        return new Size(Math.Ceiling(b.Width) + 1, Math.Ceiling(b.Ascent + b.Descent));
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        Box b = Layout();
        b.Draw(context, 0, b.Ascent);
    }

    // ---- boxes ----

    private abstract class Box
    {
        public double Width, Ascent, Descent;

        public abstract void Draw(DrawingContext c, double x, double baseline);
    }

    private sealed class Glyphs : Box
    {
        private readonly FormattedText _text;

        public Glyphs(FormattedText text, double width)
        {
            _text = text;
            Width = width;
            Ascent = text.Baseline;
            Descent = text.Height - text.Baseline;
        }

        public override void Draw(DrawingContext c, double x, double baseline) => c.DrawText(_text, new Point(x, baseline - Ascent));
    }

    private sealed class Group : Box
    {
        public readonly List<(Box Box, double X, double Y)> Parts = [];

        public override void Draw(DrawingContext c, double x, double baseline)
        {
            foreach ((Box box, double dx, double dy) in Parts)
            {
                box.Draw(c, x + dx, baseline + dy);
            }
        }

        public void Add(Box b, double x, double y)
        {
            Parts.Add((b, x, y));
            Width = Math.Max(Width, x + b.Width);
            Ascent = Math.Max(Ascent, b.Ascent - y);
            Descent = Math.Max(Descent, b.Descent + y);
        }
    }

    private sealed class Drawn(Action<DrawingContext, double, double> draw, double width, double ascent, double descent) : Box
    {
        public override void Draw(DrawingContext c, double x, double baseline) => draw(c, x, baseline);

        public Drawn Sized() { Width = width; Ascent = ascent; Descent = descent; return this; }
    }

    private sealed class Layouter(double baseSize, IBrush brush)
    {
        private readonly Pen _rule = new(brush, Math.Max(1, baseSize / 14));

        public Box Build(TexNode node, double size, bool display) => Make(node, size, display);

        private FormattedText Text(string s, double size, bool italic, bool bold = false) =>
            new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(MathFont, italic ? FontStyle.Italic : FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal), size, brush);

        private static double Axis(double size) => size * 0.26;

        private Box Make(TexNode node, double size, bool display)
        {
            switch (node)
            {
                case TexRow r:
                    return Row(r, size, display);
                case TexAtom a:
                    return Atom(a, size, display);
                case TexScript s:
                    return Script(s, size, display);
                case TexFrac f:
                    return Frac(f, size, display);
                case TexSqrt q:
                    return Sqrt(q, size, display);
                case TexFenced fe:
                    return Fenced(fe, size, display);
                case TexAccent ac:
                    return Accent(ac, size, display);
                case TexMatrix mx:
                    return Matrix(mx, size, display);
                case TexStack st:
                    return Stack(st, size, display);
                default:
                    return new Group();
            }
        }

        private Box Atom(TexAtom a, double size, bool display = false)
        {
            if (a.Kind == TexKind.Space)
            {
                return new Group { Width = a.Em * size };
            }
            if (a.Text.Length == 0)
            {
                return new Group();
            }
            double s = a.Kind == TexKind.BigOp ? size * (display ? 1.35 : 1.12) : size;
            bool italic = a.Kind == TexKind.Var;
            FormattedText t = Text(a.Text, s, italic);
            // Italic glyphs lean into the next symbol; a little space keeps them apart.
            return new Glyphs(t, t.WidthIncludingTrailingWhitespace + (italic ? s * 0.04 : 0));
        }

        private Box Row(TexRow r, double size, bool display)
        {
            var g = new Group();
            double x = 0;
            TexAtom? prev = null;
            foreach (TexNode n in r.Items)
            {
                Box b = Make(n, size, display);
                TexAtom? atom = Head(n);
                double before = 0, after = 0;
                // In sub- and superscripts TeX sets no space around relations and operators.
                if (atom is not null && size >= baseSize * 0.85)
                {
                    bool unary = prev is null || prev.Kind is TexKind.Bin or TexKind.Rel || (prev.Kind == TexKind.Fence && prev.Text is "(" or "[" or "⟨");
                    if (atom.Kind == TexKind.Rel)
                    {
                        before = after = 0.28 * size;
                    }
                    else if (atom.Kind == TexKind.Bin && !unary)
                    {
                        before = after = 0.22 * size;
                    }
                    else if (atom.Kind == TexKind.Func)
                    {
                        after = 0.17 * size;
                    }
                }
                x += before;
                g.Add(b, x, 0);
                x += b.Width + after;
                if (atom is not null)
                {
                    prev = atom;
                }
            }
            g.Width = Math.Max(g.Width, x);
            return g;
        }

        /// <summary>The symbol that decides the spacing around <paramref name="n"/>, when it is one.</summary>
        private static TexAtom? Head(TexNode n) => n as TexAtom ?? (n as TexScript)?.Base as TexAtom;

        private Box Script(TexScript s, double size, bool display)
        {
            Box b = Make(s.Base, size, display);
            double small = size * 0.7;
            Box? sub = s.Sub is null ? null : Make(s.Sub, small, false);
            Box? sup = s.Sup is null ? null : Make(s.Sup, small, false);
            var g = new Group();
            if (display && IsLimitsOp(s.Base))
            {
                // Limits stacked above and below, centred on the operator.
                double w = Math.Max(b.Width, Math.Max(sub?.Width ?? 0, sup?.Width ?? 0));
                g.Add(b, (w - b.Width) / 2, 0);
                if (sup is not null)
                {
                    g.Add(sup, (w - sup.Width) / 2, -(b.Ascent + sup.Descent + size * 0.08));
                }
                if (sub is not null)
                {
                    g.Add(sub, (w - sub.Width) / 2, b.Descent + sub.Ascent + size * 0.08);
                }
                g.Width = w + size * 0.1;
                return g;
            }
            g.Add(b, 0, 0);
            bool integral = s.Base is TexAtom { Kind: TexKind.BigOp, Text: "∫" or "∮" };
            double x = b.Width + (s.Base is TexAtom { Kind: TexKind.BigOp } ? size * 0.06 : 0);
            // An integral's limits lean away from the sign: the upper one out, the lower one back in.
            double supShift = integral ? size * 0.28 : 0, subShift = integral ? -size * 0.22 : 0;
            double up = Math.Max(size * 0.38, b.Ascent - small * 0.55);
            double down = Math.Max(size * 0.2, b.Descent + small * 0.15);
            if (sup is not null && sub is not null)
            {
                down = Math.Max(down, up + sub.Ascent + sup.Descent > 0 ? (sub.Ascent - up + sup.Descent + size * 0.1) : down);
                down = Math.Max(down, size * 0.22);
            }
            if (sup is not null)
            {
                g.Add(sup, x + supShift, -up);
            }
            if (sub is not null)
            {
                g.Add(sub, x + subShift, down);
            }
            g.Width = x + Math.Max((sup?.Width ?? 0) + supShift, (sub?.Width ?? 0) + subShift) + size * 0.04;
            return g;
        }

        private Box Frac(TexFrac f, double size, bool display)
        {
            double inner = display ? size : size * 0.85;
            Box n = Make(f.Numerator, inner, false), d = Make(f.Denominator, inner, false);
            double pad = size * 0.12, gap = size * 0.14;
            double w = Math.Max(n.Width, d.Width) + 2 * pad;
            double axis = Axis(size);
            var g = new Group();
            g.Add(n, (w - n.Width) / 2, -(axis + gap + n.Descent));
            g.Add(d, (w - d.Width) / 2, -axis + gap + d.Ascent);
            g.Width = w;
            if (f.Rule)
            {
                Pen rule = _rule;
                var line = new Drawn((c, x, y) => c.DrawLine(rule, new Point(x + pad / 2, y - axis), new Point(x + w - pad / 2, y - axis)), w, 0, 0).Sized();
                g.Add(line, 0, 0);
            }
            g.Width = w;
            return g;
        }

        private Box Sqrt(TexSqrt q, double size, bool display)
        {
            Box body = Make(q.Body, size, display);
            Box? index = q.Index is null ? null : Make(q.Index, size * 0.55, false);
            double gap = size * 0.12, hook = size * 0.55;
            double top = body.Ascent + gap, bottom = body.Descent;
            double h = top + bottom;
            Pen rule = _rule;
            double indent = index is null ? 0 : Math.Max(0, index.Width - hook * 0.5);
            var g = new Group();
            double w = indent + hook + body.Width + size * 0.1;
            var radical = new Drawn((c, x, y) =>
            {
                double x0 = x + indent;
                var path = new StreamGeometry();
                using (StreamGeometryContext ctx = path.Open())
                {
                    ctx.BeginFigure(new Point(x0, y - h * 0.38), false);
                    ctx.LineTo(new Point(x0 + hook * 0.28, y - h * 0.42));
                    ctx.LineTo(new Point(x0 + hook * 0.55, y + bottom));
                    ctx.LineTo(new Point(x0 + hook, y - top));
                    ctx.LineTo(new Point(x0 + hook + body.Width + size * 0.08, y - top));
                    ctx.EndFigure(false);
                }
                c.DrawGeometry(null, rule, path);
            }, w, top, bottom).Sized();
            g.Add(radical, 0, 0);
            g.Add(body, indent + hook + size * 0.02, 0);
            if (index is not null)
            {
                g.Add(index, 0, -(h * 0.5));
            }
            g.Width = w;
            g.Ascent = Math.Max(g.Ascent, top + rule.Thickness);
            return g;
        }

        private static readonly HashSet<string> LimitFunctions = new(StringComparer.Ordinal) { "lim", "limsup", "liminf", "max", "min", "sup", "inf", "det", "gcd", "Pr" };

        /// <summary>Whether <paramref name="n"/> takes its limits above and below in display style: sums, products, lim, max; not integrals.</summary>
        private static bool IsLimitsOp(TexNode n) => n is TexAtom { Kind: TexKind.BigOp } a ? a.Text is not ("∫" or "∮") : n is TexAtom { Kind: TexKind.Func } f && LimitFunctions.Contains(f.Text);

        private Box Fenced(TexFenced fe, double size, bool display) => Wrap(fe.Open, Make(fe.Body, size, display), fe.Close, size);

        /// <summary><paramref name="body"/> between brackets as tall as it is.</summary>
        private Box Wrap(string openText, Box body, string closeText, double size)
        {
            Box open = Fence(openText, body, size), close = Fence(closeText, body, size);
            var g = new Group();
            g.Add(open, 0, 0);
            g.Add(body, open.Width + size * 0.04, 0);
            g.Add(close, open.Width + body.Width + size * 0.08, 0);
            g.Width = open.Width + body.Width + close.Width + size * 0.12;
            return g;
        }

        /// <summary>A bracket as tall as <paramref name="body"/> (at least one line), drawn as lines and curves so it can stretch.</summary>
        private Box Fence(string d, Box body, double size)
        {
            if (d.Length == 0)
            {
                return new Group();
            }
            double ascent = Math.Max(body.Ascent, size * 0.78), descent = Math.Max(body.Descent, size * 0.22);
            double h = ascent + descent;
            if (h <= size * 1.05 && d is not ("‖" or "{" or "}"))
            {
                return Atom(new TexAtom(d, TexKind.Fence), size); // one line tall: the font's own bracket is right
            }
            double w = d == "‖" ? size * 0.32 : size * 0.34;
            Pen rule = _rule;
            var box = new Drawn((c, x, y) =>
            {
                double top = y - ascent, bottom = y + descent, mid = (top + bottom) / 2;
                switch (d)
                {
                    case "(":
                    case ")":
                    {
                        bool left = d == "(";
                        // A cubic with both controls well out to one side gives a round arc, not a point.
                        double x1 = left ? x + w * 0.85 : x + w * 0.15, x2 = left ? x + w * 0.05 : x + w * 0.95;
                        var path = new StreamGeometry();
                        using (StreamGeometryContext ctx = path.Open())
                        {
                            ctx.BeginFigure(new Point(x1, top), false);
                            ctx.CubicBezierTo(new Point(x2, top + h * 0.28), new Point(x2, bottom - h * 0.28), new Point(x1, bottom));
                            ctx.EndFigure(false);
                        }
                        c.DrawGeometry(null, rule, path);
                        break;
                    }
                    case "[":
                    case "]":
                    case "⌊":
                    case "⌋":
                    case "⌈":
                    case "⌉":
                    {
                        bool left = d is "[" or "⌊" or "⌈";
                        double xs = left ? x + w * 0.35 : x + w * 0.65, xe = left ? x + w * 0.8 : x + w * 0.2;
                        c.DrawLine(rule, new Point(xs, top), new Point(xs, bottom));
                        if (d is "[" or "]" or "⌈" or "⌉")
                        {
                            c.DrawLine(rule, new Point(xs, top), new Point(xe, top));
                        }
                        if (d is "[" or "]" or "⌊" or "⌋")
                        {
                            c.DrawLine(rule, new Point(xs, bottom), new Point(xe, bottom));
                        }
                        break;
                    }
                    case "{":
                    case "}":
                    {
                        bool left = d == "{";
                        double X(double f) => x + w * (left ? f : 1 - f);
                        double xr = X(0.8), xm = X(0.5), xl = X(0.15), q = h * 0.12;
                        var path = new StreamGeometry();
                        using (StreamGeometryContext ctx = path.Open())
                        {
                            ctx.BeginFigure(new Point(xr, top), false);
                            ctx.QuadraticBezierTo(new Point(xm, top), new Point(xm, top + q));
                            ctx.LineTo(new Point(xm, mid - q * 0.8));
                            ctx.QuadraticBezierTo(new Point(xm, mid), new Point(xl, mid));
                            ctx.QuadraticBezierTo(new Point(xm, mid), new Point(xm, mid + q * 0.8));
                            ctx.LineTo(new Point(xm, bottom - q));
                            ctx.QuadraticBezierTo(new Point(xm, bottom), new Point(xr, bottom));
                            ctx.EndFigure(false);
                        }
                        c.DrawGeometry(null, rule, path);
                        break;
                    }
                    case "|":
                        c.DrawLine(rule, new Point(x + w / 2, top), new Point(x + w / 2, bottom));
                        break;
                    case "‖":
                        c.DrawLine(rule, new Point(x + w * 0.3, top), new Point(x + w * 0.3, bottom));
                        c.DrawLine(rule, new Point(x + w * 0.7, top), new Point(x + w * 0.7, bottom));
                        break;
                    default:
                    {
                        FormattedText t = Text(d, Math.Max(size, h * 0.95), false);
                        c.DrawText(t, new Point(x, mid - t.Height / 2));
                        break;
                    }
                }
            }, w, ascent, descent);
            return box.Sized();
        }

        private Box Matrix(TexMatrix m, double size, bool display)
        {
            int rows = m.Rows.Count, cols = rows == 0 ? 0 : m.Rows.Max(r => r.Count);
            if (rows == 0 || cols == 0)
            {
                return new Group();
            }
            var cell = new Box?[rows, cols];
            var colWidth = new double[cols];
            var rowAscent = new double[rows];
            var rowDescent = new double[rows];
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < m.Rows[r].Count; c++)
                {
                    Box b = Make(m.Rows[r][c], size, false);
                    cell[r, c] = b;
                    colWidth[c] = Math.Max(colWidth[c], b.Width);
                    rowAscent[r] = Math.Max(rowAscent[r], b.Ascent);
                    rowDescent[r] = Math.Max(rowDescent[r], b.Descent);
                }
                rowAscent[r] = Math.Max(rowAscent[r], size * 0.7); // an empty row still takes a line
                rowDescent[r] = Math.Max(rowDescent[r], size * 0.22);
            }
            double rowGap = size * 0.3;
            double height = rowAscent.Sum() + rowDescent.Sum() + rowGap * (rows - 1);
            double axis = Axis(size);
            var colX = new double[cols];
            double x = 0;
            for (int c = 0; c < cols; c++)
            {
                colX[c] = x;
                // In aligned lines a pair of columns (right, left) meets with no gap; pairs are an em apart.
                double gap = m.Columns == TexColumns.Aligned && c % 2 == 0 ? 0 : size;
                x += colWidth[c] + gap;
            }
            double width = x - (m.Columns == TexColumns.Aligned && (cols - 1) % 2 == 0 ? size : m.Columns == TexColumns.Aligned ? 0 : size);
            width = Math.Max(width, colX[^1] + colWidth[^1]);
            var g = new Group();
            double top = 0;
            for (int r = 0; r < rows; r++)
            {
                double baseline = top + rowAscent[r] - (height / 2 + axis); // relative to the grid's own baseline
                for (int c = 0; c < cols; c++)
                {
                    if (cell[r, c] is not { } b)
                    {
                        continue;
                    }
                    double dx = m.Columns switch
                    {
                        TexColumns.Left => 0,
                        TexColumns.Aligned => c % 2 == 0 ? colWidth[c] - b.Width : 0,
                        _ => (colWidth[c] - b.Width) / 2,
                    };
                    g.Add(b, colX[c] + dx, baseline);
                }
                top += rowAscent[r] + rowDescent[r] + rowGap;
            }
            g.Width = width;
            g.Ascent = Math.Max(g.Ascent, height / 2 + axis);
            g.Descent = Math.Max(g.Descent, height / 2 - axis);
            return m.Open.Length == 0 && m.Close.Length == 0 ? g : Wrap(m.Open, g, m.Close, size);
        }

        private Box Stack(TexStack st, double size, bool display)
        {
            Box body = Make(st.Body, size, display);
            double small = size * 0.7, gap = size * 0.08, brace = st.Brace ? size * 0.3 : 0;
            Box? over = st.Over is null ? null : Make(st.Over, small, false);
            Box? under = st.Under is null ? null : Make(st.Under, small, false);
            double w = Math.Max(body.Width, Math.Max(over?.Width ?? 0, under?.Width ?? 0));
            var g = new Group();
            g.Add(body, (w - body.Width) / 2, 0);
            Pen rule = _rule;
            double bodyWidth = body.Width, bx = (w - body.Width) / 2;
            if (st.Brace)
            {
                bool above = st.Over is not null || st.Under is null; // an overbrace carries its label above; with no label it is still an overbrace
                double edge = above ? body.Ascent + gap : body.Descent + gap; // distance of the brace's near edge from the baseline
                double sign = above ? -1 : 1;
                var curve = new Drawn((c, x, y) =>
                {
                    double x0 = x + bx, x1 = x0 + bodyWidth, xm = (x0 + x1) / 2;
                    double r = Math.Min(size * 0.15, bodyWidth / 4), tip = size * 0.1;
                    double near = y + sign * edge, far = y + sign * (edge + brace - tip);
                    var path = new StreamGeometry();
                    using (StreamGeometryContext ctx = path.Open())
                    {
                        ctx.BeginFigure(new Point(x0, near), false);
                        ctx.QuadraticBezierTo(new Point(x0, far), new Point(x0 + r, far));
                        ctx.LineTo(new Point(xm - r, far));
                        ctx.QuadraticBezierTo(new Point(xm, far), new Point(xm, far + sign * tip));
                        ctx.QuadraticBezierTo(new Point(xm, far), new Point(xm + r, far));
                        ctx.LineTo(new Point(x1 - r, far));
                        ctx.QuadraticBezierTo(new Point(x1, far), new Point(x1, near));
                        ctx.EndFigure(false);
                    }
                    c.DrawGeometry(null, rule, path);
                }, w, 0, 0).Sized();
                g.Add(curve, 0, 0);
                // The brace sits in the box's height too, so a label clears it.
                if (above)
                {
                    g.Ascent = Math.Max(g.Ascent, edge + brace);
                }
                else
                {
                    g.Descent = Math.Max(g.Descent, edge + brace);
                }
            }
            // A lone symbol's box has the font's full ascent, far above the glyph; labels sit on the glyph instead.
            double top = st.Body is TexAtom && !st.Brace ? Math.Min(body.Ascent, size * 0.72) : body.Ascent;
            double bottom = st.Body is TexAtom && !st.Brace ? Math.Min(body.Descent, size * 0.1) : body.Descent;
            if (over is not null)
            {
                g.Add(over, (w - over.Width) / 2, -(top + gap + brace + over.Descent + gap));
            }
            if (under is not null)
            {
                g.Add(under, (w - under.Width) / 2, bottom + gap + brace + under.Ascent + gap);
            }
            g.Width = w;
            return g;
        }

        private Box Accent(TexAccent ac, double size, bool display)
        {
            Box body = Make(ac.Body, size, display);
            Pen rule = _rule;
            double y = body.Ascent + size * 0.1;
            var g = new Group();
            g.Add(body, 0, 0);
            string mark = ac.Mark;
            double w = body.Width;
            FormattedText? t = mark is "‾" or "_" ? null : Text(mark, size * 0.8, false);
            bool below = mark == "_";
            var overlay = new Drawn((c, x, by) =>
            {
                if (below)
                {
                    c.DrawLine(rule, new Point(x, by + body.Descent + size * 0.1), new Point(x + w, by + body.Descent + size * 0.1));
                }
                else if (t is null)
                {
                    c.DrawLine(rule, new Point(x, by - y), new Point(x + w, by - y));
                }
                else
                {
                    c.DrawText(t, new Point(x + (w - t.Width) / 2, by - y - t.Baseline * 0.55 - size * 0.12));
                }
            }, w, below ? 0 : y + size * 0.1, below ? body.Descent + size * 0.15 : 0).Sized();
            g.Add(overlay, 0, 0);
            g.Width = body.Width;
            return g;
        }
    }
}
