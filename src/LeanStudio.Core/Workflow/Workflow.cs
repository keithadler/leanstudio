using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LeanStudio.Core.Editing;
using LeanStudio.Core.Git;
using LeanStudio.Core.Processes;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Toolchains;

namespace LeanStudio.Core.Workflow;

public enum MarkerKind
{
    Sorry,
    Admit,
    Todo,
}

/// <summary>A sorry, admit or TODO somewhere in the project: where, which declaration, and the line.</summary>
public sealed record Marker(string Path, int Line, int Column, MarkerKind Kind, string? Declaration, string LineText)
{
    public string KindLabel => Kind switch
    {
        MarkerKind.Sorry => "sorry",
        MarkerKind.Admit => "admit",
        _ => "TODO",
    };
}

/// <summary>
/// The unfinished work in a project: every <c>sorry</c> and <c>admit</c> in code, and every TODO/FIXME in a
/// comment, found by reading the sources (so files that are not open, and projects that are not built, count).
/// </summary>
public static partial class Markers
{
    [GeneratedRegex(@"\b(sorry|admit)\b")]
    private static partial Regex SorryWord();

    [GeneratedRegex(@"\b(TODO|FIXME|XXX)\b")]
    private static partial Regex TodoWord();

    [GeneratedRegex(@"^\s*(@\[[^\]]*\]\s*)*((private|protected|noncomputable|partial|unsafe|nonrec)\s+)*(theorem|lemma|def|example|instance|abbrev|structure|class|inductive)\s*([^\s:({\[]*)")]
    private static partial Regex Declaration();

    public static IReadOnlyList<Marker> Scan(string root, CancellationToken ct = default)
    {
        var list = new List<Marker>();
        foreach (string file in ProjectSearch.Files(root, leanOnly: true))
        {
            ct.ThrowIfCancellationRequested();
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            list.AddRange(ScanText(file, lines));
        }
        return list;
    }

    public static IEnumerable<Marker> ScanText(string path, IReadOnlyList<string> lines)
    {
        bool inBlock = false;
        string? decl = null;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            string code = Proofs.ProofSteps.StripComments(line, ref inBlock);
            Match d = Declaration().Match(code);
            if (d.Success)
            {
                decl = d.Groups[5].Value.Length > 0 ? d.Groups[5].Value : d.Groups[4].Value;
            }
            foreach (Match m in SorryWord().Matches(code))
            {
                yield return new Marker(path, i, m.Index, m.Value == "admit" ? MarkerKind.Admit : MarkerKind.Sorry, decl, line.Trim());
            }
            // TODOs live in comments: the part of the line that is not code.
            Match t = TodoWord().Match(line);
            if (t.Success && (t.Index >= code.Length || code[t.Index] == ' '))
            {
                yield return new Marker(path, i, t.Index, MarkerKind.Todo, decl, line.Trim());
            }
        }
    }
}

/// <summary>An error or warning from <c>lake build</c>, for a file that may not be open.</summary>
public sealed record BuildMessage(string Path, int Line, int Column, bool IsError, string Message);

public static partial class LakeOutput
{
    [GeneratedRegex(@"^(?<sev>error|warning|info): (?<file>[^:\n]+\.lean):(?<line>\d+):(?<col>\d+): (?<msg>.*)$")]
    private static partial Regex Header();

    /// <summary>
    /// Read Lake's build log into messages. Each starts with "error: File.lean:line:col: text" and may continue on
    /// following lines until the next message or Lake's own status lines (✔ ⚠ ✖, "Build completed", …).
    /// </summary>
    public static IReadOnlyList<BuildMessage> Parse(string output, string projectRoot)
    {
        var list = new List<BuildMessage>();
        string? file = null;
        int line = 0, col = 0;
        bool isError = false;
        var text = new StringBuilder();
        void Flush()
        {
            if (file is not null)
            {
                string full = Path.IsPathRooted(file) ? file : Path.GetFullPath(Path.Combine(projectRoot, file));
                list.Add(new BuildMessage(full, line - 1, col, isError, text.ToString().TrimEnd()));
            }
            file = null;
            text.Clear();
        }
        foreach (string raw in output.Split('\n'))
        {
            string l = raw.TrimEnd('\r');
            Match m = Header().Match(l);
            if (m.Success)
            {
                Flush();
                if (m.Groups["sev"].Value == "info")
                {
                    continue;
                }
                file = m.Groups["file"].Value;
                line = int.Parse(m.Groups["line"].Value, CultureInfo.InvariantCulture);
                col = int.Parse(m.Groups["col"].Value, CultureInfo.InvariantCulture);
                isError = m.Groups["sev"].Value == "error";
                text.Append(m.Groups["msg"].Value);
            }
            else if (file is not null && (l.StartsWith('✔') || l.StartsWith('⚠') || l.StartsWith('✖') || l.StartsWith("Build ", StringComparison.Ordinal)
                                          || l.StartsWith("Some required", StringComparison.Ordinal) || l.StartsWith("- ", StringComparison.Ordinal)
                                          || l.StartsWith("error:", StringComparison.Ordinal) || l.StartsWith("trace:", StringComparison.Ordinal)))
            {
                Flush();
            }
            else if (file is not null)
            {
                text.Append('\n').Append(l);
            }
        }
        Flush();
        return list;
    }
}

