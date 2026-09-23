using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LeanStudio.Core.Verification;

namespace LeanStudio.App.Views;

/// <summary>
/// The project map, drawn: one box per declaration, in columns by level (what uses nothing in the project on
/// the left, what builds on it further right), an arc for each use, coloured by status. Scroll to zoom, drag to
/// pan, click a declaration to open it; the selected one's uses and users are highlighted.
/// </summary>
public sealed class ProjectMapView : Control
{
    private const double RowHeight = 34, BoxHeight = 24, ColumnGap = 70, Pad = 24;

    private ProjectMap? _map;
    private Rect[] _boxes = [];
    private FormattedText[] _labels = [];
    private Size _extent;
    private double _scale = 1;
    private Vector _offset;
    private Point? _dragFrom;
    private bool _dragged;
    private int _selected = -1, _hover = -1;

    /// <summary>A declaration was clicked.</summary>
    public event Action<MapNode>? NodeClicked;

    public ProjectMapView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public ProjectMap? Map
    {
        get => _map;
        set
        {
            _map = value;
            _selected = _hover = -1;
            Layout();
            Fit();
            InvalidateVisual();
        }
    }

    /// <summary>Select a declaration by name and bring it into view.</summary>
    public void Select(string name)
    {
        if (_map is null)
        {
            return;
        }
        for (int i = 0; i < _map.Nodes.Count; i++)
        {
            if (_map.Nodes[i].Name == name)
            {
                _selected = i;
                Rect b = _boxes[i];
                _offset = new Vector(Bounds.Width / 2 - b.Center.X * _scale, Bounds.Height / 2 - b.Center.Y * _scale);
                InvalidateVisual();
                return;
            }
        }
    }

    private static readonly Typeface Face = new("JuliaMono, Cascadia Code, SF Mono, Menlo, Consolas, monospace");

    private void Layout()
    {
        if (_map is null || _map.Nodes.Count == 0)
        {
            _boxes = [];
            _labels = [];
            _extent = default;
            return;
        }
        int n = _map.Nodes.Count;
        _labels = _map.Nodes.Select(x => new FormattedText(x.Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 12, Brushes.White)).ToArray();
        var uses = new List<int>[n];
        for (int i = 0; i < n; i++)
        {
            uses[i] = [];
        }
        foreach ((int from, int to) in _map.Edges)
        {
            uses[from].Add(to);
        }
        // Columns by level; within a column, order by where what each uses sits (barycentre), to untangle arcs.
        var columns = _map.Nodes.Select((x, i) => (x.Level, i)).GroupBy(t => t.Level).OrderBy(g => g.Key).Select(g => g.Select(t => t.i).ToList()).ToList();
        var row = new double[n];
        foreach (List<int> col in columns)
        {
            var order = col
                .Select(i => (i, key: uses[i].Count == 0 ? 0 : uses[i].Average(j => row[j])))
                .OrderBy(t => t.key)
                .ThenBy(t => _map.Nodes[t.i].Module, StringComparer.Ordinal)
                .ThenBy(t => _map.Nodes[t.i].Line ?? 0)
                .Select(t => t.i).ToList();
            for (int r = 0; r < order.Count; r++)
            {
                row[order[r]] = r;
            }
        }
        _boxes = new Rect[n];
        double x = Pad, height = 0;
        foreach (List<int> col in columns)
        {
            double width = col.Max(i => _labels[i].Width) + 20;
            foreach (int i in col)
            {
                _boxes[i] = new Rect(x, Pad + row[i] * RowHeight, width, BoxHeight);
                height = Math.Max(height, _boxes[i].Bottom);
            }
            x += width + ColumnGap;
        }
        _extent = new Size(x - ColumnGap + Pad, height + Pad);
    }

