namespace LeanStudio.Core.Workflow;

/// <summary>One style problem in a file's text.</summary>
/// <param name="Line">0-based line.</param>
/// <param name="Rule">A short name: <c>trailing-whitespace</c>, <c>long-line</c>, <c>tab</c>, <c>crlf</c> or <c>final-newline</c>.</param>
/// <param name="Message">What is wrong, for a person.</param>
public sealed record StyleProblem(int Line, string Rule, string Message);

/// <summary>
/// The text rules Mathlib's CI holds a file to (<c>lake exe lint-style</c>): no trailing whitespace, no line over 100
/// characters, no tabs, Unix line endings and exactly one newline at the end. Finding the problems, and fixing the
/// ones that have one obvious fix (everything but a long line, which is a person's to break).
/// </summary>
public static class StyleCheck
{
    /// <summary>The longest line Mathlib allows, in characters.</summary>
    public const int MaxLineLength = 100;

    /// <summary>Every problem in <paramref name="text"/>, in line order.</summary>
    public static IReadOnlyList<StyleProblem> Find(string text)
    {
        var problems = new List<StyleProblem>();
        string[] lines = text.Split('\n');
        bool crlf = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.EndsWith('\r'))
            {
                crlf = true;
                line = line[..^1];
            }
            if (line.Length > 0 && line[^1] is ' ' or '\t')
            {
                problems.Add(new StyleProblem(i, "trailing-whitespace", "Trailing whitespace."));
            }
            if (line.Contains('\t'))
            {
                problems.Add(new StyleProblem(i, "tab", "A tab: Lean files are indented with spaces."));
            }
            int length = new System.Globalization.StringInfo(line).LengthInTextElements;
            if (length > MaxLineLength)
            {
                problems.Add(new StyleProblem(i, "long-line", $"{length} characters; the limit is {MaxLineLength}."));
            }
        }
        if (crlf)
        {
            problems.Add(new StyleProblem(0, "crlf", "Windows line endings (CRLF); use LF."));
        }
        if (text.Length > 0 && !text.EndsWith('\n'))
        {
            problems.Add(new StyleProblem(lines.Length - 1, "final-newline", "The file does not end with a newline."));
        }
        else if (text.EndsWith("\n\n", StringComparison.Ordinal) || text.EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            problems.Add(new StyleProblem(lines.Length - 2, "final-newline", "More than one newline at the end of the file."));
        }
        return problems;
    }

    /// <summary>
    /// <paramref name="text"/> with what has one obvious fix fixed: line endings made LF, trailing whitespace
    /// removed, tabs turned into two spaces, and exactly one newline at the end (none for an empty file).
    /// Long lines are left for a person.
    /// </summary>
    public static string Fix(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }
        IEnumerable<string> lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(l => l.Replace("\t", "  ", StringComparison.Ordinal).TrimEnd(' '));
        string body = string.Join('\n', lines).TrimEnd('\n');
        return body.Length == 0 ? "" : body + "\n";
    }

    /// <summary>
    /// <paramref name="text"/> with the comment lines longer than <paramref name="max"/> characters broken at a space:
    /// a <c>--</c> comment continues on a new <c>--</c> line, and a line of prose in a doc or module comment continues on the
    /// next line. Code is never touched: not a line outside a comment, not one in a code fence or indented four spaces or
    /// more inside a comment, not a word longer than the limit (a URL).
    /// </summary>
    public static string WrapComments(string text, int max = MaxLineLength)
    {
        string[] lines = text.Split('\n');
        var output = new List<string>(lines.Length);
        int depth = 0;
        bool fence = false;
        foreach (string raw in lines)
        {
            string cr = raw.EndsWith('\r') ? "\r" : "";
            string line = cr.Length > 0 ? raw[..^1] : raw;
            bool inBlock = depth > 0;
            depth = Math.Max(0, depth + BlockDelta(line));
            string? prefix = null;
            if (!inBlock && line.TrimStart().StartsWith("--", StringComparison.Ordinal))
            {
                int dash = line.IndexOf("--", StringComparison.Ordinal);
                int end = dash + 2;
                while (end < line.Length && line[end] is '-' or '!')
                {
                    end++;
                }
                prefix = line[..end] + " ";
            }
            else if (inBlock || line.TrimStart().StartsWith("/-", StringComparison.Ordinal))
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    fence = !fence;
                }
                else if (!fence && !(inBlock && line.StartsWith("    ", StringComparison.Ordinal)))
                {
                    prefix = line[..(line.Length - line.TrimStart().Length)];
                }
            }
            if (depth == 0 && !inBlock && prefix is null || prefix is null)
            {
                output.Add(raw);
                continue;
            }
            while (new System.Globalization.StringInfo(line).LengthInTextElements > max)
            {
                int cut = line.LastIndexOf(' ', Math.Min(max, line.Length - 1));
                if (cut <= prefix.Length || line[prefix.Length..cut].Trim().Length == 0)
                {
                    break; // one long word: leave it
                }
                output.Add(line[..cut].TrimEnd() + cr);
                line = prefix + line[(cut + 1)..].TrimStart();
            }
            output.Add(line + cr);
        }
        return string.Join('\n', output);
    }

    /// <summary>How much a line opens more block comments than it closes (<c>/-</c> against <c>-/</c>).</summary>
    private static int BlockDelta(string line)
    {
        int delta = 0;
        for (int i = 0; i + 1 < line.Length; i++)
        {
            if (line[i] == '/' && line[i + 1] == '-')
            {
                delta++;
                i++;
            }
            else if (line[i] == '-' && line[i + 1] == '/')
            {
                delta--;
                i++;
            }
        }
        return delta;
    }
}
