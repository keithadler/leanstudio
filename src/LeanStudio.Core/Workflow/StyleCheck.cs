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
}