    private void Fit()
    {
        if (_extent.Width <= 0 || Bounds.Width <= 0)
        {
            _scale = 1;
            _offset = default;
            return;
        }
        _scale = Math.Clamp(Math.Min(Bounds.Width / _extent.Width, Bounds.Height / _extent.Height), 0.25, 1.4);
        _offset = new Vector((Bounds.Width - _extent.Width * _scale) / 2, Math.Max(0, (Bounds.Height - _extent.Height * _scale) / 2));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        bool first = Bounds.Width <= 0;
        Size s = base.ArrangeOverride(finalSize);
        if (first)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { Fit(); InvalidateVisual(); });
        }
        return s;
    }

    private static IBrush Fill(MapStatus s) => s switch
    {
        MapStatus.Proved => new SolidColorBrush(Color.FromArgb(0x40, 0x3F, 0xB9, 0x50)),
        MapStatus.RestsOnSorry => new SolidColorBrush(Color.FromArgb(0x48, 0xE5, 0x9E, 0x2C)),
        MapStatus.Axiom => new SolidColorBrush(Color.FromArgb(0x55, 0xB4, 0x8E, 0xAD)),
        _ => new SolidColorBrush(Color.FromArgb(0x33, 0xB4, 0x8E, 0xAD)),
    };

    private static Color Edge(MapStatus s) => s switch
    {
        MapStatus.Proved => Color.FromRgb(0x3F, 0xB9, 0x50),
        MapStatus.RestsOnSorry => Color.FromRgb(0xE5, 0x9E, 0x2C),
        _ => Color.FromRgb(0xB4, 0x8E, 0xAD),
    };

    public override void Render(DrawingContext context)
    {
        IBrush background = this.TryFindResource("PanelBackground", ActualThemeVariant, out object? bg) && bg is IBrush b ? b : Brushes.Black;
        IBrush text = this.TryFindResource("SystemControlForegroundBaseHighBrush", ActualThemeVariant, out object? fg) && fg is IBrush f ? f : Brushes.White;
        context.FillRectangle(background, new Rect(Bounds.Size));
        if (_map is null || _boxes.Length == 0)
        {
            return;
        }
        int focus = _hover >= 0 ? _hover : _selected;
        using (context.PushTransform(Matrix.CreateScale(_scale, _scale) * Matrix.CreateTranslation(_offset.X, _offset.Y)))
        {
            var dim = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0x8A, 0x90, 0x9C)), 1);
            foreach ((int from, int to) in _map.Edges)
            {
                bool lit = focus >= 0 && (from == focus || to == focus);
                if (focus >= 0 && !lit)
                {
                    continue;
                }
                DrawArc(context, _boxes[to], _boxes[from], dim);
            }
            if (focus >= 0)
            {
                // The focused declaration's arcs on top: what it uses in its colour, what uses it in the accent.
                foreach ((int from, int to) in _map.Edges.Where(e => e.From == focus || e.To == focus))
                {
                    var pen = new Pen(new SolidColorBrush(from == focus ? Edge(_map.Nodes[to].Status) : Color.FromRgb(0x6F, 0xB3, 0xFF)), 2);
                    DrawArc(context, _boxes[to], _boxes[from], pen);
                }
            }
            for (int i = 0; i < _boxes.Length; i++)
            {
                MapNode node = _map.Nodes[i];
                Rect r = _boxes[i];
                bool faded = focus >= 0 && i != focus && !_map.Edges.Any(e => (e.From == focus && e.To == i) || (e.To == focus && e.From == i));
                using (context.PushOpacity(faded ? 0.35 : 1))
                {
                    var border = new Pen(new SolidColorBrush(Edge(node.Status)), i == focus ? 2.5 : node.IsSource ? 2 : 1);
                    context.DrawRectangle(Fill(node.Status), border, r, 5, 5);
                    _labels[i].SetForegroundBrush(text);
                    context.DrawText(_labels[i], new Point(r.X + 10, r.Y + (BoxHeight - _labels[i].Height) / 2));
                    if (node.IsSource && node.Status != MapStatus.Proved)
                    {
                        context.DrawEllipse(new SolidColorBrush(Edge(node.Status)), null, new Point(r.Right - 1, r.Top + 1), 4, 4);
                    }
                }
            }
        }
    }

    private static void DrawArc(DrawingContext context, Rect used, Rect user, Pen pen)
    {
        var a = new Point(used.Right, used.Center.Y);
        var z = new Point(user.Left, user.Center.Y);
        double dx = Math.Max(30, (z.X - a.X) / 2);
        var geo = new StreamGeometry();
        using (StreamGeometryContext g = geo.Open())
        {
            g.BeginFigure(a, false);
            g.CubicBezierTo(new Point(a.X + dx, a.Y), new Point(z.X - dx, z.Y), z);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geo);
    }

    private int HitTest(Point p)
    {
        var world = new Point((p.X - _offset.X) / _scale, (p.Y - _offset.Y) / _scale);
        for (int i = 0; i < _boxes.Length; i++)
        {
            if (_boxes[i].Contains(world))
            {
                return i;
            }
        }
        return -1;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Point p = e.GetPosition(this);
        double factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        double next = Math.Clamp(_scale * factor, 0.15, 3);
        // Zoom about the pointer, so what is under it stays there.
        _offset = new Vector(p.X - (p.X - _offset.X) * next / _scale, p.Y - (p.Y - _offset.Y) * next / _scale);
        _scale = next;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragFrom = e.GetPosition(this);
        _dragged = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point p = e.GetPosition(this);
        if (_dragFrom is Point from)
        {
            Vector d = p - from;
            if (_dragged || Math.Abs(d.X) + Math.Abs(d.Y) > 4)
            {
                _dragged = true;
                _offset += d;
                _dragFrom = p;
                InvalidateVisual();
            }
            return;
        }
        int h = HitTest(p);
        if (h != _hover)
        {
            _hover = h;
            if (_map is not null && h >= 0)
            {
                MapNode n = _map.Nodes[h];
                ToolTip.SetTip(this, $"{n.Kind} {n.Name}\n{n.Module}" + (n.Line is int l ? $", line {l}" : "")
                    + $"\n{Describe(n)}" + (n.UsedBy > 0 ? $"\n{n.UsedBy} declaration{(n.UsedBy == 1 ? "" : "s")} in the project rest on it" : ""));
                ToolTip.SetIsOpen(this, true);
            }
            else
            {
                ToolTip.SetIsOpen(this, false);
            }
            Cursor = h >= 0 ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            InvalidateVisual();
        }
    }

    public static string Describe(MapNode n) => n.Status switch
    {
        MapStatus.Proved => "✓ fully proved",
        MapStatus.RestsOnSorry => n.IsSource ? "◐ uses sorry itself" : "◐ rests on a sorry further down",
        MapStatus.Axiom => "an axiom the project introduces",
        _ => "◐ rests on an axiom the project introduces",
    };

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        bool click = !_dragged;
        _dragFrom = null;
        if (click && _map is not null)
        {
            int h = HitTest(e.GetPosition(this));
            _selected = h;
            InvalidateVisual();
            if (h >= 0)
            {
                NodeClicked?.Invoke(_map.Nodes[h]);
            }
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = -1;
        ToolTip.SetIsOpen(this, false);
        InvalidateVisual();
    }
}
