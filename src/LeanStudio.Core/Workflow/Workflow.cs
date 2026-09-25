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

/// <summary>What a <see cref="Marker"/> marks.</summary>
public enum MarkerKind
{
    /// <summary>A <c>sorry</c> in code.</summary>
    Sorry,
    /// <summary>An <c>admit</c> in code.</summary>
    Admit,
    /// <summary>A TODO, FIXME or XXX in a comment.</summary>
    Todo,
}

/// <summary>A sorry, admit or TODO somewhere in the project: where, which declaration, and the line.</summary>
/// <param name="Path">The file, as the scan was given it (full paths from <see cref="Markers.Scan"/>).</param>
/// <param name="Line">The line, 0-based.</param>
/// <param name="Column">Where the word starts in the line, 0-based, in UTF-16 code units.</param>
/// <param name="Kind">Which kind of marker it is.</param>
/// <param name="Declaration">
/// The name of the nearest declaration that starts at or above the line (the keyword, such as <c>example</c>, when it
/// has no name), or <see langword="null"/> when there is none.
/// </param>
/// <param name="LineText">The whole line, trimmed, for display.</param>
public sealed record Marker(string Path, int Line, int Column, MarkerKind Kind, string? Declaration, string LineText)
{
    /// <summary>The kind as shown in the list: <c>sorry</c>, <c>admit</c> or <c>TODO</c> (also for FIXME and XXX).</summary>
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

    /// <summary>
    /// Read every Lean file under <paramref name="root"/> from disk and collect its markers. Files that cannot be read
    /// are skipped. Synchronous and potentially slow on a large project; checks <paramref name="ct"/> between files.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
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

    /// <summary>
    /// The markers in one file's lines, lazily, in order. <c>sorry</c> and <c>admit</c> count only outside comments;
    /// TODO, FIXME and XXX only inside them. Block comments spanning lines are followed.
    /// </summary>
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
/// <param name="Path">The file's full path.</param>
/// <param name="Line">The line, 0-based (Lake prints it 1-based).</param>
/// <param name="Column">The column as Lake prints it, which is 0-based.</param>
/// <param name="IsError">An error rather than a warning.</param>
/// <param name="Message">The message, which may span several lines.</param>
public sealed record BuildMessage(string Path, int Line, int Column, bool IsError, string Message);

/// <summary>Reads the messages out of <c>lake build</c>'s output.</summary>
public static partial class LakeOutput
{
    [GeneratedRegex(@"^(?<sev>error|warning|info): (?<file>[^:\n]+\.lean):(?<line>\d+):(?<col>\d+): (?<msg>.*)$")]
    private static partial Regex Header();

    /// <summary>
    /// Read Lake's build log into messages. Each starts with "error: File.lean:line:col: text" and may continue on
    /// following lines until the next message or Lake's own status lines (✔ ⚠ ✖, "Build completed", …).
    /// Info messages are dropped. Relative file names are resolved against <paramref name="projectRoot"/>.
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

/// <summary>One saved version of a file in <see cref="LocalHistory"/>.</summary>
/// <param name="Path">The file the version belongs to, as passed to <see cref="LocalHistory.Versions"/>.</param>
/// <param name="SavedAt">When it was saved, in UTC.</param>
/// <param name="SnapshotFile">The full path of the file holding that version's text.</param>
public sealed record HistoryEntry(string Path, DateTime SavedAt, string SnapshotFile)
{
    /// <summary>When it was saved, in local time and the current culture, e.g. <c>Tue 3 Mar, 14:05:09</c>.</summary>
    public string Label => SavedAt.ToLocalTime().ToString("ddd d MMM, HH:mm:ss", CultureInfo.CurrentCulture);
}

/// <summary>
/// Local history: every save of a file keeps a copy, so earlier versions can be brought back even without git.
/// The newest <see cref="Keep"/> versions of each file are kept, under the app's data folder.
/// </summary>
/// <param name="folder">
/// Where to keep the versions: one subfolder per file, named by a hash of its full path. Created when first needed.
/// </param>
public sealed class LocalHistory(string folder)
{
    /// <summary>How many versions of each file are kept; older ones are deleted as new ones are recorded.</summary>
    public const int Keep = 40;

