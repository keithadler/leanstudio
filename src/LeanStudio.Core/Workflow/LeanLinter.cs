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
    /// <summary>The release of lean-linter that is built when the newest one cannot be found or does not build.</summary>
    public const string Release = "v0.5.2";

    private const string Repository = "https://github.com/keithadler/lean-linter";

    /// <summary>Where the builds of <paramref name="release"/> are kept, one folder per Lean version.</summary>
    /// <param name="release">A release tag, such as <c>v0.5.0</c>.</param>
    public static string CacheRoot(string release = Release) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LeanStudio", "lean-linter", release);

    /// <summary>The folder for a toolchain such as <c>leanprover/lean4:v4.35.0-rc4</c>.</summary>
    /// <param name="toolchain">The project's toolchain.</param>
    /// <param name="release">A release tag.</param>
    public static string FolderFor(string toolchain, string release = Release) =>
        Path.Combine(CacheRoot(release), new string([.. toolchain.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_')]));

    /// <summary>The executable of <paramref name="release"/> built for <paramref name="toolchain"/>, or null when it has not been built.</summary>
    /// <param name="toolchain">The project's toolchain.</param>
    /// <param name="release">A release tag.</param>
    public static string? BuiltFor(string toolchain, string release = Release)
    {
        string exe = Path.Combine(FolderFor(toolchain, release), ".lake", "build", "bin", OperatingSystem.IsWindows() ? "lean-linter.exe" : "lean-linter");
        return File.Exists(exe) ? exe : null;
    }

    private static string? _latest;

    /// <summary>
    /// The newest <c>vX.Y.Z</c> tag of lean-linter, asked of GitHub once per run of the app; null when it cannot be reached
    /// within a few seconds (offline, or git missing).
    /// </summary>
    /// <param name="ct">Cancels the lookup.</param>
    public static async Task<string?> LatestReleaseAsync(CancellationToken ct = default)
    {
        if (_latest is not null)
        {
            return _latest;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            ProcessResult r = await ProcessRunner.RunAsync("git", ["ls-remote", "--tags", "--refs", Repository], ct: timeout.Token).ConfigureAwait(false);
            _latest = r.Success ? NewestTag(r.Output) : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _latest = null;
        }
        return _latest;
    }

    /// <summary>The highest <c>vX.Y.Z</c> among the tags in <paramref name="lsRemoteOutput"/> (<c>git ls-remote --tags</c>), or null.</summary>
    /// <param name="lsRemoteOutput">Lines of <c>hash refs/tags/name</c>.</param>
    public static string? NewestTag(string lsRemoteOutput) =>
        lsRemoteOutput.Split('\n')
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"refs/tags/(v(\d+)\.(\d+)\.(\d+))\s*$"))
            .Where(m => m.Success)
            .Select(m => (Tag: m.Groups[1].Value, Version: new Version(int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture))))
            .OrderByDescending(x => x.Version)
            .Select(x => x.Tag)
            .FirstOrDefault();

    /// <summary>
    /// Clone and build the newest lean-linter with the project's Lean version, unless that is already done; if it cannot be
    /// found or does not build, the release this version of Lean Studio was made with. Returns the executable.
    /// </summary>
    /// <param name="project">The project, whose toolchain decides the build.</param>
    /// <param name="onLine">Called with each line git and Lake print.</param>
    /// <param name="ct">Cancels the build.</param>
    public static async Task<(string? Exe, string? Error)> EnsureBuiltAsync(LeanProject project, Action<string>? onLine = null, CancellationToken ct = default)
    {
        if (project.Toolchain is not string toolchain)
        {
            return (null, "lean-linter has to be built with the project's Lean version, and the project has no lean-toolchain file.");
        }
        string latest = await LatestReleaseAsync(ct).ConfigureAwait(false) ?? Release;
        (string? exe, string? error) = await BuildAsync(toolchain, latest, onLine, ct).ConfigureAwait(false);
        if (exe is null && latest != Release)
        {
            onLine?.Invoke($"lean-linter {latest} did not build; using {Release}.");
            (exe, error) = await BuildAsync(toolchain, Release, onLine, ct).ConfigureAwait(false);
        }
        return (exe, error);
    }

    private static async Task<(string? Exe, string? Error)> BuildAsync(string toolchain, string release, Action<string>? onLine, CancellationToken ct)
    {
        if (BuiltFor(toolchain, release) is string built)
        {
            return (built, null);
        }
        string folder = FolderFor(toolchain, release);
        if (!Directory.Exists(Path.Combine(folder, ".git")))
        {
            Directory.CreateDirectory(CacheRoot(release));
            ProcessResult clone = await ProcessRunner.RunAsync("git",
                ["clone", "--depth", "1", "--branch", release, Repository, folder], onLine: onLine, ct: ct).ConfigureAwait(false);
            if (!clone.Success)
            {
                return (null, "Could not fetch lean-linter: " + clone.Output.Trim());
            }
        }
        // Its own lean-toolchain is the one it was developed on; the library it reads decides which one it needs.
        await File.WriteAllTextAsync(Path.Combine(folder, LeanProject.ToolchainFile), toolchain + "\n", ct).ConfigureAwait(false);
        string lake = Elan.FindExecutable("lake") ?? "lake";
        ProcessResult build = await ProcessRunner.RunAsync(lake, ["build"], folder, onLine, ct: ct).ConfigureAwait(false);
        return BuiltFor(toolchain, release) is string exe
            ? (exe, null)
            : (null, $"lean-linter {release} did not build with {toolchain}: " + build.Output.Trim());
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
