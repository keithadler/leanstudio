namespace LeanStudio.App.Services;

/// <summary>Who made Lean Studio, in one place so the About box, the welcome screen and the bundle all agree.</summary>
public static class Credits
{
    public const string Author = "Keith Adler";
    public const string XHandle = "@keithadler";
    public const string XUrl = "https://x.com/keithadler";
    public const string RepositoryUrl = "https://github.com/keithadler/leanstudio";
    public const string TenetUrl = "https://github.com/keithadler/tenet";

    public static string CreatedBy => $"Created by {Author} ({XHandle} on X)";

    public static string Copyright => $"© 2026 {Author} ({XHandle}). MIT License.";

    public static string Version =>
        typeof(Credits).Assembly.GetName().Version?.ToString(3) ?? "";
}