    /// <summary>The folder the versions are kept in.</summary>
    public string Folder { get; } = folder;

    private string FolderFor(string path)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..24];
        return System.IO.Path.Combine(Folder, key);
    }

    /// <summary>
    /// Save <paramref name="text"/> as the newest version of the file at <paramref name="path"/>, unless it is the
    /// same as the newest one already kept, and delete versions beyond <see cref="Keep"/>. Writes to disk; I/O errors
    /// are not caught.
    /// </summary>
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
        // Named by the time, to the millisecond; two saves in the same millisecond take the next free one.
        DateTime at = DateTime.UtcNow;
        string last = existing.Length > 0 ? System.IO.Path.GetFileName(existing[^1]) : "";
        string name;
        while (string.CompareOrdinal(name = at.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture) + ".snap", last) <= 0
               || File.Exists(System.IO.Path.Combine(dir, name)))
        {
            at = at.AddMilliseconds(1);
        }
        File.WriteAllText(System.IO.Path.Combine(dir, name), text);
        foreach (string old in existing.Take(Math.Max(0, existing.Length + 1 - Keep)))
        {
            File.Delete(old);
        }
    }

    /// <summary>The versions kept for the file at <paramref name="path"/>, newest first; empty when there are none.</summary>
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
/// <param name="Name">The declaration's full name, e.g. <c>Nat.add_comm</c>.</param>
/// <param name="Type">Its type (the statement, for a theorem), as Lean prints it.</param>
/// <param name="Module">The module that defines it, e.g. <c>Mathlib.Algebra.Group.Basic</c>.</param>
/// <param name="Doc">Its docstring, or <see langword="null"/> when it has none.</param>
public sealed record LoogleHit(string Name, string Type, string Module, string? Doc);

/// <summary>
/// Loogle (loogle.lean-lang.org) searches Mathlib by name, by constant, or by the shape of a type:
/// <c>Real.sqrt</c>, <c>"prime"</c>, <c>_ * (_ ^ _)</c>, <c>|- tsum _ = _ * tsum _</c>. Online, read-only.
/// </summary>
/// <param name="http">The client to send requests with; by default, one with a 20-second timeout.</param>
public sealed class Loogle(HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// Send <paramref name="query"/> to Loogle and read the answer (see <see cref="Parse"/>). A query Loogle cannot
    /// understand is not an exception: it comes back as <c>Error</c>, with no hits.
    /// </summary>
    /// <exception cref="HttpRequestException">The request failed or Loogle answered with an HTTP error.</exception>
    public async Task<(IReadOnlyList<LoogleHit> Hits, string? Error, int Count)> SearchAsync(string query, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://loogle.lean-lang.org/json?q=" + Uri.EscapeDataString(query));
        req.Headers.UserAgent.ParseAdd("LeanStudio/" + Updates.UpdateChecker.CurrentVersion.ToString(3));
        using HttpResponseMessage r = await _http.SendAsync(req, ct).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return Parse(doc.RootElement);
    }

    /// <summary>
    /// Read Loogle's JSON answer: the hits it returned, its error message (or <see langword="null"/>), and how many
    /// declarations matched in total, which can be more than the hits returned.
    /// </summary>
    public static (IReadOnlyList<LoogleHit> Hits, string? Error, int Count) Parse(JsonElement root)
    {
        // Loogle is a web service: read only what has the shape expected, and say so when nothing does.
        static string? Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return ([], "Loogle answered with something Lean Studio couldn't read.", 0);
        }
        if (root.TryGetProperty("error", out JsonElement err))
        {
            return ([], err.ValueKind == JsonValueKind.String ? err.GetString() : err.ToString(), 0);
        }
        var hits = new List<LoogleHit>();
        if (root.TryGetProperty("hits", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement h in arr.EnumerateArray().Where(h => h.ValueKind == JsonValueKind.Object))
            {
                hits.Add(new LoogleHit(Str(h, "name") ?? "", (Str(h, "type") ?? "").Trim(), Str(h, "module") ?? "", Str(h, "doc")));
            }
        }
        int count = root.TryGetProperty("count", out JsonElement c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : hits.Count;
        return (hits, null, count);
    }
}

