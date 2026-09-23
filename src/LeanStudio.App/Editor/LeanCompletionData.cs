using Avalonia.Media;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using LeanStudio.Lsp;

namespace LeanStudio.App.Editor;

/// <summary>One completion Lean offered.</summary>
public sealed class LeanCompletionData(CompletionItem item, double priority) : ICompletionData
{
    public IImage? Image => null;
    public string Text => item.Label;
    public object Content => item.Detail is null ? item.Label : $"{item.Label}  :  {Shorten(item.Detail)}";
    public object Description => string.IsNullOrWhiteSpace(item.Documentation) ? item.Detail ?? "" : item.Detail + "\n\n" + item.Documentation;
    public double Priority => priority;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
        textArea.Document.Replace(completionSegment, item.InsertText ?? item.Label);

    private static string Shorten(string s)
    {
        s = s.Replace('\n', ' ');
        return s.Length > 80 ? s[..80] + "…" : s;
    }
}

/// <summary>A Unicode abbreviation offered while one is being typed (after a backslash).</summary>
public sealed class AbbreviationCompletionData(string abbreviation, string symbol, int priority) : ICompletionData
{
    public IImage? Image => null;
    public string Text => abbreviation;
    public object Content => $"{symbol}   \\{abbreviation}";
    public object Description => $"\\{abbreviation} → {symbol}";
    public double Priority => priority;

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        // The segment starts after the backslash; replace the backslash too.
        int start = Math.Max(0, completionSegment.Offset - 1);
        textArea.Document.Replace(start, completionSegment.EndOffset - start, symbol);
    }
}
