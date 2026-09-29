using System.Text;
using LeanStudio.Core.Proofs;
using LeanStudio.Lsp;

namespace LeanStudio.Core.Ai;

/// <summary>A suggestion to show as grey text at the cursor.</summary>
/// <param name="Offset">Where it goes in the text it was made for.</param>
/// <param name="Text">What to insert.</param>
/// <param name="CheckedByLean">Lean checked the file with it inserted and found no error in it.</param>
public sealed record InlineSuggestion(int Offset, string Text, bool CheckedByLean);

/// <summary>
/// AI completion as you type: the model is shown the code around the cursor and asked for what comes next; its
/// answer is cleaned up (fences, echoes of the line, overlap with what follows) and, in a Lean file, checked by Lean
/// with the suggestion in place before it is offered, so a suggestion that does not elaborate is never shown.
/// </summary>
public static class InlineCompletion
{
    /// <summary>How many lines a suggestion may have at most.</summary>
    public const int MaxLines = 8;

    /// <summary>What is sent to the model: the code before and after the cursor, trimmed to fit its context.</summary>
    public static IReadOnlyList<ChatMessage> Prompt(string text, int offset, string fileName, int contextTokens)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int budget = Math.Max(256, contextTokens - 400);
        string before = AiText.Fit(text[..offset], budget * 3 / 4, FitKeep.End);
        string after = AiText.Fit(text[offset..], budget / 4, FitKeep.Start);
        bool lean = fileName.EndsWith(".lean", StringComparison.Ordinal);
        string system = (lean
                ? "You complete Lean 4 code (with Mathlib where it is imported). "
                : "You complete code. ")
            + "The user shows a file with <CURSOR> where the cursor is. Reply with only the text to insert at <CURSOR>: "
            + "no explanation, no code fences, nothing that is already before or after it. Keep it short: finish the "
            + "current line, or add the next few lines of the same proof or definition, at most " + MaxLines + " lines.";
        return [ChatMessage.System(system), ChatMessage.User($"File {fileName}:\n{before}<CURSOR>{after}")];
    }

    /// <summary>
    /// A model's answer as text to insert at the cursor, or empty: without thinking, fences or <c>&lt;CURSOR&gt;</c>,
    /// without an echo of the current line's start, stopped before it repeats what follows the cursor, and at most
    /// <see cref="MaxLines"/> lines.
    /// </summary>
    public static string Clean(string answer, string before, string after)
    {
        // Only when there is thinking to drop: stripping it trims the answer, and its indentation matters here.
        string s = answer.Contains("<think", StringComparison.Ordinal) ? AiText.WithoutThinking(answer) : answer;
        if (AiText.CodeBlocks(s) is { Count: > 0 } blocks)
        {
            s = blocks[0];
        }
        s = s.Replace("<CURSOR>", "", StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal);
        // Models often repeat the start of the line they are completing.
        string line = before[(before.LastIndexOf('\n') + 1)..];
        string head = line.TrimStart();
        if (head.Length > 0 && s.TrimStart().StartsWith(head, StringComparison.Ordinal))
        {
            s = s.TrimStart()[head.Length..];
        }
        // ...or everything after the cursor, when they rewrite the whole thing.
        string next = after.TrimStart().Split('\n')[0].Trim();
        if (next.Length >= 8)
        {
            int dup = s.IndexOf(next[..Math.Min(40, next.Length)], StringComparison.Ordinal);
            if (dup >= 0)
            {
                s = s[..dup];
            }
        }
        string[] lines = s.Split('\n');
        if (lines.Length > MaxLines)
        {
            s = string.Join('\n', lines[..MaxLines]);
        }
        s = s.TrimEnd();
        // At the start of a line, a suggestion that begins on the next one is really on this one.
        if (line.Trim().Length == 0)
        {
            s = s.TrimStart('\n');
        }
        return s.Trim().Length == 0 ? "" : s;
    }

    /// <summary>Ask <paramref name="model"/> for a suggestion at <paramref name="offset"/>; empty when it has none.</summary>
    public static async Task<string> SuggestAsync(IChatModel model, string text, int offset, string fileName, CancellationToken ct = default)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var answer = new StringBuilder();
        await foreach (string piece in model.StreamAsync(Prompt(text, offset, fileName, model.ContextTokens), new ChatOptions(MaxTokens: 200, Temperature: 0.1), ct).ConfigureAwait(false))
        {
            answer.Append(piece);
            if (answer.Length > 4000)
            {
                break;
            }
        }
        return Clean(answer.ToString(), text[..offset], text[offset..]);
    }

    /// <summary>
    /// Check a suggestion with Lean: the file with it inserted is elaborated in a scratch document beside the real
    /// one. True when no error starts on the lines it covers (an unfinished proof around it is fine: the suggestion
    /// may be the next step of one).
    /// </summary>
    public static async Task<bool> CheckWithLeanAsync(LeanServer server, string path, string text, int offset, string suggestion, CancellationToken ct = default)
    {
        string with = text[..offset] + suggestion + text[offset..];
        IReadOnlyList<Diagnostic> diags = await Scratch.CheckAsync(server, path, "Inline", with, ct).ConfigureAwait(false);
        return Acceptable(with, offset, suggestion, diags);
    }

    /// <summary>No error starts on the lines the suggestion (inserted at <paramref name="offset"/> of <paramref name="with"/>) covers.</summary>
    public static bool Acceptable(string with, int offset, string suggestion, IReadOnlyList<Diagnostic> diagnostics)
    {
        int first = with.AsSpan(0, offset).Count('\n');
        int last = first + suggestion.Count(c => c == '\n');
        return !diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error && d.Range.Start.Line >= first && d.Range.Start.Line <= last);
    }
}