/// <summary>A Mathlib result found by meaning, with its informal statement.</summary>
/// <param name="Name">The declaration's full name.</param>
/// <param name="Kind">What kind of declaration it is (<c>theorem</c>, <c>def</c>, …); empty when not given.</param>
/// <param name="Module">The module that defines it; empty when not given.</param>
/// <param name="Type">Its type or signature; empty when not given.</param>
/// <param name="InformalName">A short English name for it, when LeanSearch has one.</param>
/// <param name="InformalStatement">An English statement of it, when LeanSearch has one.</param>
/// <param name="Distance">How far it is from the question, as LeanSearch reports it: smaller is closer, <c>0</c> when not given.</param>
public sealed record MeaningHit(string Name, string Kind, string Module, string Type, string? InformalName, string? InformalStatement, double Distance)
{
    /// <summary>How closely it matches the question, 0–100.</summary>
    public int Relevance => (int)Math.Round(Math.Clamp(1 - Distance, 0, 1) * 100);
}

/// <summary>
/// Search Mathlib by meaning, in plain English ("the sum of the first n odd numbers is n squared"), with LeanSearch
/// (leansearch.net), which matches the question against informal statements of every Mathlib result. Loogle finds
/// what you can name or shape; this finds what you can only describe.
/// </summary>
/// <param name="http">The client to send requests with; by default, one with a 30-second timeout.</param>
public sealed class LeanSearch(HttpClient? http = null)
{
    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>What to say when a search failed: the service answered with an error, or couldn't be reached.</summary>
    public static string Explain(Exception e) => e switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode code } =>
            $"LeanSearch is having trouble right now (it answered HTTP {(int)code}); try again later, or use Loogle.",
        TaskCanceledException => "LeanSearch took too long to answer; try again later, or use Loogle.",
        System.Text.Json.JsonException => "LeanSearch answered with something Lean Studio couldn't read; try again later.",
        _ => "Could not reach LeanSearch (" + e.Message + "). Check the connection, or use Loogle.",
    };

    /// <summary>The LeanSearch URL queries are posted to.</summary>
    public const string Endpoint = "https://leansearch.net/search";

    /// <summary>Ask LeanSearch for up to <paramref name="results"/> results matching <paramref name="question"/>, closest first.</summary>
    /// <exception cref="HttpRequestException">The request failed or LeanSearch answered with an HTTP error.</exception>
    public async Task<IReadOnlyList<MeaningHit>> SearchAsync(string question, int results = 20, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { query = new[] { question }, num_results = results }),
                System.Text.Encoding.UTF8, "application/json"),
        };
        req.Headers.UserAgent.ParseAdd("LeanStudio/" + Updates.UpdateChecker.CurrentVersion.ToString(3));
        using HttpResponseMessage r = await _http.SendAsync(req, ct).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return Parse(doc.RootElement);
    }

    /// <summary>Read LeanSearch's answer: a list (one per query) of lists of <c>{result, distance}</c>.</summary>
    public static IReadOnlyList<MeaningHit> Parse(JsonElement root)
    {
        var hits = new List<MeaningHit>();
        JsonElement list = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Array ? root[0] : root;
        if (list.ValueKind != JsonValueKind.Array)
        {
            return hits;
        }
        static string Joined(JsonElement e) => e.ValueKind == JsonValueKind.Array
            ? string.Join('.', e.EnumerateArray().Select(x => x.GetString() ?? ""))
            : e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
        static string? Str(JsonElement o, string name) =>
            o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        foreach (JsonElement item in list.EnumerateArray())
        {
            JsonElement res = item.TryGetProperty("result", out JsonElement rr) ? rr : item;
            if (res.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            string name = res.TryGetProperty("name", out JsonElement n) ? Joined(n) : "";
            if (name.Length == 0)
            {
                continue;
            }
            hits.Add(new MeaningHit(
                name,
                Str(res, "kind") ?? "",
                res.TryGetProperty("module_name", out JsonElement m) ? Joined(m) : "",
                (Str(res, "type") ?? Str(res, "signature") ?? "").Trim(),
                Str(res, "informal_name"),
                Str(res, "informal_description"),
                item.TryGetProperty("distance", out JsonElement d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0));
        }
        return hits;
    }
}

/// <summary>Links to the documentation site that covers Mathlib and its dependencies, Lean core included.</summary>
public static class DocLinks
{
    /// <summary>
    /// The page for <paramref name="declaration"/> (its full name) in <paramref name="module"/>. The link is built,
    /// not checked, so it only works for modules the site documents.
    /// </summary>
    public static string For(string module, string declaration) =>
        $"https://leanprover-community.github.io/mathlib4_docs/{module.Replace('.', '/')}.html#{Uri.EscapeDataString(declaration)}";
}

/// <summary>Who last changed a line, and when, from <c>git blame</c>.</summary>
/// <param name="Author">The author's name.</param>
/// <param name="When">The author time (UTC).</param>
/// <param name="Summary">The first line of the commit message.</param>
/// <param name="Commit">The full commit hash; all zeros for a change that is not committed yet.</param>
public sealed record BlameLine(string Author, DateTimeOffset When, string Summary, string Commit)
{
    /// <summary>The line has changed since the last commit.</summary>
    public bool IsUncommitted => Commit.All(c => c == '0');

    /// <summary>One line for the editor: author, how long before <paramref name="now"/>, and summary.</summary>
    public string Describe(DateTimeOffset now) => IsUncommitted
        ? "You, not committed yet"
        : $"{Author}, {Ago(now - When)} · {Summary}";

    /// <summary>A rough English age, e.g. <c>just now</c>, <c>5 min ago</c>, <c>3 months ago</c>.</summary>
    public static string Ago(TimeSpan t) =>
        t.TotalMinutes < 1 ? "just now"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min ago"
        : t.TotalDays < 1 ? $"{(int)t.TotalHours} h ago"
        : t.TotalDays < 60 ? $"{(int)t.TotalDays} days ago"
        : t.TotalDays < 730 ? $"{(int)(t.TotalDays / 30)} months ago"
        : $"{(int)(t.TotalDays / 365)} years ago";
}

/// <summary>Line blame, through <c>git blame --porcelain</c>.</summary>
public static class Blame
{
    /// <summary>
    /// Who last changed line <paramref name="oneBasedLine"/> of <paramref name="file"/> (as saved on disk); null when
    /// git fails, for example because the file is untracked.
    /// </summary>
    public static async Task<BlameLine?> LineAsync(GitRepository repo, string file, int oneBasedLine, CancellationToken ct = default)
    {
        ProcessResult r = await repo.RunAsync(["blame", "--porcelain", "-L", $"{oneBasedLine},{oneBasedLine}", "--", file], ct: ct).ConfigureAwait(false);
        return r.Success ? Parse(r.Output) : null;
    }

    /// <summary>Read the first entry of <c>git blame --porcelain</c> output; null when it does not start with a commit hash.</summary>
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
/// <param name="Title">What the task list shows, e.g. <c>lake build</c>.</param>
/// <param name="Detail">A short description of what it does.</param>
/// <param name="FileName">The executable to run.</param>
/// <param name="Arguments">Its arguments, one per element, unquoted.</param>
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

    /// <summary>
    /// The tasks for <paramref name="project"/>: the standard Lake commands, <c>lake exe cache get</c> when it depends
    /// on Mathlib, and one per <c>lean_exe</c> and (in a <c>lakefile.lean</c>) <c>script</c> found in the lakefile.
    /// Reads the lakefile from disk.
    /// </summary>
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
