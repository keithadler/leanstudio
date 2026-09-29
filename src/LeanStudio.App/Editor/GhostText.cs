using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Rendering;

namespace LeanStudio.App.Editor;

/// <summary>
/// AI completion as you type, drawn: the suggestion as grey text at the cursor (a suggestion of several lines makes
/// its line taller, pushing the lines below down, so nothing is covered). Tab takes it; typing, moving or Esc drops it.
/// </summary>
public sealed class GhostText : VisualLineElementGenerator
{
    /// <summary>Where the suggestion is, and what it says; null when there is none.</summary>
    public (int Offset, string Text, bool Checked)? Suggestion { get; private set; }

    /// <summary>The editor's font.</summary>
    public FontFamily FontFamily { get; set; } = FontFamily.Default;

    /// <summary>The editor's font size.</summary>
    public double FontSize { get; set; } = 14;

    /// <summary>Show <paramref name="text"/> at <paramref name="offset"/>; <paramref name="checkedByLean"/> says so in its tooltip.</summary>
    public void Show(int offset, string text, bool checkedByLean) => Suggestion = (offset, text, checkedByLean);

    /// <summary>Show nothing.</summary>
    public void Clear() => Suggestion = null;

    /// <inheritdoc/>
    public override int GetFirstInterestedOffset(int startOffset) =>
        Suggestion is { } s && s.Offset >= startOffset ? s.Offset : -1;

    /// <inheritdoc/>
    public override VisualLineElement? ConstructElement(int offset)
    {
        if (Suggestion is not { } s || s.Offset != offset)
        {
            return null;
        }
        var block = new TextBlock
        {
            Text = s.Text,
            FontFamily = FontFamily,
            FontSize = FontSize,
            FontStyle = FontStyle.Italic,
            Opacity = 0.45,
        };
        ToolTip.SetTip(block, s.Checked ? "AI suggestion, checked by Lean. Tab takes it, Esc drops it." : "AI suggestion. Tab takes it, Esc drops it.");
        return new InlineObjectElement(0, block);
    }
}