public sealed record HistoryEntry(string Path, DateTime SavedAt, string SnapshotFile)
{
    public string Label => SavedAt.ToLocalTime().ToString("ddd d MMM, HH:mm:ss", CultureInfo.CurrentCulture);
}

/// <summary>
/// Local history: every save of a file keeps a copy, so earlier versions can be brought back even without git.
/// The newest <see cref="Keep"/> versions of each file are kept, under the app's data folder.
/// </summary>
public sealed class LocalHistory(string folder)
{
    public const int Keep = 40;

    public string Folder { get; } = folder;

    private string FolderFor(string path)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..24];
        return System.IO.Path.Combine(Folder, key);
    }

    public void Record(string path, string text)
    {
        string dir = FolderFor(path);
        Directory.CreateDirectory(dir);
        File.WriteAllText(System.IO.Path.Combine(dir, "path.txt"), System.IO.Path.GetFullPath(path));
        string[] existing = Directory.GetFiles(dir, "*.snap").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (existing.Length > 0 && File.ReadAllText(existing[^1]) == text)
        {
            return; // unchanged since the last save
        }
        string name = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture) + ".snap";
        File.WriteAllText(System.IO.Path.Combine(dir, name), text);
        foreach (string old in existing.Take(Math.Max(0, existing.Length + 1 - Keep)))
        {
            File.Delete(old);
        }
    }

    public IReadOnlyList<HistoryEntry> Versions(string path)
    {
        string dir = FolderFor(path);
        if (!Directory.Exists(dir))
        {
            return [];
        }
        return Directory.GetFiles(dir, "*.snap")
            .Select(f => (File: f, Stamp: System.IO.Path.GetFileNameWithoutExtension(f)))
            .Select(x => new HistoryEntry(path, DateTime.SpecifyKind(DateTime.ParseExact(x.Stamp, "yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture), DateTimeKind.Utc), x.File))
            .OrderByDescending(e => e.SavedAt)
            .ToList();
    }
}

/// <summary>A hit from Loogle, the Mathlib search engine: a name, its type, and the module that defines it.</summary>
public sealed record LoogleHit(string Name, string Type, string Module, string? Doc);

/// <summary>
/// Loogle (loogle.lean-lang.org) searches Mathlib by name, by constant, or by the shape of a type:
/// <c>Real.sqrt</c>, <c>"prime"</c>, <c>_ * (_ ^ _)</c>, <c>|- tsum _ = _ * tsum _</c>. Online, read-only.
/// </summary>
public sealed class Loogle(HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<(IReadOnlyList<LoogleHit> Hits, string? Error, int Count)> SearchAsync(string query, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://loogle.lean-lang.org/json?q=" + Uri.EscapeDataString(query));
        req.Headers.UserAgent.ParseAdd("LeanStudio/" + Updates.UpdateChecker.CurrentVersion.ToString(3));
        using HttpResponseMessage r = await _http.SendAsync(req, ct).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return Parse(doc.RootElement);
    }

    public static (IReadOnlyList<LoogleHit> Hits, string? Error, int Count) Parse(JsonElement root)
    {
        if (root.TryGetProperty("error", out JsonElement err))
        {
            return ([], err.GetString(), 0);
        }
        var hits = new List<LoogleHit>();
        if (root.TryGetProperty("hits", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement h in arr.EnumerateArray())
            {
                hits.Add(new LoogleHit(
                    h.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "",
                    h.TryGetProperty("type", out JsonElement t) ? (t.GetString() ?? "").Trim() : "",
                    h.TryGetProperty("module", out JsonElement m) ? m.GetString() ?? "" : "",
                    h.TryGetProperty("doc", out JsonElement d) ? d.GetString() : null));
            }
        }
        int count = root.TryGetProperty("count", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : hits.Count;
        return (hits, null, count);
    }
}

/// <summary>Links to the documentation site that covers Mathlib and its dependencies, Lean core included.</summary>
public static class DocLinks
{
    public static string For(string module, string declaration) =>
        $"https://leanprover-community.github.io/mathlib4_docs/{module.Replace('.', '/')}.html#{Uri.EscapeDataString(declaration)}";
}

/// <summary>Who last changed a line, and when, from <c>git blame</c>.</summary>
public sealed record BlameLine(string Author, DateTimeOffset When, string Summary, string Commit)
{
    public bool IsUncommitted => Commit.All(c => c == '0');

    public string Describe(DateTimeOffset now) => IsUncommitted
        ? "You, not committed yet"
        : $"{Author}, {Ago(now - When)} · {Summary}";

    public static string Ago(TimeSpan t) =>
        t.TotalMinutes < 1 ? "just now"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min ago"
        : t.TotalDays < 1 ? $"{(int)t.TotalHours} h ago"
        : t.TotalDays < 60 ? $"{(int)t.TotalDays} days ago"
        : t.TotalDays < 730 ? $"{(int)(t.TotalDays / 30)} months ago"
        : $"{(int)(t.TotalDays / 365)} years ago";
}

public static class Blame
{
    public static async Task<BlameLine?> LineAsync(GitRepository repo, string file, int oneBasedLine, CancellationToken ct = default)
    {
        ProcessResult r = await repo.RunAsync(["blame", "--porcelain", "-L", $"{oneBasedLine},{oneBasedLine}", "--", file], ct: ct).ConfigureAwait(false);
        return r.Success ? Parse(r.Output) : null;
    }

    public static BlameLine? Parse(string porcelain)
    {
        string[] lines = porcelain.Split('\n');
        if (lines.Length == 0 || lines[0].Length < 40)
        {
            return null;
        }
        string commit = lines[0][..40];
        string author = "", summary = "";
        long time = 0;
        foreach (string l in lines)
        {
            if (l.StartsWith("author ", StringComparison.Ordinal))
            {
                author = l[7..];
            }
            else if (l.StartsWith("author-time ", StringComparison.Ordinal))
            {
                long.TryParse(l[12..], CultureInfo.InvariantCulture, out time);
            }
            else if (l.StartsWith("summary ", StringComparison.Ordinal))
            {
                summary = l[8..];
            }
        }
        return new BlameLine(author, DateTimeOffset.FromUnixTimeSeconds(time), summary, commit);
    }
}

/// <summary>Something to run in a project: a Lake target or command, or a shell command.</summary>
public sealed record ProjectTask(string Title, string Detail, string FileName, IReadOnlyList<string> Arguments);

/// <summary>The tasks a Lean project offers: build, test, lint, each executable, each Lake script.</summary>
public static partial class ProjectTasks
{
    [GeneratedRegex(@"\[\[lean_exe\]\]\s*\n\s*name\s*=\s*""(?<n>[^""]+)""")]
    private static partial Regex TomlExe();

    [GeneratedRegex(@"^\s*lean_exe\s+«?(?<n>[\w.]+)»?", RegexOptions.Multiline)]
    private static partial Regex LeanExe();

    [GeneratedRegex(@"^\s*script\s+«?(?<n>[\w.]+)»?", RegexOptions.Multiline)]
    private static partial Regex LeanScript();

    public static IReadOnlyList<ProjectTask> For(LeanProject project)
    {
        string lake = Elan.FindExecutable("lake") ?? "lake";
        var tasks = new List<ProjectTask>
        {
            new("lake build", "Build the project", lake, ["build"]),
            new("lake test", "Run the project's test driver", lake, ["test"]),
            new("lake lint", "Run the project's lint driver", lake, ["lint"]),
            new("lake update", "Update dependencies to their latest allowed versions", lake, ["update"]),
            new("lake clean", "Delete the build output", lake, ["clean"]),
        };
        if (project.DependsOnMathlib)
        {
            tasks.Add(new("lake exe cache get", "Download Mathlib's prebuilt files", lake, ["exe", "cache", "get"]));
        }
        if (project.Lakefile is string lf)
        {
            string text = File.ReadAllText(lf);
            IEnumerable<Match> exes = lf.EndsWith(".toml", StringComparison.Ordinal) ? TomlExe().Matches(text) : LeanExe().Matches(text);
            foreach (Match m in exes)
            {
                tasks.Add(new($"lake exe {m.Groups["n"].Value}", "Build and run this executable", lake, ["exe", m.Groups["n"].Value]));
            }
            if (!lf.EndsWith(".toml", StringComparison.Ordinal))
            {
                foreach (Match m in LeanScript().Matches(text))
                {
                    tasks.Add(new($"lake script run {m.Groups["n"].Value}", "Run this Lake script", lake, ["script", "run", m.Groups["n"].Value]));
                }
            }
        }
        return tasks;
    }

    /// <summary>A shell command line, run the way the platform's terminal would.</summary>
    public static ProjectTask Shell(string commandLine) => OperatingSystem.IsWindows()
        ? new(commandLine, "shell command", "cmd.exe", ["/c", commandLine])
        : new(commandLine, "shell command", Environment.GetEnvironmentVariable("SHELL") is { Length: > 0 } sh ? sh : "/bin/sh", ["-lc", commandLine]);
}
