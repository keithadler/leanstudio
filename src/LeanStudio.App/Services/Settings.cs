using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeanStudio.App.Services;

/// <summary>What Lean Studio remembers between runs, kept as JSON in the user's application data folder.</summary>
public sealed class Settings
{
    /// <summary><c>Dark</c> or <c>Light</c>; anything else is treated as dark.</summary>
    public string Theme { get; set; } = "Dark";
    /// <summary>The editor's font size, in device-independent pixels.</summary>
    public double EditorFontSize { get; set; } = 14;
    /// <summary>The editor font, as a comma-separated list of families to try in order.</summary>
    public string EditorFontFamily { get; set; } = "JuliaMono, Cascadia Code, SF Mono, Menlo, Consolas, DejaVu Sans Mono, Noto Sans Mono, monospace";
    /// <summary>Whether the editor shows line numbers.</summary>
    public bool ShowLineNumbers { get; set; } = true;
    /// <summary>Whether typing <c>\alpha</c> and the like in the editor inserts the Unicode symbol.</summary>
    public bool UnicodeInput { get; set; } = true;
    /// <summary>After a successful build, re-check what was built with Tenet.</summary>
    public bool VerifyAfterBuild { get; set; } = true;
    /// <summary>Toolchain for files outside any project, when elan has no default.</summary>
    public string? FallbackToolchain { get; set; }
    /// <summary>Root folders of recently opened projects, most recent first (at most 12; see <see cref="RememberProject"/>).</summary>
    public List<string> RecentProjects { get; set; } = [];
    /// <summary>The root folder of the project open when Lean Studio last closed, reopened on start.</summary>
    public string? LastProject { get; set; }
    /// <summary>Full paths of the files open when Lean Studio last closed, reopened on start if they still exist.</summary>
    public List<string> LastOpenFiles { get; set; } = [];
    /// <summary>Tutorial lessons solved, by file name.</summary>
    public HashSet<string> CompletedLessons { get; set; } = [];
    /// <summary>Show each goal read aloud in English under it, for newcomers.</summary>
    public bool ShowGoalsInEnglish { get; set; } = true;
    /// <summary>Show #eval and #check results at the end of their line.</summary>
    public bool InlineResults { get; set; } = true;

    /// <summary>Colour bound variables and fields as Lean classifies them, over the grammar's highlighting.</summary>
    public bool SemanticHighlighting { get; set; } = true;

    /// <summary>Show Lean's inlay hints in the text, such as implicit arguments it binds automatically.</summary>
    public bool InlayHints { get; set; } = true;

    /// <summary>Edit with Vim's keys: normal, insert and visual modes, motions, operators and ex commands.</summary>
    public bool VimMode { get; set; }

    /// <summary>Edit with Emacs's keys: C-f, M-f, C-k, C-y, the mark and region, C-x C-s. Vim mode wins if both are on.</summary>
    public bool EmacsMode { get; set; }
    /// <summary>Mark the end of each proof: ✔ when it's finished, "⊢ goals left" when it isn't.</summary>
    public bool ShowProofMarks { get; set; } = true;

    /// <summary>The Tactic State leaves out hypotheses that are types (<c>α : Type</c>).</summary>
    public bool HideTypeAssumptions { get; set; }

    /// <summary>The Tactic State leaves out type class instances.</summary>
    public bool HideInstanceAssumptions { get; set; }

    /// <summary>The Tactic State leaves out inaccessible names (<c>n✝</c>).</summary>
    public bool HideInaccessibleNames { get; set; }

    /// <summary>The Tactic State shows let variables without their values.</summary>
    public bool HideLetValues { get; set; }

    /// <summary>The Tactic State shows each goal's target before its hypotheses.</summary>
    public bool GoalBeforeAssumptions { get; set; }

    /// <summary>Explain Lean's error messages in plain words.</summary>
    public bool ExplainErrors { get; set; } = true;
    /// <summary>Whether to check for a new release on start (at most every 20 hours).</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Save a file a moment after you stop typing, and everything when the window loses focus.</summary>
    public bool AutoSave { get; set; }
    /// <summary>Who last changed the current line, in the status bar.</summary>
    public bool ShowBlame { get; set; } = true;
    /// <summary>When exact?, simp? and the like find exactly one answer, put it in the proof.</summary>
    public bool AutoApplyFixes { get; set; }
    /// <summary>Whether the editor wraps long lines.</summary>
    public bool WordWrap { get; set; }
    /// <summary>The full path of the file that was active when Lean Studio last closed.</summary>
    public string? LastActiveFile { get; set; }
    /// <summary>The last command run with Run a shell command, offered again next time.</summary>
    public string? LastShellCommand { get; set; }
    /// <summary>Where the cursor was in each file, restored when it is opened again: full path to 0-based [line, column].</summary>
    public Dictionary<string, int[]> CaretPositions { get; set; } = [];
    /// <summary>When updates were last checked for, in UTC; null if never.</summary>
    public DateTime? LastUpdateCheck { get; set; }
    /// <summary>The release tag the user chose to skip; that version is not offered again automatically.</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>
    /// The folder settings (and the crash log) are kept in: <c>LeanStudio</c> in the application data folder, or
    /// <c>$LEANSTUDIO_SETTINGS_DIR</c> when set (for tests).
    /// </summary>
    [JsonIgnore]
    public static string Directory =>
        Environment.GetEnvironmentVariable("LEANSTUDIO_SETTINGS_DIR") is { Length: > 0 } d
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LeanStudio");

    /// <summary>The settings file, <c>settings.json</c> in <see cref="Directory"/>.</summary>
    [JsonIgnore]
    public static string FilePath => Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Read the settings file, or return defaults when it is missing, unreadable or not valid JSON.</summary>
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

    /// <summary>Write the settings to <see cref="FilePath"/>, creating the folder. Errors writing the file are ignored.</summary>
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

    /// <summary>
    /// Put <paramref name="root"/> at the top of <see cref="RecentProjects"/> (keeping 12) and make it
    /// <see cref="LastProject"/>. Does not save.
    /// </summary>
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
