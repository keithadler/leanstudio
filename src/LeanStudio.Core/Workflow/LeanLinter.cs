using System.Text.Json;
using LeanStudio.Core.Processes;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Toolchains;

namespace LeanStudio.Core.Workflow;

/// <summary>One thing lean-linter found.</summary>
/// <param name="Check">The check, such as <c>mislabel</c> or <c>doc-refs</c>.</param>
/// <param name="Declaration">The declaration it is about.</param>
/// <param name="Module">The module the declaration is in.</param>
/// <param name="Line">0-based line of the declaration.</param>
/// <param name="Message">What is wrong.</param>
public sealed record LeanLinterFinding(string Check, string Declaration, string Module, int Line, string Message);

/// <summary>
/// <a href="https://github.com/keithadler/lean-linter">lean-linter</a>: what Lean's type checker cannot see, such as
/// lemmas whose names say the opposite of their statements, docstrings that name declarations that no longer exist,
/// duplicates and dead private code. It reads compiled <c>.olean</c> files, so it is built with the project's own Lean
/// version (once per version, kept under the application data folder) and the file's module has to be built.
/// </summary>
public static class LeanLinter
{
    /// <summary>The release of lean-linter that is built.</summary>
    public const string Release = "v0.5.0";

    private const string Repository = "https://github.com/keithadler/lean-linter";

    /// <summary>Where the builds are kept, one folder per Lean version.</summary>
    public static string CacheRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LeanStudio", "lean-linter", Release);

    /// <summary>The folder for a toolchain such as <c>leanprover/lean4:v4.35.0-rc4</c>.</summary>
    public static string FolderFor(string toolchain) =>
        Path.Combine(CacheRoot, new string([.. toolchain.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_')]));

    /// <summary>The executable built for <paramref name="toolchain"/>, or null when it has not been built.</summary>
    public static string? BuiltFor(string toolchain)
    {
        string exe = Path.Combine(FolderFor(toolchain), ".lake", "build", "bin", OperatingSystem.IsWindows() ? "lean-linter.exe" : "lean-linter");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>Clone and build lean-linter with the project's Lean version, unless that is already done. Returns the executable.</summary>
    public static async Task<(string? Exe, string? Error)> EnsureBuiltAsync(LeanProject project, Action<string>? onLine = null, CancellationToken ct = default)
    {
        if (project.Toolchain is not string toolchain)
        {
            return (null, "lean-linter has to be built with the project's Lean version, and the project has no lean-toolchain file.");
        }
        if (BuiltFor(toolchain) is string built)
        {
            return (built, null);
        }
        string folder = FolderFor(toolchain);
        if (!Directory.Exists(Path.Combine(folder, ".git")))
        {
            Directory.CreateDirectory(CacheRoot);
            ProcessResult clone = await ProcessRunner.RunAsync("git",
                ["clone", "--depth", "1", "--branch", Release, Repository, folder], onLine: onLine, ct: ct).ConfigureAwait(false);
            if (!clone.Success)
            {
                return (null, "Could not fetch lean-linter: " + clone.Output.Trim());
            }
        }
        // Its own lean-toolchain is the one it was developed on; the library it reads decides which one it needs.
        await File.WriteAllTextAsync(Path.Combine(folder, LeanProject.ToolchainFile), toolchain + "\n", ct).ConfigureAwait(false);
        string lake = Elan.FindExecutable("lake") ?? "lake";
        ProcessResult build = await ProcessRunner.RunAsync(lake, ["build"], folder, onLine, ct: ct).ConfigureAwait(false);
        return BuiltFor(toolchain) is string exe
            ? (exe, null)
            : (null, $"lean-linter did not build with {toolchain}: " + build.Output.Trim());
    }

    /// <summary>The source file of <paramref name="module"/> in <paramref name="project"/>.</summary>
    public static string FileOf(LeanProject project, string module) =>
        Path.Combine(project.Root, Path.Combine(module.Split('.')) + ".lean");

    /// <summary>
    /// Run lean-linter on the module of <paramref name="file"/> and keep what it says about that module. Returns the
    /// findings, or why it could not run (not built, toolchain it cannot build with).
    /// </summary>
    public static async Task<(IReadOnlyList<LeanLinterFinding> Findings, string? Error)> RunAsync(
        LeanProject project, string file, Action<string>? onLine = null, CancellationToken ct = default)
    {
        if (!project.IsLakeProject || project.ModuleNameOf(file) is not string module)
        {
            return ([], "lean-linter needs a file in a Lake project's library.");
        }
        if (project.OleanOf(file) is null)
        {
            return ([], $"{module} is not built yet: lean-linter reads compiled modules, so run Build first.");
        }
        (string? exe, string? error) = await EnsureBuiltAsync(project, onLine, ct).ConfigureAwait(false);
        if (exe is null)
        {
            return ([], error);
        }
        string lake = Elan.FindExecutable("lake") ?? "lake";
        // `lake env` gives it the project's LEAN_PATH, so it finds the module and everything it imports.
        ProcessResult r = await ProcessRunner.RunAsync(lake,
            ["env", exe, "--json", "--root", module, module], project.Root, ct: ct).ConfigureAwait(false);
        IReadOnlyList<LeanLinterFinding> all = Parse(r.Output);
        // Exit status 1 means it found something; 2 is a usage error, and anything else it could not read the module.
        if (all.Count == 0 && r.ExitCode > 1)
        {
            return ([], r.Output.Trim());
        }
        return ([.. all.Where(f => f.Module == module)], null);
    }

    /// <summary>
    /// Read <c>--json</c> output: one object per line. Its summary goes to stderr, which arrives in the same stream,
    /// so a line that is not a finding is skipped.
    /// </summary>
    public static IReadOnlyList<LeanLinterFinding> Parse(string output)
    {
        var findings = new List<LeanLinterFinding>();
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith('{'))
            {
                continue;
            }
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement o = doc.RootElement;
                if (o.TryGetProperty("check", out JsonElement check) && o.TryGetProperty("declaration", out JsonElement decl)
                    && o.TryGetProperty("module", out JsonElement module) && o.TryGetProperty("line", out JsonElement ln)
                    && o.TryGetProperty("message", out JsonElement message) && ln.TryGetInt32(out int n))
                {
                    findings.Add(new LeanLinterFinding(check.GetString() ?? "", decl.GetString() ?? "", module.GetString() ?? "", Math.Max(0, n - 1), message.GetString() ?? ""));
                }
            }
            catch (JsonException)
            {
            }
        }
        return findings;
    }
}
