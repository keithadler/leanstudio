using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using LeanStudio.Core.Editing;

namespace LeanStudio.App.Editor;

/// <summary>
/// Sticky scroll: while the start of a namespace, section or declaration is scrolled out of view, its first line
/// stays pinned at the top of the editor, outermost first, so it is always clear what the lines below belong to.
/// Click a pinned line to go to it.
/// </summary>
public sealed class StickyScroll : Control
{
    private readonly TextEditor _editor;
    private readonly Func<LeanScopes.ScopeIndex?> _scopes;
    private IReadOnlyList<LeanScope> _shown = [];

    /// <summary>Pin the scopes of <paramref name="editor"/>'s text, read from <paramref name="scopes"/> (null when the file is not Lean).</summary>
    public StickyScroll(TextEditor editor, Func<LeanScopes.ScopeIndex?> scopes)
    {
        _editor = editor;
        _scopes = scopes;
        VerticalAlignment = VerticalAlignment.Top;
        ClipToBounds = true;
        Cursor = new Avalonia.Input.Cursor(StandardCursorType.Hand);
        IsVisible = false;
    }

    /// <summary>Pin scopes at all (a setting).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The pinned scopes, outermost first.</summary>
    public IReadOnlyList<LeanScope> Shown => _shown;

    /// <summary>A pinned line was clicked: go to its 0-based line.</summary>
    public event Action<int>? LineClicked;

    /// <summary>Work out what to pin for the lines now shown; call when the editor scrolls or its text changes.</summary>
    public void Update()
    {
        TextView view = _editor.TextArea.TextView;
        IReadOnlyList<LeanScope> scopes = [];
        if (Enabled && _scopes() is LeanScopes.ScopeIndex index && view.Document is not null && view.ScrollOffset.Y > 0)
        {
            int top = view.GetDocumentLineByVisualTop(view.ScrollOffset.Y)?.LineNumber - 1 ?? 0;
            // The pinned lines cover the lines under them: what is pinned is for the first line still visible.
            scopes = index.Sticky(top);
            scopes = index.Sticky(top + scopes.Count);
        }
        if (!scopes.SequenceEqual(_shown))
        {
            _shown = scopes;
            IsVisible = scopes.Count > 0;
            Height = scopes.Count * view.DefaultLineHeight + 1;
            InvalidateVisual();
        }
    }

    /// <inheritdoc/>
    public override void Render(DrawingContext context)
    {
        if (_shown.Count == 0 || _scopes() is not LeanScopes.ScopeIndex index)
        {
            return;
        }
        TextView view = _editor.TextArea.TextView;
        IBrush background = _editor.Background ?? Brushes.Black;
        IBrush foreground = _editor.Foreground ?? Brushes.White;
        IBrush numbers = _editor.LineNumbersForeground ?? foreground;
        double lineHeight = view.DefaultLineHeight;
        context.FillRectangle(background, new Rect(Bounds.Size));
        // Where the text starts: after the gutter, less any horizontal scroll.
        double textX = (view.TranslatePoint(new Point(0, 0), this)?.X ?? 0) - view.ScrollOffset.X;
        var face = new Typeface(_editor.FontFamily);
        for (int i = 0; i < _shown.Count; i++)
        {
            double y = i * lineHeight;
            string number = (_shown[i].Line + 1).ToString(CultureInfo.InvariantCulture);
            var n = new FormattedText(number, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, _editor.FontSize, numbers);
            var t = new FormattedText(index.Line(_shown[i].Line), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, _editor.FontSize, foreground);
            using (context.PushClip(new Rect(Math.Max(0, textX), y, Math.Max(0, Bounds.Width - textX), lineHeight)))
            {
                context.DrawText(t, new Point(textX, y + (lineHeight - t.Height) / 2));
            }
            if (_editor.ShowLineNumbers)
            {
                context.DrawText(n, new Point(Math.Max(0, (view.TranslatePoint(new Point(0, 0), this)?.X ?? 0) - n.Width - 22), y + (lineHeight - n.Height) / 2));
            }
        }
        // A hairline and a soft shadow under the pinned lines, so they read as sitting above the text.
        double bottom = _shown.Count * lineHeight;
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80)), new Rect(0, bottom, Bounds.Width, 1));
    }

    /// <inheritdoc/>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        int row = (int)(e.GetPosition(this).Y / _editor.TextArea.TextView.DefaultLineHeight);
        if (row >= 0 && row < _shown.Count)
        {
            LineClicked?.Invoke(_shown[row].Line);
            e.Handled = true;
        }
    }
}

