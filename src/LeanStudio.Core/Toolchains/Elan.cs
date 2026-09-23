using System.Runtime.InteropServices;
using LeanStudio.Core.Processes;

namespace LeanStudio.Core.Toolchains;

public sealed record Toolchain(string Name, bool IsDefault)
{
    /// <summary>The part after the channel, e.g. <c>v4.34.0</c> from <c>leanprover/lean4:v4.34.0</c>.</summary>
    public string Version => Name.Contains(':', StringComparison.Ordinal) ? Name[(Name.IndexOf(':', StringComparison.Ordinal) + 1)..] : Name;
}

/// <summary>
/// elan, Lean's toolchain manager: where it lives, what it has installed, and installing or removing toolchains.
/// Every Lean executable Lean Studio runs goes through elan's proxies, so a project's <c>lean-toolchain</c> file
/// decides the Lean version and nothing here has to.
/// </summary>
public static class Elan
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public static string Home =>
        Environment.GetEnvironmentVariable("ELAN_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".elan");

    public static string BinDirectory => Path.Combine(Home, "bin");

    public static string ToolchainsDirectory => Path.Combine(Home, "toolchains");

    /// <summary>Find <paramref name="name"/> (lean, lake, elan) in elan's bin directory, then on PATH.</summary>
    public static string? FindExecutable(string name)
    {
        string file = IsWindows ? name + ".exe" : name;
        string inElan = Path.Combine(BinDirectory, file);
        if (File.Exists(inElan))
        {
            return inElan;
        }
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, file);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    public static bool IsInstalled => FindExecutable("elan") is not null;

    /// <summary>The page with elan's installer for this platform.</summary>
    public static string InstallUrl => "https://github.com/leanprover/elan#installation";

    public static async Task<IReadOnlyList<Toolchain>> ListAsync(CancellationToken ct = default)
    {
        string? elan = FindExecutable("elan");
        if (elan is null)
        {
            return [];
        }
        ProcessResult r = await ProcessRunner.RunAsync(elan, ["toolchain", "list"], ct: ct).ConfigureAwait(false);
        return ParseList(r.Output);
    }

    /// <summary>Parse <c>elan toolchain list</c>: one per line, the default marked "(default)".</summary>
    public static IReadOnlyList<Toolchain> ParseList(string output)
    {
        var list = new List<Toolchain>();
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("no installed", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("error", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            bool isDefault = line.Contains("(default)", StringComparison.Ordinal);
            string name = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            list.Add(new Toolchain(name, isDefault));
        }
        return list;
    }

    public static Task<ProcessResult> InstallAsync(string toolchain, Action<string>? onLine = null, CancellationToken ct = default) =>
        RunElanAsync(["toolchain", "install", toolchain], onLine, ct);

    public static Task<ProcessResult> UninstallAsync(string toolchain, Action<string>? onLine = null, CancellationToken ct = default) =>
        RunElanAsync(["toolchain", "uninstall", toolchain], onLine, ct);

    public static Task<ProcessResult> SetDefaultAsync(string toolchain, Action<string>? onLine = null, CancellationToken ct = default) =>
        RunElanAsync(["default", toolchain], onLine, ct);

    public static Task<ProcessResult> SelfUpdateAsync(Action<string>? onLine = null, CancellationToken ct = default) =>
        RunElanAsync(["self", "update"], onLine, ct);

    private static Task<ProcessResult> RunElanAsync(IEnumerable<string> args, Action<string>? onLine, CancellationToken ct)
    {
        string? elan = FindExecutable("elan");
        if (elan is null)
        {
            const string msg = "elan is not installed";
            onLine?.Invoke(msg);
            return Task.FromResult(new ProcessResult(-1, msg));
        }
        return ProcessRunner.RunAsync(elan, args, onLine: onLine, ct: ct);
    }

    /// <summary>
    /// The directory of an installed toolchain. elan names them by replacing '/' and ':' with "--" and '-'
    /// (leanprover/lean4:v4.34.0 is leanprover--lean4---v4.34.0).
    /// </summary>
    public static string ToolchainDirectory(string toolchain) =>
        Path.Combine(ToolchainsDirectory, toolchain.Replace("/", "--", StringComparison.Ordinal).Replace(":", "---", StringComparison.Ordinal));
}
