using System.Text.RegularExpressions;

namespace LeanOut;

/// <summary>
/// Finds which declaration the caret is in, and what Lean calls it, by reading the file above the caret:
/// <c>namespace</c>, <c>section</c> and <c>end</c> nest names, and the last declaration keyword at or above the caret
/// is the one the caret is inside. This is text analysis, not Lean's own elaboration, so it names the common cases
/// (theorem, lemma, def, abbrev, instance with a name, structure, inductive, ...) and says nothing for the rest.
/// </summary>
public static partial class DeclarationLocator
{
    [GeneratedRegex(@"^\s*namespace\s+(?<n>[^\s]+)")]
    private static partial Regex Namespace();

    [GeneratedRegex(@"^\s*section\b")]
    private static partial Regex Section();

    [GeneratedRegex(@"^\s*end\b")]
    private static partial Regex End();

    [GeneratedRegex(@"^\s*(?:@\[[^\]]*\]\s*)*(?:(?:private|protected|noncomputable|unsafe|partial|nonrec)\s+)*(?:theorem|lemma|def|abbrev|axiom|opaque|structure|class|inductive|instance|example)\b\s*(?<n>[^\s(){}\[\]:]*)")]
    private static partial Regex Declaration();

    [GeneratedRegex(@"^\s*(?:@\[[^\]]*\]\s*)*(?:(?:protected|noncomputable|unsafe|partial|nonrec)\s+)*private\b")]
    private static partial Regex Private();

    /// <summary>What the locator found.</summary>
    /// <param name="Candidates">Full names to try, most specific first (namespaces applied, then the bare name).</param>
    /// <param name="Reason">When there are no candidates, why not; otherwise empty.</param>
    public sealed record Located(IReadOnlyList<string> Candidates, string Reason);

    /// <summary>The declaration the caret is inside.</summary>
    /// <param name="text">The file's text.</param>
    /// <param name="caretLine">The caret's 0-based line.</param>
    public static Located At(string text, int caretLine)
    {
        string[] lines = text.Split('\n');
        var frames = new List<(bool IsNamespace, string Name)>();
        string? name = null;
        List<string> namespacesAtDecl = [];
        bool isPrivate = false;
        bool anonymous = false;
        for (int i = 0; i <= caretLine && i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            if (Namespace().Match(line) is { Success: true } ns)
            {
                frames.Add((true, ns.Groups["n"].Value));
            }
            else if (Section().IsMatch(line))
            {
                frames.Add((false, ""));
            }
            else if (End().IsMatch(line))
            {
                if (frames.Count > 0)
                {
                    frames.RemoveAt(frames.Count - 1);
                }
            }
            else if (Declaration().Match(line) is { Success: true } d)
            {
                string n = d.Groups["n"].Value;
                anonymous = n.Length == 0;
                name = n;
                isPrivate = Private().IsMatch(line);
                namespacesAtDecl = frames.Where(f => f.IsNamespace).Select(f => f.Name).ToList();
            }
        }
        if (name is null)
        {
            return new Located([], "The caret is not inside a declaration.");
        }
        if (anonymous)
        {
            return new Located([], "This declaration has no name written in the file (an anonymous instance or example).");
        }
        if (isPrivate)
        {
            return new Located([], "This declaration is private: Lean gives it a hidden name that a file cannot spell.");
        }
        return new Located(Candidates(namespacesAtDecl, name), "");
    }

    /// <summary>The names a written name could mean inside the given namespaces, most specific first.</summary>
    /// <param name="namespaces">The open namespaces, outermost first.</param>
    /// <param name="written">The name as written after the keyword.</param>
    public static IReadOnlyList<string> Candidates(IReadOnlyList<string> namespaces, string written)
    {
        if (written.StartsWith("_root_.", StringComparison.Ordinal))
        {
            return [written["_root_.".Length..]];
        }
        var result = new List<string>();
        for (int k = namespaces.Count; k >= 1; k--)
        {
            result.Add(string.Join('.', namespaces.Take(k)) + "." + written);
        }
        result.Add(written);
        return result;
    }

    /// <summary>The module names a file could be, most likely first: its path from the project root, then with leading folders dropped (a Lake <c>srcDir</c>).</summary>
    /// <param name="projectRoot">The project's folder.</param>
    /// <param name="filePath">The Lean file's full path.</param>
    public static IReadOnlyList<string> ModuleCandidates(string projectRoot, string filePath)
    {
        string rel = Path.GetRelativePath(projectRoot, filePath);
        if (rel.StartsWith("..", StringComparison.Ordinal) || !rel.EndsWith(".lean", StringComparison.Ordinal))
        {
            return [];
        }
        string[] parts = rel[..^".lean".Length].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var result = new List<string>();
        for (int drop = 0; drop < parts.Length && drop <= 2; drop++)
        {
            result.Add(string.Join('.', parts.Skip(drop)));
        }
        return result;
    }
}
