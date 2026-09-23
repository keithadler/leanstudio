using System.Diagnostics;

namespace LeanStudio.Tests;

/// <summary>Where the tests find Lean, and a way to skip the ones that need it when it is not installed.</summary>
internal static class Lean
{
    public const string Toolchain = "leanprover/lean4:v4.34.0";

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
