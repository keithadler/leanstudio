using System.Diagnostics;
using System.Text.RegularExpressions;
using LeanStudio.Core.Processes;
using LeanStudio.Core.Toolchains;

namespace LeanStudio.Core.Workflow;

/// <summary>
/// Installs elan, Lean's toolchain manager, with its official installer and the latest stable Lean, for someone
/// who has nothing yet. The same thing the Lean documentation tells people to paste into a terminal, done for them.
/// </summary>
public static class ElanInstaller
{
    /// <summary>elan's official installer script for macOS and Linux.</summary>
    public const string UnixScript = "https://raw.githubusercontent.com/leanprover/elan/master/elan-init.sh";
    /// <summary>elan's official installer script for Windows (PowerShell).</summary>
    public const string WindowsScript = "https://raw.githubusercontent.com/leanprover/elan/master/elan-init.ps1";

    /// <summary>The script to fetch, and how to run it once saved at <paramref name="scriptPath"/>.</summary>
    public static (string Url, string FileName, IReadOnlyList<string> Arguments) Plan(string scriptPath, bool windows) => windows
        ? (WindowsScript, "powershell", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-NoPrompt", "1", "-DefaultToolchain", "stable"])
        : (UnixScript, "sh", [scriptPath, "-y", "--default-toolchain", "stable"]);

    /// <summary>
    /// Download the installer for this platform into a new temporary folder and run it without prompts, installing
    /// elan and the latest stable Lean as the default toolchain. Progress is streamed to <paramref name="onLine"/>.
    /// The client used, including one passed as <paramref name="http"/>, is disposed afterwards.
    /// </summary>
    /// <exception cref="HttpRequestException">The installer could not be downloaded.</exception>
    public static async Task<ProcessResult> InstallAsync(Action<string>? onLine = null, HttpClient? http = null, CancellationToken ct = default)
    {
        bool windows = OperatingSystem.IsWindows();
        string dir = Directory.CreateTempSubdirectory("leanstudio-elan").FullName;
        string script = Path.Combine(dir, windows ? "elan-init.ps1" : "elan-init.sh");
        var (url, file, args) = Plan(script, windows);
        onLine?.Invoke("Downloading the elan installer from " + url);
        using HttpClient client = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        await File.WriteAllBytesAsync(script, await client.GetByteArrayAsync(url, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        onLine?.Invoke("Installing elan and the latest stable Lean (a few hundred MB)…");
        return await ProcessRunner.RunAsync(file, args, dir, onLine, ct: ct).ConfigureAwait(false);
    }
}

/// <summary>File operations for the explorer: move to the trash, open a terminal, reveal.</summary>
public static class FileOps
{
    /// <summary>
    /// Move a file or folder to the system's trash: through Finder on macOS, the Recycle Bin on Windows, and
    /// <c>gio trash</c> elsewhere. False when the platform offers no way to or it did not work; does not throw.
    /// </summary>
    public static async Task<bool> MoveToTrashAsync(string path)
    {
        string full = Path.GetFullPath(path);
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                string escaped = full.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
                ProcessResult r = await ProcessRunner.RunAsync("osascript", ["-e", $"tell application \"Finder\" to delete POSIX file \"{escaped}\""]).ConfigureAwait(false);
                return r.Success && !Exists(full);
            }
            if (OperatingSystem.IsWindows())
            {
                if (Directory.Exists(full))
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(full, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                else
                {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(full, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                }
                return !Exists(full);
            }
            ProcessResult g = await ProcessRunner.RunAsync("gio", ["trash", full]).ConfigureAwait(false);
            return g.Success && !Exists(full);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return false;
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>
    /// Open the platform's terminal in a folder, trying each likely terminal in turn without waiting for it. False
    /// when none could be started.
    /// </summary>
    public static bool OpenTerminal(string folder)
    {
        (string file, string[] args)[] candidates = OperatingSystem.IsMacOS()
            ? [("open", ["-a", "Terminal", folder])]
            : OperatingSystem.IsWindows()
                ? [("wt.exe", ["-d", folder]), ("cmd.exe", ["/c", "start", "cmd.exe", "/k", "cd", "/d", folder])]
                : [("x-terminal-emulator", ["--working-directory=" + folder]), ("gnome-terminal", ["--working-directory=" + folder]), ("konsole", ["--workdir", folder]), ("xterm", ["-e", "cd \"" + folder + "\" && $SHELL"])];
        foreach ((string file, string[] args) in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = folder };
                foreach (string a in args)
                {
                    psi.ArgumentList.Add(a);
                }
                Process.Start(psi)?.Dispose();
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }
        return false;
    }
}

/// <summary>An import that would bring a missing name into scope.</summary>
/// <param name="Name">The declaration's full name, which may include a namespace the file does not open.</param>
/// <param name="Module">The module to import.</param>
public sealed record ImportSuggestion(string Name, string Module);

/// <summary>
/// "Unknown identifier": find which module defines the name, so the fix can be offered as an import. Asks
/// Loogle, which indexes Mathlib and everything it builds on (Batteries, Std, Lean core).
/// </summary>
public static partial class ImportFinder
{
    [GeneratedRegex(@"^unknown (?:identifier|constant) '(?<n>[^']+)'")]
    private static partial Regex Unknown();

    /// <summary>
    /// The missing name in an "unknown identifier" or "unknown constant" message, without <c>«»</c>, or null when the
    /// message is something else.
    /// </summary>
    public static string? MissingName(string message)
    {
        Match m = Unknown().Match(message.Trim());
        return m.Success ? m.Groups["n"].Value.Trim('«', '»') : null;
    }

    /// <summary>
    /// Declarations named exactly <paramref name="name"/>, or ending in .name (it may need a namespace or an import),
    /// ranked by <see cref="Rank"/>. Asks Loogle online; empty when Loogle reports an error.
    /// </summary>
    /// <exception cref="HttpRequestException">Loogle could not be reached.</exception>
    public static async Task<IReadOnlyList<ImportSuggestion>> SuggestAsync(string name, Loogle? loogle = null, CancellationToken ct = default)
    {
        var (hits, error, _) = await (loogle ?? new Loogle()).SearchAsync("\"" + name + "\"", ct).ConfigureAwait(false);
        if (error is not null)
        {
            return [];
        }
        return Rank(hits, name);
    }

    /// <summary>
    /// Keep the hits named <paramref name="name"/> or ending in <c>.name</c>, exact names first, then modules outside
    /// Mathlib, then shorter names; at most one per module and eight in all.
    /// </summary>
    public static IReadOnlyList<ImportSuggestion> Rank(IEnumerable<LoogleHit> hits, string name) =>
        hits.Where(h => h.Name == name || h.Name.EndsWith("." + name, StringComparison.Ordinal))
            .OrderBy(h => h.Name == name ? 0 : 1)
            .ThenBy(h => h.Module.StartsWith("Mathlib", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(h => h.Name.Length)
            .Select(h => new ImportSuggestion(h.Name, h.Module))
            .DistinctBy(s => s.Module)
            .Take(8)
            .ToList();

    /// <summary>
    /// Add <c>import Module</c> after the file's last import (or at the top, after any <c>module</c> or
    /// <c>prelude</c> header and comments), unless it is already there. Returns the new text.
    /// </summary>
    public static string AddImport(string text, string module)
    {
        string[] lines = text.Split('\n');
        if (lines.Any(l => Regex.IsMatch(l, $@"^\s*(public\s+)?import\s+(.*\s)?{Regex.Escape(module)}(\s|$)")))
        {
            return text;
        }
        int last = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.StartsWith("import ", StringComparison.Ordinal) || t.StartsWith("public import ", StringComparison.Ordinal))
            {
                last = i;
            }
            else if (t.Length > 0 && !t.StartsWith("--", StringComparison.Ordinal) && !t.StartsWith("module", StringComparison.Ordinal) && !t.StartsWith("prelude", StringComparison.Ordinal))
            {
                break;
            }
        }
        var list = lines.ToList();
        list.Insert(last + 1, "import " + module);
        return string.Join('\n', list);
    }

    /// <summary>Whether the project can import a module: core Lean always; Mathlib, Batteries and friends only if it depends on Mathlib.</summary>
    public static bool Available(string module, bool dependsOnMathlib) =>
        module.Split('.')[0] is "Init" or "Std" or "Lean" or "Lake" || dependsOnMathlib;
}
