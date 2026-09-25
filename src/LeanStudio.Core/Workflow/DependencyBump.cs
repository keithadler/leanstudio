using System.Text.Json;
using System.Text.RegularExpressions;
using LeanStudio.Core.Projects;

namespace LeanStudio.Core.Workflow;

/// <summary>A deprecated name used somewhere, and what Lean says to use instead.</summary>
/// <param name="File">The file that uses it.</param>
/// <param name="Line">The 0-based line.</param>
/// <param name="Column">The 0-based column where the old name starts.</param>
/// <param name="Old">The deprecated name.</param>
/// <param name="New">The name Lean says to use.</param>
public sealed record DeprecatedUse(string File, int Line, int Column, string Old, string New);

/// <summary>What a dependency update changed and broke.</summary>
/// <param name="Package">The package updated (<c>mathlib</c>).</param>
/// <param name="OldRev">Its commit before.</param>
/// <param name="NewRev">Its commit after.</param>
/// <param name="OldToolchain">The project's toolchain before.</param>
/// <param name="NewToolchain">After (the dependency's, when it moved).</param>
/// <param name="Errors">The build's errors after the update, by file.</param>
/// <param name="Deprecated">Uses of deprecated names, with their replacements.</param>
public sealed record BumpReport(string Package, string? OldRev, string? NewRev, string? OldToolchain, string? NewToolchain,
    IReadOnlyDictionary<string, IReadOnlyList<BuildMessage>> Errors, IReadOnlyList<DeprecatedUse> Deprecated)
{
    /// <summary>Everything in a few lines, for Output.</summary>
    public string Summary
    {
        get
        {
            string revs = OldRev == NewRev ? $"{Package} is already at {Short(NewRev)}" : $"{Package} {Short(OldRev)} → {Short(NewRev)}";
            string tc = OldToolchain == NewToolchain ? "" : $"; toolchain {OldToolchain} → {NewToolchain}";
            int errors = Errors.Values.Sum(e => e.Count);
            string broke = errors == 0 ? "the project builds" : $"{errors} error{(errors == 1 ? "" : "s")} in {Errors.Count} file{(Errors.Count == 1 ? "" : "s")}";
            string dep = Deprecated.Count == 0 ? "" : $"; {Deprecated.Count} use{(Deprecated.Count == 1 ? "" : "s")} of deprecated names ({Deprecated.Select(d => d.Old).Distinct().Count()} different) can be renamed automatically";
            return revs + tc + ": " + broke + dep + ".";
        }
    }

    private static string Short(string? rev) => rev is null ? "?" : rev.Length > 8 ? rev[..8] : rev;
}

/// <summary>
/// Updating a dependency (Mathlib, usually) and finding out what that did: the commits before and after, the
/// toolchain it moved to, the build's errors by file, and every use of a name the update deprecated, with the
/// replacement Lean names, to rename in one go.
/// </summary>
public static partial class DependencyBump
{
    /// <summary>The commit a package is at, from lake-manifest.json; null when it isn't there.</summary>
    public static string? ManifestRev(LeanProject project, string package)
    {
        if (!File.Exists(project.ManifestPath))
        {
            return null;
        }
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(project.ManifestPath));
        if (!doc.RootElement.TryGetProperty("packages", out JsonElement pkgs))
        {
            return null;
        }
        foreach (JsonElement p in pkgs.EnumerateArray())
        {
            if (p.TryGetProperty("name", out JsonElement n) && string.Equals(n.GetString(), package, StringComparison.OrdinalIgnoreCase)
                && p.TryGetProperty("rev", out JsonElement rev))
            {
                return rev.GetString();
            }
        }
        return null;
    }

    [GeneratedRegex(@"`(?<old>[^`]+)` has been deprecated[.:,]?\s*(?:[Uu]se|consider using)\s+`(?<new>[^`]+)`")]
    private static partial Regex DeprecatedMessage();

    /// <summary>The uses of deprecated names in a build's messages, with what Lean says to use instead.</summary>
    public static IReadOnlyList<DeprecatedUse> DeprecatedUses(IEnumerable<BuildMessage> messages) =>
        messages.Select(m => (m, DeprecatedMessage().Match(m.Message)))
                .Where(x => x.Item2.Success)
                .Select(x => new DeprecatedUse(x.m.Path, x.m.Line, x.m.Column, x.Item2.Groups["old"].Value, x.Item2.Groups["new"].Value))
                .Distinct()
                .ToList();

    /// <summary>
    /// <paramref name="text"/> (the contents of one file) with each deprecated use in it renamed. A use is replaced
    /// only where the old name is written, or its last components are (<c>foo</c> for <c>Nat.foo</c>, as code in
    /// the namespace writes it); the new name is written as short, if it is in the same namespace, and in full if
    /// not. Uses at positions that no longer hold the name are left alone.
    /// </summary>
    public static string Rename(string text, IEnumerable<DeprecatedUse> uses)
    {
        string[] lines = text.Split('\n');
        foreach (IGrouping<int, DeprecatedUse> onLine in uses.GroupBy(u => u.Line))
        {
            if (onLine.Key < 0 || onLine.Key >= lines.Length)
            {
                continue;
            }
            string line = lines[onLine.Key];
            foreach (DeprecatedUse u in onLine.OrderByDescending(u => u.Column).Where(u => u.Column >= 0 && u.Column <= line.Length))
            {
                (string written, string replacement)? pick = Candidates(u).FirstOrDefault(c =>
                    u.Column + c.Written.Length <= line.Length && string.CompareOrdinal(line, u.Column, c.Written, 0, c.Written.Length) == 0
                    && (u.Column + c.Written.Length == line.Length || !IsNameChar(line[u.Column + c.Written.Length])));
                if (pick is (string w, string r))
                {
                    line = line[..u.Column] + r + line[(u.Column + w.Length)..];
                }
            }
            lines[onLine.Key] = line;
        }
        return string.Join('\n', lines);
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '\'' or '.' or '!' or '?';

    /// <summary>The ways the old name may be written, longest first, each with the new name to write there.</summary>
    private static IEnumerable<(string Written, string Replacement)> Candidates(DeprecatedUse u)
    {
        string[] old = u.Old.Split('.'), nu = u.New.Split('.');
        for (int k = old.Length; k >= 1; k--)
        {
            string written = string.Join('.', old[^k..]);
            // Written short (inside its namespace), the new name can be written short too only if it lives in the
            // same namespace; otherwise it is written in full.
            int keep = Math.Min(k, nu.Length);
            bool sameNamespace = old[..^k].SequenceEqual(nu[..^keep]);
            yield return (written, sameNamespace ? string.Join('.', nu[^keep..]) : u.New);
        }
    }
}
