using System.Text;

namespace LeanStudio.Core.Workflow;

/// <summary>One message Lean gave about the code in a <see cref="ZulipPost"/>.</summary>
/// <param name="Line">1-based line.</param>
/// <param name="Column">1-based column.</param>
/// <param name="Severity"><c>error</c>, <c>warning</c> or <c>info</c>.</param>
/// <param name="Text">What Lean said.</param>
public sealed record ZulipMessage(int Line, int Column, string Severity, string Text);

/// <summary>
/// A question for the Lean Zulip chat, ready to paste: the code in a <c>lean</c> fence, then what Lean said about it in
/// a quote (errors first), the way a minimal working example is usually asked about there. The fences are long
/// enough that backticks in the code cannot end them early.
/// </summary>
public static class ZulipPost
{
    /// <summary>The message to paste: <paramref name="code"/> and, when there are any, <paramref name="messages"/>.</summary>
    public static string Build(string code, IEnumerable<ZulipMessage> messages)
    {
        var sb = new StringBuilder();
        sb.Append(Fence(code, "lean", code.TrimEnd('\n', '\r')));
        List<ZulipMessage> ordered = [.. messages.OrderBy(m => m.Severity == "error" ? 0 : m.Severity == "warning" ? 1 : 2).ThenBy(m => m.Line).ThenBy(m => m.Column)];
        if (ordered.Count > 0)
        {
            string body = string.Join("\n\n", ordered.Select(m => $"{m.Severity} ({m.Line}:{m.Column}): {m.Text.Trim()}"));
            sb.Append('\n').Append(Fence(body, "quote", body));
        }
        return sb.ToString();
    }

    private static string Fence(string within, string language, string body)
    {
        int longest = 0, run = 0;
        foreach (char c in within)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        string fence = new('`', Math.Max(3, longest + 1));
        return $"{fence}{language}\n{body}\n{fence}\n";
    }
}
