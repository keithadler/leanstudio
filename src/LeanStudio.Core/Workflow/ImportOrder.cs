using System.Text.RegularExpressions;

namespace LeanStudio.Core.Workflow;

/// <summary>
/// Putting a file's imports in order, as Mathlib's style asks: each run of consecutive <c>import</c> lines in the
/// file's header sorted by module name, with repeated lines dropped. Only the header is touched: it ends at the first
/// line that is neither an import, blank nor a comment, so nothing in the body moves. Blank lines and comments between
/// runs stay where they are, which keeps a deliberate grouping.
/// </summary>
public static class ImportOrder
{
    private static readonly Regex ImportLine = new(@"^\s*(?:(?:public|private|meta)\s+)*import\s+(?:all\s+)?(?<m>\S+)", RegexOptions.Compiled);

    /// <summary><paramref name="text"/> with the runs of imports in its header sorted. Unchanged when they already are.</summary>
    public static string Sort(string text)
    {
        string[] lines = text.Split('\n');
        int i = 0;
        int blockComment = 0;
        var output = new List<string>(lines.Length);
        while (i < lines.Length)
        {
            string line = lines[i];
            string t = line.Trim();
            if (blockComment > 0 || t.StartsWith("/-", StringComparison.Ordinal))
            {
                // A block comment (the copyright header, or `/-! … -/`): skip it whole, nested ones included.
                for (int k = 0; k + 1 < t.Length; k++)
                {
                    if (k + 1 < t.Length && t[k] == '/' && t[k + 1] == '-')
                    {
                        blockComment++;
                        k++;
                    }
                    else if (k + 1 < t.Length && t[k] == '-' && t[k + 1] == '/')
                    {
                        blockComment--;
                        k++;
                    }
                }
                output.Add(line);
                i++;
                continue;
            }
            if (t.Length == 0 || t.StartsWith("--", StringComparison.Ordinal) || t == "module" || t == "prelude")
            {
                output.Add(line);
                i++;
                continue;
            }
            if (!ImportLine.IsMatch(line))
            {
                break; // the body begins
            }
            int start = i;
            while (i < lines.Length && ImportLine.IsMatch(lines[i]))
            {
                i++;
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            output.AddRange(lines[start..i]
                .OrderBy(l => ImportLine.Match(l).Groups["m"].Value, StringComparer.Ordinal)
                .Where(l => seen.Add(l.Trim())));
        }
        output.AddRange(lines[i..]);
        return string.Join('\n', output);
    }
}
