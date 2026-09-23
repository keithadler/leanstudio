using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeanStudio.App.Services;

/// <summary>What Lean Studio remembers between runs, kept as JSON in the user's application data folder.</summary>
public sealed class Settings
{
    public string Theme { get; set; } = "Dark";
    public double EditorFontSize { get; set; } = 14;
    public string EditorFontFamily { get; set; } = "JuliaMono, Cascadia Code, SF Mono, Menlo, Consolas, DejaVu Sans Mono, Noto Sans Mono, monospace";
    public bool ShowLineNumbers { get; set; } = true;
    public bool UnicodeInput { get; set; } = true;
    /// <summary>After a successful build, re-check what was built with Tenet.</summary>
    public bool VerifyAfterBuild { get; set; } = true;
    /// <summary>Toolchain for files outside any project, when elan has no default.</summary>
    public string? FallbackToolchain { get; set; }
    public List<string> RecentProjects { get; set; } = [];
    public string? LastProject { get; set; }
    public List<string> LastOpenFiles { get; set; } = [];

    [JsonIgnore]
    public static string Directory =>
        Environment.GetEnvironmentVariable("LEANSTUDIO_SETTINGS_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LeanStudio");

    [JsonIgnore]
    public static string FilePath => Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void RememberProject(string root)
    {
        RecentProjects.RemoveAll(p => string.Equals(p, root, StringComparison.Ordinal));
        RecentProjects.Insert(0, root);
        if (RecentProjects.Count > 12)
        {
            RecentProjects.RemoveRange(12, RecentProjects.Count - 12);
        }
        LastProject = root;
    }
}
