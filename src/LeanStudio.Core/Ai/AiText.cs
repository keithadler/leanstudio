using System.Text.RegularExpressions;

namespace LeanStudio.Core.Ai;

/// <summary>Text handling shared by the AI features: token estimates, trimming to fit, and reading answers.</summary>
public static partial class AiText
{
    /// <summary>
    /// A rough, cautious count of the tokens in <paramref name="text"/>. Lean is dense with symbols, which tokenize
    /// badly, so this counts about three characters a token, and each non-ASCII character as one.
    /// </summary>
    public static int EstimateTokens(string text)
    {
        int ascii = 0, other = 0;
        foreach (char c in text)
        {
            if (c < 128)
            {
                ascii++;
            }
            else if (!char.IsLowSurrogate(c))
            {
                other++;
            }
        }
        return (ascii + 2) / 3 + other;
    }

    /// <summary>
    /// <paramref name="text"/> cut to about <paramref name="tokens"/> tokens. <paramref name="keep"/> says which end
    /// matters: a file's code before the cursor keeps its end, a message keeps its start. The cut is marked.
    /// </summary>
    public static string Fit(string text, int tokens, FitKeep keep = FitKeep.Start)
    {
        if (tokens <= 0)
        {
            return "";
        }
        if (EstimateTokens(text) <= tokens)
        {
            return text;
        }
        // Shrink by lines where possible, so the model sees whole lines.
        string[] lines = text.Split('\n');
        int lo = 0, hi = lines.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            string candidate = Take(lines, mid, keep);
            if (EstimateTokens(candidate) <= tokens - 4)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }
        if (lo > 0)
        {
            string kept = Take(lines, lo, keep);
            return keep == FitKeep.End ? "…\n" + kept : kept + "\n…";
        }
        int chars = Math.Max(0, tokens * 3 - 3);
        return keep == FitKeep.End ? "…" + text[^Math.Min(chars, text.Length)..] : text[..Math.Min(chars, text.Length)] + "…";
    }

    private static string Take(string[] lines, int count, FitKeep keep) =>
        string.Join('\n', keep == FitKeep.End ? lines[^count..] : lines[..count]);

    /// <summary>An answer without the <c>&lt;think&gt;…&lt;/think&gt;</c> section reasoning models write first.</summary>
    public static string WithoutThinking(string answer)
    {
        string a = ThinkBlock().Replace(answer, "");
        // An unfinished think block (the answer was cut off while thinking) leaves nothing to use.
        int open = a.IndexOf("<think>", StringComparison.Ordinal);
        return (open >= 0 ? a[..open] : a).Trim();
    }

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex(@"```[ \t]*(?<lang>[\w+-]*)[^\n]*\n(?<code>.*?)(\n[ \t]*```|\z)", RegexOptions.Singleline)]
    private static partial Regex Fence();

    /// <summary>
    /// The code in each fenced block (<c>```lean … ```</c>) of a Markdown answer, in order. An unclosed last block
    /// (the answer was cut off) still counts.
    /// </summary>
    public static IReadOnlyList<string> CodeBlocks(string markdown) =>
        Fence().Matches(markdown).Select(m => m.Groups["code"].Value.TrimEnd()).Where(c => c.Trim().Length > 0).ToList();
}

/// <summary>Which end of a text to keep when it is cut to fit.</summary>
public enum FitKeep
{
    /// <summary>Keep the beginning.</summary>
    Start,
    /// <summary>Keep the end.</summary>
    End,
}
