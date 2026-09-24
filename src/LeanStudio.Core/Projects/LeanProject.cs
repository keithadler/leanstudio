using LeanStudio.Core.Toolchains;
using LeanStudio.Lsp;

namespace LeanStudio.Core.Projects;

/// <summary>
/// A folder Lean Studio has open: a Lake project (it has a lakefile), a bare folder pinned to a toolchain (it has
/// only a <c>lean-toolchain</c>), or neither. The kind decides how the language server starts and what "build" means.
/// </summary>
public sealed class LeanProject
{
    /// <summary>The name of the file that pins a folder's Lean toolchain.</summary>
    public const string ToolchainFile = "lean-toolchain";
    private static readonly string[] Lakefiles = ["lakefile.lean", "lakefile.toml"];

    /// <summary>
    /// A project rooted at <paramref name="root"/>, made absolute. Nothing is read or checked here; use
    /// <see cref="FindEnclosing"/> to locate the project a file belongs to.
    /// </summary>
    public LeanProject(string root)
    {
        Root = Path.GetFullPath(root);
    }

    /// <summary>The absolute path of the project folder.</summary>
    public string Root { get; }

    /// <summary>The folder's name.</summary>
    public string Name => Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>
    /// The full path of <c>lakefile.lean</c>, else <c>lakefile.toml</c>, or <see langword="null"/> when there is
    /// neither. Checks the disk on every access.
    /// </summary>
    public string? Lakefile => Lakefiles.Select(f => Path.Combine(Root, f)).FirstOrDefault(File.Exists);

    /// <summary>The folder has a lakefile (checked on every access).</summary>
    public bool IsLakeProject => Lakefile is not null;

    /// <summary>The full path of the project's <c>lean-toolchain</c> file, whether or not it exists.</summary>
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

    /// <summary>Write <paramref name="toolchain"/> (trimmed, with a trailing newline) to <c>lean-toolchain</c>, creating or replacing it.</summary>
    public void SetToolchain(string toolchain) => File.WriteAllText(ToolchainPath, toolchain.Trim() + "\n");

    /// <summary>Where Lake puts the project's compiled <c>.olean</c> files: <c>.lake/build/lib/lean</c>.</summary>
    public string BuildLibDirectory => Path.Combine(Root, ".lake", "build", "lib", "lean");

    /// <summary>Where Lake checks out dependencies: <c>.lake/packages</c>.</summary>
    public string PackagesDirectory => Path.Combine(Root, ".lake", "packages");

    /// <summary>The full path of <c>lake-manifest.json</c>, which records the resolved dependency versions.</summary>
    public string ManifestPath => Path.Combine(Root, "lake-manifest.json");

    /// <summary>
    /// Whether the project depends on Mathlib, which decides whether "get cache" is offered. A text search of the
    /// manifest and lakefile, read from disk on every access.
    /// </summary>
    public bool DependsOnMathlib =>
        (File.Exists(ManifestPath) && File.ReadAllText(ManifestPath).Contains("\"mathlib\"", StringComparison.OrdinalIgnoreCase))
        || (Lakefile is string lf && File.ReadAllText(lf).Contains("mathlib", StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether this is the Mathlib repository itself (its lakefile declares the package <c>mathlib</c>).</summary>
    public bool IsMathlib => Lakefile is string lf && System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(lf),
        @"^\s*(?:name\s*=\s*""mathlib""|package\s+mathlib\b)", System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>
    /// Whether the project depends on Batteries, directly or through Mathlib (its lake-manifest lists it), so its
    /// linters (<c>lake exe runLinter</c>) are available.
    /// </summary>
    public bool DependsOnBatteries =>
        File.Exists(ManifestPath) && File.ReadAllText(ManifestPath).Contains("\"name\": \"batteries\"", StringComparison.Ordinal);

    /// <summary>
    /// The nearest folder at or above <paramref name="path"/> (a file or a folder) with a lakefile, else the nearest
    /// with a toolchain file, else <see langword="null"/>. Folders inside <c>.lake</c> are skipped, so a file in a
    /// dependency belongs to the outer project.
    /// </summary>
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

    /// <summary>
    /// The Lean module a source file defines: <c>Root/Foo/Bar.lean</c> is <c>Foo.Bar</c>. <see langword="null"/> when
    /// the file is not a <c>.lean</c> file under <see cref="Root"/>.
    /// </summary>
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

    /// <summary>
    /// The path of the compiled <c>.olean</c> for a source file, or <see langword="null"/> if Lake has not built it
    /// (or the file is not a module of this project). Does not check whether it is up to date.
    /// </summary>
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

    /// <summary>
    /// Lean source files in the project, as full paths, lazily. Skips folders whose names start with <c>.</c>
    /// (so build output and dependencies in <c>.lake</c>) or are <c>build</c>, and folders that cannot be read.
    /// </summary>
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
