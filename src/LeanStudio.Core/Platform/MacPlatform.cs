using System.Runtime.InteropServices;

namespace LeanStudio.Core.Platform;

/// <summary>
/// What Lean Studio needs to know about the Mac it runs on. macOS 27 runs only on Apple silicon and is the last
/// release with Rosetta for apps, so an Intel build running translated on an Apple silicon Mac should move to the
/// Apple silicon build.
/// </summary>
public static class MacPlatform
{
    private static readonly Lazy<bool> Translated = new(() =>
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return false;
        }
        try
        {
            // 1 when this process is an Intel binary translated by Rosetta; the key is missing on Intel Macs.
            var psi = new System.Diagnostics.ProcessStartInfo("/usr/sbin/sysctl", "-in sysctl.proc_translated")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return output == "1";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    });

    /// <summary>This is the Intel build, running under Rosetta on an Apple silicon Mac.</summary>
    public static bool IsTranslated => Translated.Value;

    /// <summary>The Mac's processor is Apple silicon, whichever build is running.</summary>
    public static bool IsAppleSilicon =>
        OperatingSystem.IsMacOS() && (RuntimeInformation.OSArchitecture == Architecture.Arm64 || IsTranslated);

    /// <summary>
    /// A sentence for the Output panel when the Intel build runs under Rosetta; null otherwise.
    /// </summary>
    public static string? RosettaNotice => IsTranslated
        ? "This is the Intel build of Lean Studio, running under Rosetta. macOS 27 is the last macOS to run Intel apps: "
          + "install the Apple silicon build (Help ▸ Check for Updates offers it), which is also faster."
        : null;
}
