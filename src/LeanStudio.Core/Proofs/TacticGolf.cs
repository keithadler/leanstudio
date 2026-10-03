using System.Text.RegularExpressions;

namespace LeanStudio.Core.Proofs;

/// <summary>
/// The small clean-ups a reviewer asks for in a proof, made where they cannot change what the proof does: two
/// rewrites in a row become one (<c>rw [a]</c> then <c>rw [b]</c> is <c>rw [a, b]</c>, the same for <c>simp_rw</c>), and so do two
/// <c>intro</c>s. Only whole lines that do nothing else are merged: the same indentation, no comment, no
/// <c>;</c> or <c>&lt;;&gt;</c>, and for a rewrite the same location (<c>at h</c>).
/// </summary>
public static class TacticGolf
{
    private static readonly Regex Rewrite = new(@"^(?<ind>\s*)(?<tac>rw|simp_rw)\s*\[(?<rules>[^\]\n]*)\](?<at>\s+at\s+[^\n;<]+?)?\s*$", RegexOptions.Compiled);
    private static readonly Regex Intro = new(@"^(?<ind>\s*)intro\s+(?<args>[^;\n<-]+?)\s*$", RegexOptions.Compiled);

    /// <summary><paramref name="text"/> with the consecutive rewrites and intros merged, and how many lines went.</summary>
    public static (string Text, int Merged) Merge(string text)
    {
        string[] lines = text.Split('\n');
        var output = new List<string>(lines.Length);
        int merged = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string cr = line.EndsWith('\r') ? "\r" : "";
            string plain = line.TrimEnd('\r');
            while (i + 1 < lines.Length)
            {
                string next = lines[i + 1].TrimEnd('\r');
                string? joined = Join(plain, next);
                if (joined is null)
                {
                    break;
                }
                plain = joined;
                i++;
                merged++;
            }
            output.Add(plain + cr);
        }
        return (string.Join('\n', output), merged);
    }

    private static string? Join(string first, string second)
    {
        Match a = Rewrite.Match(first), b = Rewrite.Match(second);
        if (a.Success && b.Success && a.Groups["ind"].Value == b.Groups["ind"].Value && a.Groups["tac"].Value == b.Groups["tac"].Value
            && a.Groups["at"].Value.Trim() == b.Groups["at"].Value.Trim() && a.Groups["rules"].Value.Trim().Length > 0 && b.Groups["rules"].Value.Trim().Length > 0)
        {
            return $"{a.Groups["ind"].Value}{a.Groups["tac"].Value} [{a.Groups["rules"].Value.Trim()}, {b.Groups["rules"].Value.Trim()}]{a.Groups["at"].Value}";
        }
        Match c = Intro.Match(first), d = Intro.Match(second);
        if (c.Success && d.Success && c.Groups["ind"].Value == d.Groups["ind"].Value)
        {
            return $"{c.Groups["ind"].Value}intro {c.Groups["args"].Value.Trim()} {d.Groups["args"].Value.Trim()}";
        }
        return null;
    }
}
