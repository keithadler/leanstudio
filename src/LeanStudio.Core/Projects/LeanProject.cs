using LeanStudio.Core.Toolchains;
using LeanStudio.Lsp;

namespace LeanStudio.Core.Projects;

/// <summary>
/// A folder Lean Studio has open: a Lake project (it has a lakefile), a bare folder pinned to a toolchain (it has
/// only a <c>lean-toolchain</c>), or neither. The kind decides how the language server starts and what "build" means.
/// </summary>
public sealed class LeanProject
{
    public const string ToolchainFile = "lean-toolchain";
    private static readonly string[] Lakefiles = ["lakefile.lean", "lakefile.toml"];

    public LeanProject(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string Root { get; }

    public string Name => Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public string? Lakefile => Lakefiles.Select(f => Path.Combine(Root, f)).FirstOrDefault(File.Exists);

    public bool IsLakeProject => Lakefile is not null;

    public string ToolchainPath => Path.Combine(Root, ToolchainFile);

    /// <summary>The toolchain named in <c>lean-toolchain</c>, or null when the folder does not pin one.</summary>
    public string? Toolchain
    {
        get
        {
            if (!File.Exists(ToolchainPath))
            {
                return null;
            }
            string t = File.ReadAllText(ToolchainPath).Trim();
            return t.Length == 0 ? null : t;
        }
    }

    public void SetToolchain(string toolchain) => File.WriteAllText(ToolchainPath, toolchain.Trim() + "\n");

    public string BuildLibDirectory => Path.Combine(Root, ".lake", "build", "lib", "lean");

    public string PackagesDirectory => Path.Combine(Root, ".lake", "packages");

    public string ManifestPath => Path.Combine(Root, "lake-manifest.json");

    /// <summary>Whether the project depends on Mathlib, which decides whether "get cache" is offered.</summary>
    public bool DependsOnMathlib =>
        (File.Exists(ManifestPath) && File.ReadAllText(ManifestPath).Contains("\"mathlib\"", StringComparison.OrdinalIgnoreCase))
        || (Lakefile is string lf && File.ReadAllText(lf).Contains("mathlib", StringComparison.OrdinalIgnoreCase));

    /// <summary>The nearest folder at or above <paramref name="path"/> with a lakefile, else with a toolchain file.</summary>
    public static LeanProject? FindEnclosing(string path)
    {
        string? dir = Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path));
        string? pinned = null;
        for (string? d = dir; d is not null; d = Path.GetDirectoryName(d))
        {
            // Lake keeps dependencies in .lake/packages; each is a project, but the one being edited is the outer one.
            if (d.Split(Path.DirectorySeparatorChar).Contains(".lake"))
            {
                continue;
            }
            if (Lakefiles.Any(f => File.Exists(Path.Combine(d, f))))
            {
                return new LeanProject(d);
            }
            if (pinned is null && File.Exists(Path.Combine(d, ToolchainFile)))
            {
                pinned = d;
            }
        }
        return pinned is null ? null : new LeanProject(pinned);
    }

    /// <summary>
    /// How to start the language server for this folder. A Lake project uses <c>lake serve</c>, which knows the
    /// project's dependencies and rebuilds imports on demand; anything else gets a plain <c>lean --server</c>,
    /// with the toolchain passed explicitly when the folder does not pin one and elan has no default.
    /// </summary>
    public LeanServerCommand ServerCommand(string? fallbackToolchain = null)
    {
        string lake = Elan.FindExecutable("lake") ?? "lake";
        string lean = Elan.FindExecutable("lean") ?? "lean";
        var prefix = new List<string>();
        if (Toolchain is null && fallbackToolchain is not null)
        {
            prefix.Add("+" + fallbackToolchain);
        }
        return IsLakeProject
            ? new LeanServerCommand(lake, [.. prefix, "serve", "--"], Root)
            : new LeanServerCommand(lean, [.. prefix, "--server"], Root);
    }

    /// <summary>The Lean module a source file defines: <c>Root/Foo/Bar.lean</c> is <c>Foo.Bar</c>.</summary>
    public string? ModuleNameOf(string file)
    {
        string full = Path.GetFullPath(file);
        if (!full.StartsWith(Root, StringComparison.Ordinal) || !full.EndsWith(".lean", StringComparison.Ordinal))
        {
            return null;
        }
        string rel = Path.GetRelativePath(Root, full);
        return rel[..^".lean".Length].Replace(Path.DirectorySeparatorChar, '.').Replace('/', '.');
    }

    /// <summary>The compiled module for a source file, if Lake has built it.</summary>
    public string? OleanOf(string file)
    {
        string? module = ModuleNameOf(file);
        if (module is null)
        {
            return null;
        }
        string olean = Path.Combine([BuildLibDirectory, .. module.Split('.')]) + ".olean";
        return File.Exists(olean) ? olean : null;
    }

    /// <summary>Lean source files in the project, skipping build output and dependencies.</summary>
    public IEnumerable<string> SourceFiles()
    {
        var pending = new Stack<string>();
        pending.Push(Root);
        while (pending.Count > 0)
        {
            string d = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> dirs;
            try
            {
                files = Directory.EnumerateFiles(d, "*.lean");
                dirs = Directory.EnumerateDirectories(d);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                continue;
            }
            foreach (string f in files)
            {
                yield return f;
            }
            foreach (string sub in dirs)
            {
                string name = Path.GetFileName(sub);
                if (!name.StartsWith('.') && name != "build")
                {
                    pending.Push(sub);
                }
            }
        }
    }
}
