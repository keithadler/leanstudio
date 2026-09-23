using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LeanStudio.Core.Editing;
using LeanStudio.Core.Processes;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Toolchains;

namespace LeanStudio.Core.Workflow;

/// <summary>One compiled C function: its name and its code.</summary>
public sealed record CFunction(string Name, string Code);

/// <summary>
/// The C that Lean's compiler emits for a file, and the functions that implement a given definition. Lean
/// compiles each definition to a C function named after it (<c>Foo.bar</c> becomes <c>l_Foo_bar</c>), with
/// companions such as <c>___boxed</c> wrappers and lifted lambdas; theorems have no code, as proofs are erased.
/// </summary>
public static partial class EmittedC
{
    /// <summary>
    /// Lean's name mangling for C identifiers: components joined by '_', '_' doubled, other ASCII as _xHH,
    /// other characters as _uHHHH or _UHHHHHHHH, and a component that would start with '_' or a digit is
    /// prefixed with "00".
    /// </summary>
    public static string Mangle(string leanName)
    {
        var parts = new List<string>();
        foreach (string component in SplitName(leanName))
        {
            if (component.Length > 0 && component.All(char.IsAsciiDigit))
            {
                parts.Add("_" + component + "_");
                continue;
            }
            var sb = new StringBuilder();
            foreach (Rune r in component.EnumerateRunes())
            {
                int v = r.Value;
                if (v < 128 && (char.IsAsciiLetterOrDigit((char)v)))
                {
                    sb.Append((char)v);
                }
                else if (v == '_')
                {
                    sb.Append("__");
                }
                else if (v < 256)
                {
                    sb.Append("_x").Append(v.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                }
                else if (v < 0x10000)
                {
                    sb.Append("_u").Append(v.ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append("_U").Append(v.ToString("x8", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            string s = sb.ToString();
            if (s.Length > 0 && (s[0] == '_' || char.IsAsciiDigit(s[0])))
            {
                s = "00" + s;
            }
            parts.Add(s);
        }
        return "l_" + string.Join('_', parts);
    }

    /// <summary>Split a Lean name on dots, keeping «guillemet» components whole.</summary>
    public static IEnumerable<string> SplitName(string name)
    {
        var sb = new StringBuilder();
        bool quoted = false;
        foreach (char c in name)
        {
            if (c == '«')
            {
                quoted = true;
            }
            else if (c == '»')
            {
                quoted = false;
            }
            else if (c == '.' && !quoted)
            {
                yield return sb.ToString();
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        yield return sb.ToString();
    }

    [GeneratedRegex(@"^(?:LEAN_EXPORT |static |LEAN_EXPORT static )?[A-Za-z_][\w\s\*]*?\b(?<name>[A-Za-z_]\w*)\((?<params>[^;{]*)\)\s*\{", RegexOptions.Multiline)]
    private static partial Regex FunctionStart();

    /// <summary>Every function defined in a C file, with its body.</summary>
    public static IReadOnlyList<CFunction> Functions(string c)
    {
        var list = new List<CFunction>();
        foreach (Match m in FunctionStart().Matches(c))
        {
            int open = c.IndexOf('{', m.Index + m.Length - 1);
            int depth = 0, end = -1;
            for (int i = open; i < c.Length; i++)
            {
                if (c[i] == '{')
                {
                    depth++;
                }
                else if (c[i] == '}' && --depth == 0)
                {
                    end = i;
                    break;
                }
            }
            if (end > 0)
            {
                list.Add(new CFunction(m.Groups["name"].Value, c[m.Index..(end + 1)]));
            }
        }
        return list;
    }

    /// <summary>The functions that implement a definition: itself, its boxed wrapper, and what the compiler split off it.</summary>
    public static IReadOnlyList<CFunction> For(string c, string leanName)
    {
        string m = Mangle(leanName);
        return Functions(c)
            .Where(f => f.Name == m || f.Name.StartsWith(m + "___", StringComparison.Ordinal)
                        || f.Name.StartsWith(m + "_lambda", StringComparison.Ordinal) || f.Name.StartsWith(m + "_spec", StringComparison.Ordinal)
                        || f.Name.StartsWith(m + "_match", StringComparison.Ordinal) || f.Name == "_init_" + m)
            .OrderBy(f => f.Name == m ? 0 : f.Name.EndsWith("___boxed", StringComparison.Ordinal) ? 2 : 1)
            .ToList();
    }

    /// <summary>
    /// Compile <paramref name="text"/> (the file as it is in the editor) to C. In a Lake project it runs with the
    /// project's dependencies, so their imports must be built.
    /// </summary>
    public static async Task<(string? C, string Error)> EmitAsync(LeanProject project, string sourcePath, string text, CancellationToken ct = default)
    {
        // Lean names a module after its path under the root, and insists the file is inside it: compile a copy
        // (the editor's text, saved or not) in a mirror of the project under .lake, rooted there, so the module
        // keeps its real name.
        string mirror = Path.Combine(project.Root, ".lake", "leanstudio-c");
        string rel = Path.GetRelativePath(project.Root, Path.GetFullPath(sourcePath));
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            rel = Path.GetFileName(sourcePath);
        }
        string leanFile = Path.Combine(mirror, rel);
        string cFile = Path.ChangeExtension(leanFile, ".c");
        Directory.CreateDirectory(Path.GetDirectoryName(leanFile)!);
        await File.WriteAllTextAsync(leanFile, text, ct).ConfigureAwait(false);
        File.Delete(cFile);
        string[] args = ["--root=" + mirror, "-c", cFile, leanFile];
        ProcessResult r = project.IsLakeProject
            ? await ProcessRunner.RunAsync(Elan.FindExecutable("lake") ?? "lake", ["env", "lean", .. args], project.Root, ct: ct).ConfigureAwait(false)
            : await ProcessRunner.RunAsync(Elan.FindExecutable("lean") ?? "lean", args, project.Root, ct: ct).ConfigureAwait(false);
        if (File.Exists(cFile))
        {
            return (await File.ReadAllTextAsync(cFile, ct).ConfigureAwait(false), "");
        }
        return (null, r.Output.Trim().Length > 0 ? r.Output.Trim() : $"lean exited with code {r.ExitCode}");
    }

    [GeneratedRegex(@"^\s*(@\[[^\]]*\]\s*)*((private|protected|noncomputable|partial|unsafe|nonrec)\s+)*(?<kind>def|theorem|lemma|instance|abbrev|opaque|example|structure|inductive|class)\b\s*(?<name>[^\s:({\[]*)")]
    private static partial Regex DeclarationLine();

    [GeneratedRegex(@"^\s*namespace\s+(?<n>\S+)")]
    private static partial Regex NamespaceLine();

    [GeneratedRegex(@"^\s*end\b\s*(?<n>\S*)")]
    private static partial Regex EndLine();

    [GeneratedRegex(@"^\s*section\b")]
    private static partial Regex SectionLine();

    /// <summary>The declaration a line belongs to, with its namespace: (kind, full name), or null outside one.</summary>
    public static (string Kind, string Name)? DeclarationAt(IReadOnlyList<string> lines, int line)
    {
        (string Kind, string Name)? found = null;
        var scopes = new Stack<(bool IsNamespace, string Name)>();
        bool inBlock = false;
        for (int i = 0; i < lines.Count && i <= line; i++)
        {
            string code = Proofs.ProofSteps.StripComments(lines[i], ref inBlock);
            Match d = DeclarationLine().Match(code);
            if (d.Success && lines[i].Length > 0 && !char.IsWhiteSpace(lines[i][0]))
            {
                string ns = string.Join('.', scopes.Reverse().Where(s => s.IsNamespace).Select(s => s.Name));
                string name = d.Groups["name"].Value;
                found = (d.Groups["kind"].Value, name.Length == 0 ? "" : ns.Length == 0 || name.StartsWith("_root_.", StringComparison.Ordinal) ? name.Replace("_root_.", "", StringComparison.Ordinal) : ns + "." + name);
                continue;
            }
            if (NamespaceLine().Match(code) is { Success: true } n)
            {
                foreach (string part in n.Groups["n"].Value.Split('.'))
                {
                    scopes.Push((true, part));
                }
                found = null;
            }
            else if (SectionLine().IsMatch(code))
            {
                scopes.Push((false, ""));
                found = null;
            }
            else if (EndLine().Match(code) is { Success: true } e)
            {
                int parts = e.Groups["n"].Value.Length == 0 ? 1 : e.Groups["n"].Value.Split('.').Length;
                for (int k = 0; k < parts && scopes.Count > 0; k++)
                {
                    scopes.Pop();
                }
                found = null;
            }
            else if (code.Length > 0 && !char.IsWhiteSpace(code[0]) && !code.TrimStart().StartsWith('@') && found is not null && i > 0 && !DeclarationLine().IsMatch(code))
            {
                // Another top-level command (#eval, open, variable…) ends the previous declaration.
                if (code.TrimStart().StartsWith('#') || code.StartsWith("open", StringComparison.Ordinal) || code.StartsWith("variable", StringComparison.Ordinal))
                {
                    found = null;
                }
            }
        }
        return found;
    }
}

/// <summary>A planned replacement in one file: how many matches, and the new text.</summary>
public sealed record FileReplacement(string Path, int Count, string NewText);

/// <summary>Refactorings that span files: replace across the project, and renaming a module with its imports.</summary>
public static partial class Refactor
{
    /// <summary>Replace every match in a text; with <paramref name="regex"/>, $1 and friends refer to groups.</summary>
    public static (string Text, int Count) ReplaceInText(string text, string query, string replacement, bool caseSensitive, bool regex)
    {
        if (query.Length == 0)
        {
            return (text, 0);
        }
        if (regex)
        {
            var re = new Regex(query, (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            int n = 0;
            string result = re.Replace(text, m =>
            {
                n++;
                return m.Result(replacement);
            });
            return (result, n);
        }
        StringComparison cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var sb = new StringBuilder();
        int count = 0, at = 0;
        for (int i = text.IndexOf(query, cmp); i >= 0; i = text.IndexOf(query, at, cmp))
        {
            sb.Append(text, at, i - at).Append(replacement);
            at = i + query.Length;
            count++;
        }
        sb.Append(text, at, text.Length - at);
        return (sb.ToString(), count);
    }

    /// <summary>Plan a project-wide replacement: every source file that would change, and its new text.</summary>
    public static IReadOnlyList<FileReplacement> Plan(string root, string query, string replacement, bool caseSensitive, bool regex, Func<string, string?>? openText = null, CancellationToken ct = default)
    {
        var list = new List<FileReplacement>();
        foreach (string file in ProjectSearch.Files(root))
        {
            ct.ThrowIfCancellationRequested();
            string? text = openText?.Invoke(file);
            try
            {
                text ??= File.ReadAllText(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            (string updated, int n) = ReplaceInText(text, query, replacement, caseSensitive, regex);
            if (n > 0)
            {
                list.Add(new FileReplacement(file, n, updated));
            }
        }
        return list;
    }

    /// <summary>The module a source file defines, relative to a root: Root/A/B.lean is A.B.</summary>
    public static string ModuleOf(string root, string file) =>
        Path.GetRelativePath(root, file)[..^".lean".Length].Replace(Path.DirectorySeparatorChar, '.').Replace('/', '.');

    [GeneratedRegex(@"^(\s*(?:public\s+|private\s+|meta\s+)*import\s+(?:all\s+)?)(.*)$", RegexOptions.Multiline)]
    private static partial Regex ImportLine();

    /// <summary>
    /// Rename a module: move its file, and rewrite every <c>import</c> of it in the project (only whole module
    /// names: renaming A.B leaves A.Bc alone). Returns the files whose imports changed.
    /// </summary>
    public static IReadOnlyList<string> RenameModule(string root, string oldFile, string newFile)
    {
        string oldModule = ModuleOf(root, oldFile), newModule = ModuleOf(root, newFile);
        var changed = new List<string>();
        foreach (string file in ProjectSearch.Files(root, leanOnly: true))
        {
            string text = File.ReadAllText(file);
            string updated = RewriteImports(text, oldModule, newModule);
            if (updated != text)
            {
                File.WriteAllText(file, updated);
                changed.Add(file);
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(newFile)!);
        File.Move(oldFile, newFile);
        return changed;
    }

    public static string RewriteImports(string text, string oldModule, string newModule) =>
        ImportLine().Replace(text, m =>
        {
            string[] modules = m.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!modules.Contains(oldModule))
            {
                return m.Value;
            }
            return m.Groups[1].Value + string.Join(' ', modules.Select(x => x == oldModule ? newModule : x));
        });
}
