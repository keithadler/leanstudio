using System.Globalization;
using System.Text;
using LeanStudio.Core.Learn;

namespace LeanStudio.Core.Verification;

/// <summary>
/// What the project's theorems say about its code, read aloud: for one definition, each theorem whose statement
/// mentions it, in plain English beside its name (<see cref="ProvedAbout"/>); for the whole project, which definitions
/// some theorem talks about and which none does (<see cref="ToMarkdown"/>). Every real bug in a verified project so
/// far has been a gap between what a theorem says and what the code does, not a broken proof, and this is where to
/// look for one.
/// </summary>
public static class SpecCoverage
{
    /// <summary>A theorem's statement in plain English, read from its source; <see langword="null"/> when it cannot be read.</summary>
    /// <param name="theorem">The theorem.</param>
    /// <param name="sources">Reads a source file's text, cached by the caller; <see langword="null"/> for one that cannot be read.</param>
    public static string? Reading(DeclarationRef theorem, Func<string, string?> sources)
    {
        if (theorem.SourceFile is null || theorem.Line is not int line || sources(theorem.SourceFile) is not string text)
        {
            return null;
        }
        return DeclExplain.ReadAt(text, line - 1);
    }

    /// <summary>A reader for <see cref="Reading"/> that reads each file once.</summary>
    public static Func<string, string?> CachedFiles()
    {
        var cache = new Dictionary<string, string?>(StringComparer.Ordinal);
        return path =>
        {
            if (!cache.TryGetValue(path, out string? text))
            {
                try
                {
                    text = File.ReadAllText(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    text = null;
                }
                cache[path] = text;
            }
            return text;
        };
    }

    private static string Where(string root, DeclarationRef d) =>
        d.SourceFile is null ? d.Module : Path.GetRelativePath(root, d.SourceFile).Replace('\\', '/') + (d.Line is int l ? $":{l}" : "");

    /// <summary>
    /// What the project's theorems state about <paramref name="name"/>, as Markdown: each theorem, where it is, and its
    /// statement in plain English. Says so plainly when no theorem mentions it.
    /// </summary>
    public static string ProvedAbout(string name, IReadOnlyList<DeclarationRef> theorems, string root, Func<string, string?>? sources = null)
    {
        sources ??= CachedFiles();
        var sb = new StringBuilder();
        if (theorems.Count == 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"No theorem in the project states anything about `{name}`. Whatever it computes, nothing proved depends on it being right, ");
            sb.Append("and nothing checks that it is.\n");
            return sb.ToString();
        }
        sb.Append(CultureInfo.InvariantCulture, $"{theorems.Count} theorem{(theorems.Count == 1 ? "" : "s")} state{(theorems.Count == 1 ? "s" : "")} something about `{name}`:\n\n");
        foreach (DeclarationRef t in theorems)
        {
            sb.Append(CultureInfo.InvariantCulture, $"- `{t.Name}` ({Where(root, t)})");
            if (Reading(t, sources) is string said)
            {
                sb.Append(": ").Append(said);
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// The project's definitions and what is said about them, as Markdown: how many some theorem mentions, the ones
    /// none does (where a gap between the theorems and the code would hide), then each stated one with the theorems
    /// about it. Long lists are cut at <paramref name="limit"/>.
    /// </summary>
    public static string ToMarkdown(IReadOnlyList<StatedDefinition> coverage, string root, int limit = 100)
    {
        var sb = new StringBuilder();
        int stated = coverage.Count(c => c.Theorems.Count > 0);
        sb.Append(CultureInfo.InvariantCulture, $"### What the theorems are about\n\n{stated:N0} of {coverage.Count:N0} definitions have a theorem whose statement mentions them.\n\n");
        List<StatedDefinition> unstated = coverage.Where(c => c.Theorems.Count == 0).ToList();
        if (unstated.Count > 0)
        {
            sb.Append("**No theorem mentions** (nothing proved depends on these being right):\n\n");
            foreach (StatedDefinition c in unstated.Take(limit))
            {
                sb.Append(CultureInfo.InvariantCulture, $"- `{c.Definition.Name}` ({Where(root, c.Definition)})\n");
            }
            if (unstated.Count > limit)
            {
                sb.Append(CultureInfo.InvariantCulture, $"- …and {unstated.Count - limit:N0} more\n");
            }
            sb.Append('\n');
        }
        List<StatedDefinition> withTheorems = coverage.Where(c => c.Theorems.Count > 0).ToList();
        if (withTheorems.Count > 0)
        {
            sb.Append("**Stated about**:\n\n");
            foreach (StatedDefinition c in withTheorems.Take(limit))
            {
                string names = string.Join(", ", c.Theorems.Take(6).Select(t => $"`{t.Name}`")) + (c.Theorems.Count > 6 ? $" and {c.Theorems.Count - 6} more" : "");
                sb.Append(CultureInfo.InvariantCulture, $"- `{c.Definition.Name}` ({Where(root, c.Definition)}): {names}\n");
            }
            if (withTheorems.Count > limit)
            {
                sb.Append(CultureInfo.InvariantCulture, $"- …and {withTheorems.Count - limit:N0} more\n");
            }
        }
        return sb.ToString();
    }
}
