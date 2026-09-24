namespace LeanStudio.App.Services;

/// <summary>Who made Lean Studio, in one place so the About box, the welcome screen and the bundle all agree.</summary>
public static class Credits
{
    /// <summary>The author's name.</summary>
    public const string Author = "Keith Adler";
    /// <summary>The author's handle on X.</summary>
    public const string XHandle = "@keithadler";
    /// <summary>The author's profile on X.</summary>
    public const string XUrl = "https://x.com/keithadler";
    /// <summary>Lean Studio's source repository.</summary>
    public const string RepositoryUrl = "https://github.com/keithadler/leanstudio";
    /// <summary>The repository of Tenet, the independent Lean 4 kernel Lean Studio re-checks builds with.</summary>
    public const string TenetUrl = "https://github.com/keithadler/tenet";

    /// <summary>The credit line: author and handle.</summary>
    public static string CreatedBy => $"Created by {Author} ({XHandle} on X)";

    /// <summary>The copyright and license line.</summary>
    public static string Copyright => $"© 2026 {Author} ({XHandle}). MIT License.";

    /// <summary>The app's version as <c>major.minor.patch</c>, from the assembly; empty if it has none.</summary>
    public static string Version =>
        typeof(Credits).Assembly.GetName().Version?.ToString(3) ?? "";
}
