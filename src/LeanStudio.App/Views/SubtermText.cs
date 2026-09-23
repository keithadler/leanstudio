using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using LeanStudio.App.ViewModels;
using LeanStudio.Lsp;

namespace LeanStudio.App.Views;

/// <summary>
/// Goal text you can look inside: moving the pointer highlights the smallest subterm under it (as Lean's own
/// structure delimits it), and resting there asks Lean for that subterm's type, its full form, and the
/// documentation of its head, shown in a tooltip.
/// </summary>
public sealed class SubtermText : SelectableTextBlock
{
    public static readonly StyledProperty<TaggedString?> TaggedProperty =
        AvaloniaProperty.Register<SubtermText, TaggedString?>(nameof(Tagged));

    private static readonly IBrush Highlight = new SolidColorBrush(Color.FromArgb(0x55, 0x4D, 0x9F, 0xFF));
    private TaggedSpan? _hovered;
    private CancellationTokenSource? _cts;

    public TaggedString? Tagged
    {
        get => GetValue(TaggedProperty);
        set => SetValue(TaggedProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TaggedProperty)
        {
            _hovered = null;
            Inlines = null;
            Text = Tagged?.Text ?? "";
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Tagged is not { Spans.Count: > 0 } tagged)
        {
            return;
        }
        TextHitTestResult hit = TextLayout.HitTestPoint(e.GetPosition(this) - new Point(Padding.Left, Padding.Top));
        int index = hit.TextPosition;
        TaggedSpan? span = hit.IsInside
            ? tagged.Spans.Where(s => s.Reference is not null && s.Start <= index && index < s.Start + s.Length).MinBy(s => s.Length)
            : null;
        if (span == _hovered)
        {
            return;
        }
        _hovered = span;
        Show(span);
        _cts?.Cancel();
        ToolTip.SetIsOpen(this, false);
        if (span?.Reference is string reference)
        {
            var cts = new CancellationTokenSource();
            _cts = cts;
            _ = InspectAsync(reference, cts.Token);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _cts?.Cancel();
        _hovered = null;
        Show(null);
        ToolTip.SetIsOpen(this, false);
    }

    private void Show(TaggedSpan? span)
    {
        string text = Tagged?.Text ?? "";
        if (span is null)
        {
            Inlines = null;
            Text = text;
            return;
        }
        var inlines = new InlineCollection
        {
            new Run(text[..span.Start]),
            new Run(text.Substring(span.Start, span.Length)) { Background = Highlight },
            new Run(text[(span.Start + span.Length)..]),
        };
        Inlines = inlines;
    }

    private async Task InspectAsync(string reference, CancellationToken ct)
    {
        try
        {
            await Task.Delay(300, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (this.FindAncestorOfType<InfoView>()?.DataContext is not InfoViewModel info)
        {
            return;
        }
        SubtermInfo? s = await info.InspectAsync(reference, ct);
        if (s is null || ct.IsCancellationRequested)
        {
            return;
        }
        var panel = new StackPanel { Spacing = 4, MaxWidth = 560 };
        string term = s.Explicit ?? (_hovered is { } h && Tagged is { } t ? t.Text.Substring(h.Start, h.Length) : "");
        panel.Children.Add(new SelectableTextBlock
        {
            Text = s.Type is null ? term : $"{term} : {s.Type}",
            FontFamily = FontFamily,
            FontSize = FontSize,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(s.Doc))
        {
            string doc = s.Doc.Trim();
            panel.Children.Add(new TextBlock
            {
                Text = doc.Length > 500 ? doc[..500] + " …" : doc,
                TextWrapping = TextWrapping.Wrap,
                FontSize = FontSize - 1,
                Opacity = 0.8,
            });
        }
        ToolTip.SetTip(this, panel);
        ToolTip.SetPlacement(this, PlacementMode.Pointer);
        ToolTip.SetIsOpen(this, true);
    }
}
