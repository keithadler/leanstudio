namespace LeanStudio.Core.Platform;

/// <summary>
/// Whether Lean Studio was installed from the Microsoft Store, and what that changes. A Store install lives in a
/// versioned folder under <c>C:\Program Files\WindowsApps</c>: the Store updates it, so the app does not offer its
/// own updates, and other programs start it through the <c>leanstudio</c> app execution alias rather than by that
/// folder's path, which changes with every update and which other programs may not run directly.
/// </summary>
public static class StorePackage
{
    /// <summary>The command the package registers (an app execution alias), the same name winget uses.</summary>
    public const string Alias = "leanstudio.exe";

    /// <summary>This process is a Microsoft Store install.</summary>
    public static bool IsStoreInstall { get; } = OperatingSystem.IsWindows() && IsPackagedPath(Environment.ProcessPath);

    /// <summary>Whether <paramref name="exePath"/> is inside a Store package's install folder.</summary>
    public static bool IsPackagedPath(string? exePath) =>
        exePath is not null && exePath.Replace('/', '\\').Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The full path of the <c>leanstudio</c> alias, which Windows keeps in the person's
    /// <c>%LOCALAPPDATA%\Microsoft\WindowsApps</c> (a folder on their PATH) and points at the current version.
    /// </summary>
    public static string AliasPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", Alias);
}
