using LeanStudio.Core.Processes;
using LeanStudio.Core.Toolchains;

namespace LeanStudio.Core.Projects;

/// <summary>The project templates <c>lake new</c> offers (see <see cref="Lake.TemplateName"/>).</summary>
public enum ProjectTemplate
{
    /// <summary>A library and an executable (<c>lake new name</c>).</summary>
    Standard,
    /// <summary>A library only.</summary>
    Library,
    /// <summary>A library that depends on Mathlib.</summary>
    Math,
    /// <summary>An executable only.</summary>
    Executable,
}

/// <summary>Lake, Lean's build tool: building, fetching Mathlib's cache, updating dependencies, and new projects.</summary>
public static class Lake
{
    private static string Exe => Elan.FindExecutable("lake") ?? "lake";

    /// <summary>
    /// Run <c>lake build</c> in the project folder, for <paramref name="target"/> or, when it is <see langword="null"/>,
    /// the default targets. Output is streamed to <paramref name="onLine"/>.
    /// </summary>
    public static Task<ProcessResult> BuildAsync(LeanProject project, string? target = null, Action<string>? onLine = null, CancellationToken ct = default) =>
        ProcessRunner.RunAsync(Exe, target is null ? ["build"] : ["build", target], project.Root, onLine, ct: ct);

    /// <summary>
    /// Download Mathlib's prebuilt .olean files instead of compiling Mathlib, which takes hours
    /// (<c>lake exe cache get</c>). Only meaningful when the project depends on Mathlib.
    /// </summary>
    public static Task<ProcessResult> GetCacheAsync(LeanProject project, Action<string>? onLine = null, CancellationToken ct = default) =>
        ProcessRunner.RunAsync(Exe, ["exe", "cache", "get"], project.Root, onLine, ct: ct);

    /// <summary>
    /// In the Mathlib repository: fetch the cache only for <paramref name="files"/> and what they import
    /// (<c>lake exe cache get</c> with their paths relative to the root).
    /// </summary>
    public static Task<ProcessResult> GetCacheForAsync(LeanProject project, IEnumerable<string> files, Action<string>? onLine = null, CancellationToken ct = default) =>
        ProcessRunner.RunAsync(Exe, ["exe", "cache", "get", .. files.Select(f => Path.GetRelativePath(project.Root, f))], project.Root, onLine, ct: ct);

    /// <summary>
    /// Run <c>lake update</c>, which moves dependencies to the newest versions the lakefile allows and rewrites
    /// <c>lake-manifest.json</c>.
    /// </summary>
    public static Task<ProcessResult> UpdateAsync(LeanProject project, Action<string>? onLine = null, CancellationToken ct = default) =>
        ProcessRunner.RunAsync(Exe, ["update"], project.Root, onLine, ct: ct);

    /// <summary>Run <c>lake clean</c>, deleting the project's build output (not its dependencies).</summary>
    public static Task<ProcessResult> CleanAsync(LeanProject project, Action<string>? onLine = null, CancellationToken ct = default) =>
        ProcessRunner.RunAsync(Exe, ["clean"], project.Root, onLine, ct: ct);

    /// <summary>Lake's name for a template, as <c>lake new</c> takes it.</summary>
    public static string TemplateName(ProjectTemplate t) => t switch
    {
        ProjectTemplate.Library => "lib",
        ProjectTemplate.Math => "math",
        ProjectTemplate.Executable => "exe",
        _ => "std",
    };

    /// <summary>
    /// Create <paramref name="parent"/>/<paramref name="name"/> with <c>lake new</c>, using the given toolchain
    /// (so it works with no elan default). The new project's <c>lean-toolchain</c> pins that toolchain, except that
    /// the Mathlib template keeps the toolchain Mathlib chose. Creates <paramref name="parent"/> if needed.
    /// </summary>
    /// <returns>The <c>lake new</c> result, and the new project, or <see langword="null"/> when it failed.</returns>
    public static async Task<(ProcessResult Result, LeanProject? Project)> NewAsync(
        string parent, string name, ProjectTemplate template, string toolchain, Action<string>? onLine = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(parent);
        ProcessResult r = await ProcessRunner.RunAsync(
            Exe, ["+" + toolchain, "new", name, TemplateName(template)], parent, onLine, ct: ct).ConfigureAwait(false);
        string root = Path.Combine(parent, name);
        if (!r.Success || !Directory.Exists(root))
        {
            return (r, null);
        }
        var project = new LeanProject(root);
        // The math template pins the toolchain Mathlib wants; the others should say the one that was chosen.
        if (template != ProjectTemplate.Math || project.Toolchain is null)
        {
            project.SetToolchain(toolchain);
        }
        return (r, project);
    }

    /// <summary>
    /// Is the name one Lake accepts as a package name? It must start with a letter or <c>_</c> and contain only
    /// letters, digits, <c>_</c> and <c>-</c>.
    /// </summary>
    public static bool IsValidName(string name) =>
        name.Length > 0 && (char.IsLetter(name[0]) || name[0] == '_')
        && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');
}
