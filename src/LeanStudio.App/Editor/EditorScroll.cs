using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using AvaloniaEdit;

namespace LeanStudio.App.Editor;

/// <summary>
/// Scrolling a text editor to a pixel offset. AvaloniaEdit's own <c>ScrollToVerticalOffset</c> and
/// <c>ScrollToHorizontalOffset</c> do nothing in the version used, so these set the offset of the scroll viewer
/// inside the editor, which does.
/// </summary>
public static class EditorScroll
{
    /// <summary>Scroll <paramref name="editor"/> to <paramref name="y"/> pixels down (and <paramref name="x"/> across, when given).</summary>
    public static void ScrollToOffset(this TextEditor editor, double y, double? x = null)
    {
        if (editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is ScrollViewer sv)
        {
            sv.Offset = new Vector(Math.Max(0, x ?? sv.Offset.X), Math.Max(0, y));
        }
    }
}
