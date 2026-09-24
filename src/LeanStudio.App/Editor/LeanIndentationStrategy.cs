using AvaloniaEdit.Document;
using AvaloniaEdit.Indentation;
using LeanStudio.Core.Editing;

namespace LeanStudio.App.Editor;

/// <summary>On Enter, indent like the line above, and two spaces more after a line that opens a block (`:= by`, `where`, `=>`…).</summary>
public sealed class LeanIndentationStrategy : IIndentationStrategy
{
    /// <summary>Replace the leading whitespace of <paramref name="line"/> with the indentation <see cref="LeanText.IndentAfter"/> gives for the line above.</summary>
    public void IndentLine(TextDocument document, DocumentLine line)
    {
        DocumentLine? previous = line.PreviousLine;
        if (previous is null)
        {
            return;
        }
        string indent = LeanText.IndentAfter(document.GetText(previous));
        ISegment existing = TextUtilities.GetWhitespaceAfter(document, line.Offset);
        document.Replace(existing.Offset, existing.Length, indent, OffsetChangeMappingType.RemoveAndInsert);
    }

    /// <summary>Does nothing: only the new line after Enter is indented, never a range of existing lines.</summary>
    public void IndentLines(TextDocument document, int beginLine, int endLine)
    {
    }
}
