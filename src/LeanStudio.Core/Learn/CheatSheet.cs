using System.Text;

namespace LeanStudio.Core.Learn;

/// <summary>
/// A cheat sheet of your own: the tactics and keywords a file uses, each explained in plain words with an example, in the
/// order you first used them. Learning from what you have already written, not from a list of everything Lean has.
/// </summary>
public static class CheatSheet
{
    /// <summary>The tactics and keywords of <paramref name="text"/> that <see cref="TacticGuide"/> explains, in order of first use.</summary>
    public static IReadOnlyList<GuideEntry> Used(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var used = new List<GuideEntry>();
        foreach (string line in CodeText.Lines(text))
        {
            if (TacticGuide.TacticOf(line) is string word && TacticGuide.Explain(word) is GuideEntry entry && seen.Add(word))
            {
                used.Add(entry);
            }
        }
        return used;
    }

    /// <summary>The cheat sheet for <paramref name="text"/> as Markdown: tactics first, then keywords.</summary>
    public static string ToMarkdown(string text)
    {
        IReadOnlyList<GuideEntry> used = Used(text);
        var sb = new StringBuilder("# My Lean cheat sheet\n\n");
        if (used.Count == 0)
        {
            return sb.Append("Nothing here yet: write a proof with `intro`, `simp` or `rfl`, and it will be explained here.\n").ToString();
        }
        sb.Append("Everything below is something you used, in the order you first used it.\n");
        foreach ((string title, string kind) in new[] { ("Tactics", "tactic"), ("Keywords", "keyword") })
        {
            List<GuideEntry> group = [.. used.Where(e => e.Kind == kind)];
            if (group.Count == 0)
            {
                continue;
            }
            sb.Length = sb.ToString().TrimEnd().Length;
            sb.Append("\n\n## ").Append(title).Append("\n\n");
            foreach (GuideEntry e in group)
            {
                sb.Append("### `").Append(e.Name).Append("`\n").Append(e.Explanation.Trim()).Append("\n\nExample: `").Append(System.Text.RegularExpressions.Regex.Replace(e.Example.Trim(), @"\s+", " ")).Append("`\n\n");
            }
        }
        return sb.ToString().TrimEnd() + "\n";
    }
}
