using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LeanStudio.Core.Editing;

namespace LeanStudio.App.Views;

/// <summary>
/// A Markdown preview drawn with the app's own controls (no browser): headings, paragraphs with bold, italics, code
/// and links, code blocks, quotes, lists with task boxes, tables, local images and rules. Links are raised as
/// <see cref="LinkClicked"/>; images are read only from disk (a remote image shows as a link), so a preview never
/// reaches the network by itself.
/// </summary>
public sealed class MarkdownView : UserControl
{
    private static readonly FontFamily Mono = new("JuliaMono, Cascadia Code, SF Mono, Menlo, Consolas, monospace");
    private readonly StackPanel _body = new() { Spacing = 10, Margin = new Thickness(18, 14) };

    /// <summary>Defines <see cref="Blocks"/>.</summary>
    public static readonly StyledProperty<IReadOnlyList<MdBlock>?> BlocksProperty =
        AvaloniaProperty.Register<MarkdownView, IReadOnlyList<MdBlock>?>(nameof(Blocks));

    /// <summary>Defines <see cref="BaseFolder"/>.</summary>
    public static readonly StyledProperty<string?> BaseFolderProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(BaseFolder));

    /// <summary>An empty preview.</summary>
    public MarkdownView() => Content = new ScrollViewer { Content = _body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

    /// <summary>The document to show.</summary>
    public IReadOnlyList<MdBlock>? Blocks
    {
        get => GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    /// <summary>The Markdown file's folder, which relative image paths and links are read from.</summary>
    public string? BaseFolder
    {
        get => GetValue(BaseFolderProperty);
        set => SetValue(BaseFolderProperty, value);
    }

    /// <summary>A link was clicked, with its address as written.</summary>
    public event Action<string>? LinkClicked;

    /// <summary>How many controls the preview is made of (for checks).</summary>
    public int ElementCount => _body.GetLogicalDescendants().Count();

    /// <inheritdoc/>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BlocksProperty || change.Property == BaseFolderProperty)
        {
            _body.Children.Clear();
            foreach (MdBlock b in Blocks ?? [])
            {
                _body.Children.Add(Render(b));
            }
        }
    }

    private Control Render(MdBlock block) => block switch
    {
        MdHeading h => Text(h.Spans, h.Level switch { 1 => 24, 2 => 20, 3 => 17, 4 => 15, _ => 14 }, FontWeight.SemiBold, h.Level <= 2 ? new Thickness(0, 6, 0, 0) : default),
        MdParagraph p => Text(p.Spans, 13.5),
        MdCode c => new Border
        {
            Background = Brush("CardBackground"),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(12, 8),
            Child = new SelectableTextBlock { Text = c.Text, FontFamily = Mono, FontSize = 12.5, TextWrapping = TextWrapping.NoWrap },
        },
        MdQuote q => new Border
        {
            BorderBrush = Brush("GoalAccent"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 2, 0, 2),
            Opacity = 0.85,
            Child = Stack(q.Blocks),
        },
        MdList l => List(l),
        MdTable t => Table(t),
        MdImage i => Image(i),
        MdRule => new Border { Height = 1, Background = Brush("Divider"), Margin = new Thickness(0, 4) },
        _ => new TextBlock(),
    };

    private StackPanel Stack(IReadOnlyList<MdBlock> blocks)
    {
        var s = new StackPanel { Spacing = 6 };
        foreach (MdBlock b in blocks)
        {
            s.Children.Add(Render(b));
        }
        return s;
    }

    private Control List(MdList l)
    {
        var s = new StackPanel { Spacing = 4 };
        int n = l.Start;
        foreach ((bool? done, IReadOnlyList<MdBlock> blocks) in l.Items)
        {
            string mark = done is bool d ? (d ? "☑" : "☐") : l.Ordered ? $"{n}." : "•";
            n++;
            var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,*") };
            row.Children.Add(new TextBlock { Text = mark, MinWidth = 22, FontSize = 13.5, Opacity = 0.8, Margin = new Thickness(0, 0, 6, 0) });
            StackPanel content = Stack(blocks);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            s.Children.Add(row);
        }
        return s;
    }

    private Control Table(MdTable t)
    {
        int cols = Math.Max(t.Header.Count, t.Rows.Select(r => r.Count).DefaultIfEmpty(0).Max());
        var grid = new Grid();
        for (int c = 0; c < cols; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }
        var all = new List<(IReadOnlyList<IReadOnlyList<MdSpan>> Cells, bool Header)>();
        if (t.Header.Count > 0)
        {
            all.Add((t.Header, true));
        }
        all.AddRange(t.Rows.Select(r => (r, false)));
        for (int r = 0; r < all.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < all[r].Cells.Count; c++)
            {
                var cell = new Border
                {
                    BorderBrush = Brush("Divider"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(8, 4),
                    Child = Text(all[r].Cells[c], 13, all[r].Header ? FontWeight.SemiBold : FontWeight.Normal),
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }
        return new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    private Control Image(MdImage i)
    {
        string? file = LocalFile(i.Url);
        if (file is not null)
        {
            try
            {
                return new Image { Source = new Bitmap(file), MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform };
            }
            catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
            {
            }
        }
        return Text([new MdSpan("🖼 " + (i.Alt.Length > 0 ? i.Alt : i.Url), Link: i.Url)], 13);
    }

    /// <summary>A local file an address names (relative to <see cref="BaseFolder"/>), if it exists; never a web address.</summary>
    private string? LocalFile(string url)
    {
        if (url.Contains("://", StringComparison.Ordinal) || BaseFolder is null)
        {
            return null;
        }
        string path = Path.GetFullPath(Path.Combine(BaseFolder, Uri.UnescapeDataString(url.Split('#', '?')[0])));
        return File.Exists(path) ? path : null;
    }

    private TextBlock Text(IReadOnlyList<MdSpan> spans, double size, FontWeight weight = FontWeight.Normal, Thickness margin = default)
    {
        var tb = new TextBlock { FontSize = size, FontWeight = weight, TextWrapping = TextWrapping.Wrap, Margin = margin };
        var inlines = new InlineCollection();
        foreach (MdSpan s in spans)
        {
            if (s.Link is string url)
            {
                var link = new HyperlinkButton
                {
                    Content = new TextBlock { Text = s.Text, FontSize = size, FontWeight = s.Bold ? FontWeight.SemiBold : weight },
                    Padding = default,
                    Margin = default,
                    VerticalAlignment = VerticalAlignment.Bottom,
                };
                ToolTip.SetTip(link, url);
                link.Click += (_, _) => LinkClicked?.Invoke(url);
                inlines.Add(new InlineUIContainer(link));
                continue;
            }
            var run = new Run(s.Text)
            {
                FontWeight = s.Bold ? FontWeight.SemiBold : weight,
                FontStyle = s.Italic ? FontStyle.Italic : FontStyle.Normal,
                TextDecorations = s.Strike ? TextDecorations.Strikethrough : null,
            };
            if (s.Code)
            {
                run.FontFamily = Mono;
                run.FontSize = size * 0.92;
                run.Background = Brush("CardBackground");
            }
            inlines.Add(run);
        }
        tb.Inlines = inlines;
        return tb;
    }

    private IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out object? r) ? r as IBrush : null;
}
