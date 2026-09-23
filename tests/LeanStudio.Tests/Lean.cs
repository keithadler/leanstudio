using System.Diagnostics;

namespace LeanStudio.Tests;

/// <summary>Where the tests find Lean, and a way to skip the ones that need it when it is not installed.</summary>
internal static class Lean
{
    public const string Toolchain = "leanprover/lean4:v4.34.0";

    /// <summary>
    /// Tests that start a Lean server run one at a time: several servers elaborating at once on a small CI
    /// machine (Windows especially) can take longer than any sensible timeout, and they test nothing extra.
    /// </summary>
    public const string Collection = "Lean";

    /// <summary>How long to wait for Lean to finish a small file, generous for a cold CI runner.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(240);

    public static string RepoRoot { get; } = FindRepoRoot();

    public static string Sample(params string[] parts) => Path.Combine([RepoRoot, "samples", .. parts]);

    private static string FindRepoRoot()
    {
        string? d = AppContext.BaseDirectory;
        while (d is not null && !File.Exists(Path.Combine(d, "LeanStudio.slnx")))
        {
            d = Path.GetDirectoryName(d);
        }
        return d ?? throw new InvalidOperationException("could not find the repository root");
    }

    private static readonly Lazy<string?> LeanPath = new(() => Core.Toolchains.Elan.FindExecutable("lean"));

    public static string? Executable => LeanPath.Value;

    public static void RequireLean()
    {
        Assert.SkipWhen(Executable is null, "lean is not installed (install elan to run these tests)");
    }

    public static string RunLean(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo(Executable!) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        using var p = Process.Start(psi)!;
        string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return o;
    }
}