/// <summary>
/// Breadcrumbs above the editor: the file's folders and name, then the namespace, sections and declaration the
/// cursor is in. Click a namespace, section or declaration to go to its first line.
/// </summary>
public sealed class Breadcrumbs : Border
{
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private string _key = "";

    /// <summary>An empty bar; <see cref="Update"/> fills it.</summary>
    public Breadcrumbs()
    {
        Child = new ScrollViewer
        {
            Content = _row,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        Padding = new Thickness(10, 3);
        MinHeight = 24;
    }

    /// <summary>A scope was clicked: go to its 0-based line.</summary>
    public event Action<int>? LineClicked;

    /// <summary>What the bar says, segment by segment (for checks).</summary>
    public IReadOnlyList<string> Segments { get; private set; } = [];

    /// <summary>Show <paramref name="path"/> (relative to <paramref name="root"/> when inside it) and <paramref name="scopes"/>.</summary>
    public void Update(string? path, string? root, IReadOnlyList<LeanScope> scopes)
    {
        string rel = path is null ? "" : root is not null && Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? Path.GetRelativePath(root, path) : Path.GetFileName(path);
        string key = rel + "\u0001" + string.Join("\u0001", scopes.Select(s => $"{s.Kind}:{s.Name}:{s.Line}"));
        if (key == _key)
        {
            return;
        }
        _key = key;
        _row.Children.Clear();
        var segments = new List<string>();
        string[] parts = rel.Length == 0 ? [] : rel.Split(Path.DirectorySeparatorChar, '/');
        for (int i = 0; i < parts.Length; i++)
        {
            Add(parts[i], null, i == parts.Length - 1 ? "file" : "folder");
            segments.Add(parts[i]);
        }
        foreach (LeanScope s in scopes)
        {
            Add(s.Name, s.Line, s.Kind switch
            {
                ScopeKind.Namespace => "namespace",
                ScopeKind.Section => "section",
                ScopeKind.Mutual => "mutual",
                _ => "declaration",
            });
            segments.Add(s.Name);
        }
        Segments = segments;
    }

    private void Add(string text, int? line, string kind)
    {
        if (_row.Children.Count > 0)
        {
            _row.Children.Add(new TextBlock { Text = "›", Opacity = 0.45, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0) });
        }
        string glyph = kind switch
        {
            "namespace" => "{} ",
            "section" => "§ ",
            "mutual" => "⇄ ",
            "declaration" => "ƒ ",
            _ => "",
        };
        var label = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = kind is "folder" ? 0.6 : 0.9,
            FontWeight = kind == "declaration" ? FontWeight.SemiBold : FontWeight.Normal,
        };
        if (glyph.Length > 0)
        {
            label.Inlines = [new Run(glyph) { Foreground = Application.Current?.FindResource("GoalAccent") as IBrush }, new Run(text)];
        }
        else
        {
            label.Text = text;
        }
        if (line is int l)
        {
            var b = new Button { Content = label, Classes = { "crumb" }, Padding = new Thickness(4, 0), MinHeight = 18, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            ToolTip.SetTip(b, $"Go to line {l + 1}");
            b.Click += (_, _) => LineClicked?.Invoke(l);
            _row.Children.Add(b);
        }
        else
        {
            label.Margin = new Thickness(4, 0);
            _row.Children.Add(label);
        }
    }
}
