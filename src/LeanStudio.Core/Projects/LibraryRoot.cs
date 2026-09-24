using System.Text.RegularExpressions;

namespace LeanStudio.Core.Projects;

/// <summary>
/// A library's root file, when it imports every module of the library (as <c>Mathlib.lean</c> does, kept so by
/// <c>lake exe mk_all</c>): a new module has to be added to it, or CI's check that everything is imported fails.
/// </summary>
public static class LibraryRoot
{
    private static readonly Regex Import = new(@"^\s*(?:(?:public|private|meta)\s+)*import\s+(?:all\s+)?(?<m>[^\s-]+)", RegexOptions.Compiled);

    /// <summary>The root file of the library <paramref name="module"/> belongs to (<c>A.lean</c> for <c>A.B.C</c>), if it exists.</summary>
    public static string? RootFileOf(LeanProject project, string module)
    {
        int dot = module.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0)
        {
            return null;
        }
        string root = Path.Combine(project.Root, module[..dot] + ".lean");
        return File.Exists(root) ? root : null;
    }

    /// <summary>The modules <paramref name="rootText"/> imports, in order.</summary>
    public static IReadOnlyList<string> ImportsOf(string rootText) =>
        rootText.Split('\n').Select(l => Import.Match(l)).Where(m => m.Success).Select(m => m.Groups["m"].Value).ToList();

    /// <summary>
    /// Whether <paramref name="rootText"/> is an "import everything" file: imports, comments and a <c>module</c>
    /// line, and nothing else.
    /// </summary>
    public static bool ImportsEverything(string rootText)
    {
        bool inComment = false, any = false;
        foreach (string raw in rootText.Split('\n'))
        {
            string l = raw.Trim();
            if (inComment)
            {
                inComment = !l.Contains("-/", StringComparison.Ordinal);
                continue;
            }
            if (l.Length == 0 || l.StartsWith("--", StringComparison.Ordinal) || l == "module")
            {
                continue;
            }
            if (l.StartsWith("/-", StringComparison.Ordinal))
            {
                inComment = !l[2..].Contains("-/", StringComparison.Ordinal);
                continue;
            }
            if (!Import.IsMatch(l))
            {
                return false;
            }
            any = true;
        }
        return any;
    }

    /// <summary>
    /// <paramref name="rootText"/> with <c>import <paramref name="module"/></c> added: in order if the imports are
    /// sorted, else after the last one. Unchanged if it is already imported.
    /// </summary>
    public static string AddImport(string rootText, string module)
    {
        if (ImportsOf(rootText).Contains(module, StringComparer.Ordinal))
        {
            return rootText;
        }
        var lines = rootText.Split('\n').ToList();
        var at = lines.Select((l, i) => (Match: Import.Match(l), Index: i)).Where(x => x.Match.Success).ToList();
        string line = "import " + module;
        if (at.Count == 0)
        {
            lines.Insert(0, line);
            return string.Join('\n', lines);
        }
        var names = at.Select(x => x.Match.Groups["m"].Value).ToList();
        bool sorted = names.SequenceEqual(names.Order(StringComparer.Ordinal));
        int insertAt = at[^1].Index + 1;
        if (sorted && at.FirstOrDefault(x => string.CompareOrdinal(x.Match.Groups["m"].Value, module) > 0) is { Match: not null } next)
        {
            insertAt = next.Index;
        }
        lines.Insert(insertAt, line);
        return string.Join('\n', lines);
    }

    /// <summary><paramref name="rootText"/> without its <c>import</c> of <paramref name="module"/> (for a deleted module).</summary>
    public static string RemoveImport(string rootText, string module) =>
        string.Join('\n', rootText.Split('\n').Where(l => Import.Match(l) is not { Success: true } m || m.Groups["m"].Value != module));

    /// <summary>
    /// The modules of the library rooted at <paramref name="rootFile"/> (every <c>.lean</c> file under the folder of
    /// the same name) that it doesn't import, sorted.
    /// </summary>
    public static IReadOnlyList<string> Missing(LeanProject project, string rootFile)
    {
        string lib = Path.GetFileNameWithoutExtension(rootFile);
        string folder = Path.Combine(Path.GetDirectoryName(rootFile)!, lib);
        if (!Directory.Exists(folder))
        {
            return [];
        }
        var imported = new HashSet<string>(ImportsOf(File.ReadAllText(rootFile)), StringComparer.Ordinal);
        return Directory.EnumerateFiles(folder, "*.lean", SearchOption.AllDirectories)
            .Select(f => project.ModuleNameOf(f))
            .OfType<string>()
            .Where(m => !imported.Contains(m))
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
