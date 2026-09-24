using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using LeanStudio.Lsp;

namespace LeanStudio.App.Editor;

/// <summary>One completion Lean offered.</summary>
/// <param name="item">The completion item from the Lean server.</param>
/// <param name="priority">The item's rank in the list; higher is preferred.</param>
public sealed class LeanCompletionData(CompletionItem item, double priority) : ICompletionData
{
    /// <summary>No icon: always null.</summary>
    public IImage? Image => null;
    /// <summary>The item's label, which the completion list filters on.</summary>
    public string Text => item.Label;
    /// <summary>What the list shows: the label and its type, shortened to one line of at most 80 characters.</summary>
    public object Content => item.Detail is null ? item.Label : $"{item.Label}  :  {Shorten(item.Detail)}";
    /// <summary>The tooltip: the item's full type, then its documentation when it has any.</summary>
    public object Description => string.IsNullOrWhiteSpace(item.Documentation) ? item.Detail ?? "" : item.Detail + "\n\n" + item.Documentation;
    /// <inheritdoc/>
    public double Priority => priority;

    /// <summary>Replace the typed prefix with the item's insert text, or its label when it has none.</summary>
    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, item.InsertText ?? item.Label);

    private static string Shorten(string s)
    {
        s = s.Replace('\n', ' ');
        return s.Length > 80 ? s[..80] + "…" : s;
    }
}

/// <summary>A Unicode abbreviation offered while one is being typed (after a backslash).</summary>
/// <param name="abbreviation">The abbreviation, without the backslash.</param>
/// <param name="symbol">The symbol it stands for.</param>
/// <param name="priority">The item's rank in the list; higher is preferred.</param>
public sealed class AbbreviationCompletionData(string abbreviation, string symbol, int priority) : ICompletionData
{
    /// <summary>No icon: always null.</summary>
    public IImage? Image => null;
    /// <summary>The abbreviation, without the backslash, which the completion list filters on.</summary>
    public string Text => abbreviation;
    /// <summary>What the list shows: the symbol, then the abbreviation with its backslash.</summary>
    public object Content => $"{symbol}   \\{abbreviation}";
    /// <summary>The tooltip: <c>\abbreviation → symbol</c>.</summary>
    public object Description => $"\\{abbreviation} → {symbol}";
    /// <inheritdoc/>
    public double Priority => priority;

    /// <summary>Replace the backslash and the typed abbreviation with the symbol.</summary>
    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        // The segment starts after the backslash; replace the backslash too.
        int start = Math.Max(0, completionSegment.Offset - 1);
        textArea.Document.Replace(start, completionSegment.EndOffset - start, symbol);
    }
}
