using AvaloniaEdit.Document;
using AvaloniaEdit.Indentation;
using LeanStudio.Core.Editing;

namespace LeanStudio.App.Editor;

/// <summary>On Enter, indent like the line above, and two spaces more after a line that opens a block (`:= by`, `where`, `=>`…).</summary>
public sealed class LeanIndentationStrategy : IIndentationStrategy
{
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

    public void IndentLines(TextDocument document, int beginLine, int endLine)
    {
    }
}
