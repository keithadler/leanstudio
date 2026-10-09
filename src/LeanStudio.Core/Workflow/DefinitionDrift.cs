using System.Text.RegularExpressions;

namespace LeanStudio.Core.Workflow;

/// <summary>A definition as written: where it is, its name, its body and its docstring.</summary>
/// <param name="Path">The file.</param>
/// <param name="Line">0-based line of the declaration.</param>
/// <param name="Name">The name as declared.</param>
/// <param name="Body">The body, comments removed and normalized, so spacing and bound-variable names do not count.</param>
/// <param name="Doc">The docstring text, or empty.</param>
/// <param name="Text">The body as written, comments removed and spacing collapsed, for a person to read.</param>
public sealed record DefinitionBody(string Path, int Line, string Name, string Body, string Doc, string Text = "");

/// <summary>A definition whose body is not the one it had.</summary>
/// <param name="Name">The definition's name.</param>
/// <param name="Path">Its file.</param>
/// <param name="Line">0-based line of the declaration after the change.</param>
/// <param name="Before">Its body before.</param>
/// <param name="After">Its body after.</param>
/// <param name="DocChanged">Whether its docstring changed as well: the prose was relabeled along with the code.</param>
/// <param name="Theorems">The theorems whose statements mention it.</param>
public sealed record ChangedDefinition(string Name, string Path, int Line, string Before, string After, bool DocChanged, IReadOnlyList<string> Theorems);

/// <summary>
/// A theorem's statement means what the definitions in it mean. Change a definition's body and every theorem stating
/// something about it says something else, though not one of those statements changed as text. This finds the definitions
/// whose bodies changed between two versions, and the theorems that mention them, so a reviewer reads the definition change
/// first. Read from the text, so nothing has to be built.
/// </summary>
public static class DefinitionDrift
{
    private static readonly Regex Declaration = new(
        @"^(?:@\[[^\]]*\]\s*)*(?:(?:protected|private|public|noncomputable|partial|unsafe|nonrec)\s+)*(?:def|abbrev|structure|class|inductive|opaque)\s+(?<name>[^\s:({\[]+)",
        RegexOptions.Compiled);

    /// <summary>The definitions in <paramref name="text"/>, with their bodies and docstrings.</summary>
    public static IReadOnlyList<DefinitionBody> Definitions(string path, string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var found = new List<DefinitionBody>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0 || char.IsWhiteSpace(lines[i][0]))
            {
                continue;
            }
            Match m = Declaration.Match(lines[i]);
            if (!m.Success)
            {
                continue;
            }
            int end = i + 1;
            while (end < lines.Length && (lines[end].Length == 0 || char.IsWhiteSpace(lines[end][0]) || IsContinuation(lines[end])))
            {
                end++;
            }
            string body = string.Join('\n', lines[i..end].Select(StripLineComment));
            body = Regex.Replace(body, @"/-.*?-/", "", RegexOptions.Singleline);
            found.Add(new DefinitionBody(path, i, m.Groups["name"].Value, DuplicateStatements.Normalize(body), DocAbove(lines, i), Regex.Replace(body, @"\s+", " ").Trim()));
        }
        return found;
    }

    /// <summary>
    /// The definitions that exist both before and after with different bodies, each with the theorems of
    /// <paramref name="theorems"/> whose statements mention it.
    /// </summary>
    public static IReadOnlyList<ChangedDefinition> Compare(IEnumerable<DefinitionBody> before, IEnumerable<DefinitionBody> after, IEnumerable<TheoremStatement> theorems)
    {
        var was = new Dictionary<string, DefinitionBody>(StringComparer.Ordinal);
        foreach (DefinitionBody d in before)
        {
            was.TryAdd(d.Name, d);
        }
        var statements = theorems.ToList();
        var changed = new List<ChangedDefinition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (DefinitionBody now in after)
        {
            if (!seen.Add(now.Name) || !was.TryGetValue(now.Name, out DefinitionBody? old) || old.Body == now.Body)
            {
                continue;
            }
            string last = now.Name[(now.Name.LastIndexOf('.') + 1)..];
            string[] mentioning = [.. statements
                .Where(t => Regex.IsMatch(t.Statement, $@"(?<![\w.']){Regex.Escape(last)}(?![\w'])"))
                .Select(t => t.Name).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal)];
            changed.Add(new ChangedDefinition(now.Name, now.Path, now.Line, old.Text.Length > 0 ? old.Text : old.Body, now.Text.Length > 0 ? now.Text : now.Body,
                Regex.Replace(old.Doc, @"\s+", " ").Trim() != Regex.Replace(now.Doc, @"\s+", " ").Trim(), mentioning));
        }
        return [.. changed.OrderBy(c => c.Name, StringComparer.Ordinal)];
    }

    private static bool IsContinuation(string line) =>
        line.StartsWith('|') || line.StartsWith(')') || line.StartsWith(']') || line.StartsWith('}');

    private static string StripLineComment(string line)
    {
        int i = line.IndexOf("--", StringComparison.Ordinal);
        return i >= 0 ? line[..i] : line;
    }

    private static string DocAbove(string[] lines, int declaration)
    {
        int k = declaration - 1;
        while (k >= 0 && lines[k].StartsWith("@[", StringComparison.Ordinal) && lines[k].TrimEnd().EndsWith(']'))
        {
            k--;
        }
        if (k < 0 || !lines[k].TrimEnd().EndsWith("-/", StringComparison.Ordinal))
        {
            return "";
        }
        int end = k;
        while (k >= 0 && !lines[k].StartsWith("/--", StringComparison.Ordinal))
        {
            k--;
        }
        return k < 0 ? "" : string.Join(' ', lines[k..(end + 1)]);
    }
}
