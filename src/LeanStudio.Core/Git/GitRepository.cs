using System.Globalization;
using System.Text.RegularExpressions;
using LeanStudio.Core.Processes;

namespace LeanStudio.Core.Git;

/// <summary>One changed file, as <c>git status</c> reports it.</summary>
/// <param name="Path">The file's path relative to the repository root, with <c>/</c> separators as git writes them.</param>
/// <param name="Index">
/// The staged status letter (git's X column): <c>.</c> unchanged, <c>M</c>, <c>A</c>, <c>D</c>, <c>R</c>, <c>C</c>,
/// <c>T</c>; <c>?</c> for an untracked file and <c>U</c> for a conflict.
/// </param>
/// <param name="WorkTree">The working-tree status letter (git's Y column), with the same letters as <paramref name="Index"/>.</param>
/// <param name="OriginalPath">For a rename or copy, the path it came from; otherwise <see langword="null"/>.</param>
public sealed record GitChange(string Path, string Index, string WorkTree, string? OriginalPath = null)
{
    /// <summary>Staged: the index differs from HEAD.</summary>
    public bool IsStaged => Index is not "." and not "?";
    /// <summary>The file is not tracked by git.</summary>
    public bool IsUntracked => Index == "?";
    /// <summary>The file has an unresolved merge conflict.</summary>
    public bool IsConflicted => Index == "U" || WorkTree == "U";

    /// <summary>
    /// One letter for the list: M modified, A added, D deleted, R renamed, C copied, U untracked, ! conflicted.
    /// Shows the staged status when the file is staged, else the working-tree one.
    /// </summary>
    public string Letter => IsUntracked ? "U" : IsConflicted ? "!" : (IsStaged ? Index : WorkTree) switch
    {
        "M" => "M",
        "A" => "A",
        "D" => "D",
        "R" => "R",
        "C" => "C",
        "T" => "M",
        _ => "M",
    };
}

/// <summary>A repository's branch and changes, as <c>git status</c> reports them.</summary>
/// <param name="Branch">The current branch, <c>(detached HEAD)</c>, or <see langword="null"/> when git did not say (for example, it failed).</param>
/// <param name="Upstream">The branch's upstream, e.g. <c>origin/main</c>, or <see langword="null"/> when it has none.</param>
/// <param name="Ahead">Commits on the branch that are not on its upstream; <c>0</c> without an upstream.</param>
/// <param name="Behind">Commits on the upstream that are not on the branch; <c>0</c> without an upstream.</param>
/// <param name="Changes">Every changed, untracked or conflicted file, sorted by path (ordinal).</param>
public sealed record GitStatus(string? Branch, string? Upstream, int Ahead, int Behind, IReadOnlyList<GitChange> Changes)
{
    /// <summary>There are no changes, not even untracked files.</summary>
    public bool IsClean => Changes.Count == 0;
}

/// <summary>A run of lines that differ from HEAD: added, modified, or a point where lines were deleted.</summary>
/// <param name="StartLine">
/// The first changed line in the current file, 1-based. For <see cref="LineChangeKind.Deleted"/>, the line after
/// which lines were removed (at least 1).
/// </param>
/// <param name="LineCount">How many lines of the current file changed; <c>0</c> for a deletion.</param>
/// <param name="Kind">What happened to the lines.</param>
public sealed record LineChange(int StartLine, int LineCount, LineChangeKind Kind);

/// <summary>How a <see cref="LineChange"/> differs from HEAD.</summary>
public enum LineChangeKind
{
    /// <summary>New lines where HEAD had none.</summary>
    Added,
    /// <summary>Lines that replace lines in HEAD.</summary>
    Modified,
    /// <summary>Lines in HEAD that are gone; the change marks the point where they were.</summary>
    Deleted,
}

/// <summary>
/// A Git working tree, driven through the <c>git</c> command so it behaves exactly as the person's own git does
/// (their config, hooks, credentials and signing). Every method runs git in the repository root.
/// </summary>
public sealed partial class GitRepository
{
    private GitRepository(string root) => Root = root;

    /// <summary>The absolute path of the working tree's top folder (the one holding <c>.git</c>).</summary>
    public string Root { get; }

    /// <summary>
    /// The path of the <c>git</c> executable, on PATH (or in elan's bin) or at <c>/usr/bin/git</c>;
    /// <see langword="null"/> when git is not installed. Searched on every access.
    /// </summary>
    public static string? GitExecutable => Toolchains.Elan.FindExecutable("git") ?? (File.Exists("/usr/bin/git") ? "/usr/bin/git" : null);

    /// <summary>git can be found (see <see cref="GitExecutable"/>).</summary>
    public static bool IsGitInstalled => GitExecutable is not null;

    /// <summary>
    /// The repository containing a folder or file: the nearest folder at or above <paramref name="path"/> with a
    /// <c>.git</c> folder or file (so worktrees and submodules count). Null when it is not in one, or git is missing.
    /// Does not run git.
    /// </summary>
    public static GitRepository? Find(string path)
    {
        for (string? d = Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path)); d is not null; d = Path.GetDirectoryName(d))
        {
            if (Directory.Exists(Path.Combine(d, ".git")) || File.Exists(Path.Combine(d, ".git")))
            {
                return IsGitInstalled ? new GitRepository(d) : null;
            }
        }
        return null;
    }

    /// <summary>
    /// Run git with <paramref name="args"/> in <see cref="Root"/>. Credential prompts in a terminal are turned off
    /// (git fails instead of waiting), and optional locks are skipped so background status checks do not get in the
    /// way of the person's own git.
    /// </summary>
    public Task<ProcessResult> RunAsync(IEnumerable<string> args, Action<string>? onLine = null, CancellationToken ct = default) =>
        ProcessRunner.RunAsync(GitExecutable ?? "git", args, Root, onLine,
            // Never stop to ask for a password in a terminal nobody can see; fail and say so instead.
            new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_OPTIONAL_LOCKS"] = "0" }, ct);

    /// <summary>
    /// Run <c>git init -b main</c> in <paramref name="folder"/>, which must exist. Returns the result, and the folder
    /// when it succeeded or <see langword="null"/> when it did not.
    /// </summary>
    public static async Task<(ProcessResult Result, string? Path)> InitAsync(string folder, CancellationToken ct = default)
    {
        ProcessResult r = await ProcessRunner.RunAsync(GitExecutable ?? "git", ["init", "-b", "main"], folder, ct: ct).ConfigureAwait(false);
        return (r, r.Success ? folder : null);
    }

    /// <summary>
    /// Clone a repository into <paramref name="parent"/>. <paramref name="source"/> may be a URL or GitHub's
    /// <c>owner/repo</c> shorthand. Submodules come along, as Lean projects often use them. The clone goes in a folder
    /// named after the repository; <paramref name="parent"/> is created if needed. Returns the result, and the new
    /// folder's path or <see langword="null"/> when the clone failed.
    /// </summary>
    public static async Task<(ProcessResult Result, string? Path)> CloneAsync(string source, string parent, Action<string>? onLine = null, CancellationToken ct = default)
    {
        string url = GitHub.ExpandShorthand(source);
        string name = Path.GetFileNameWithoutExtension(url.TrimEnd('/'));
        string target = Path.Combine(parent, name);
        Directory.CreateDirectory(parent);
        ProcessResult r = await ProcessRunner.RunAsync(GitExecutable ?? "git", ["clone", "--recurse-submodules", "--progress", url, target], parent, onLine,
            new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" }, ct).ConfigureAwait(false);
        return (r, r.Success && Directory.Exists(target) ? target : null);
    }

    /// <summary>The branch and every changed file, including untracked files in untracked folders (see <see cref="ParseStatus"/>).</summary>
    public async Task<GitStatus> StatusAsync(CancellationToken ct = default)
    {
        ProcessResult r = await RunAsync(["status", "--porcelain=v2", "--branch", "--untracked-files=all", "-z"], ct: ct).ConfigureAwait(false);
        return ParseStatus(r.Output);
    }

    /// <summary>
    /// Parse <c>git status --porcelain=v2 --branch -z</c>. Ignored files and unrecognised records are skipped;
    /// conflicted files get <c>U</c> in both columns.
    /// </summary>
    public static GitStatus ParseStatus(string output)
    {
        string? branch = null, upstream = null;
        int ahead = 0, behind = 0;
        var changes = new List<GitChange>();
        string[] records = output.Split('\0');
        for (int i = 0; i < records.Length; i++)
        {
            string rec = records[i].TrimStart('\n', '\r');
            if (rec.Length == 0)
            {
                continue;
            }
            if (rec.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                branch = rec["# branch.head ".Length..];
                if (branch == "(detached)")
                {
                    branch = "(detached HEAD)";
                }
            }
            else if (rec.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = rec["# branch.upstream ".Length..];
            }
            else if (rec.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                Match m = AheadBehind().Match(rec);
                if (m.Success)
                {
                    ahead = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    behind = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                }
            }
            else if (rec.StartsWith("1 ", StringComparison.Ordinal) || rec.StartsWith("u ", StringComparison.Ordinal))
            {
                // 1 XY sub mH mI mW hH hI path   /   u XY sub m1 m2 m3 mW h1 h2 h3 path
                string[] parts = rec.Split(' ', rec[0] == '1' ? 9 : 11);
                string xy = parts[1];
                changes.Add(new GitChange(parts[^1], rec[0] == 'u' ? "U" : xy[..1], rec[0] == 'u' ? "U" : xy[1..]));
            }
            else if (rec.StartsWith("2 ", StringComparison.Ordinal))
            {
                // 2 XY sub mH mI mW hH hI Xscore path, then the original path as the next record
                string[] parts = rec.Split(' ', 10);
                string xy = parts[1];
                string? orig = i + 1 < records.Length ? records[++i] : null;
                changes.Add(new GitChange(parts[^1], xy[..1], xy[1..], orig));
            }
            else if (rec.StartsWith("? ", StringComparison.Ordinal))
            {
                changes.Add(new GitChange(rec[2..], "?", "?"));
            }
        }
        return new GitStatus(branch, upstream, ahead, behind, changes.OrderBy(c => c.Path, StringComparer.Ordinal).ToList());
    }

    [GeneratedRegex(@"\+(\d+) -(\d+)")]
    private static partial Regex AheadBehind();

    /// <summary>Stage the given paths (<c>git add</c>); paths are relative to <see cref="Root"/> or absolute.</summary>
    public Task<ProcessResult> StageAsync(IEnumerable<string> paths, CancellationToken ct = default) => RunAsync(["add", "--", .. paths], ct: ct);

    /// <summary>Stage every change, including new and deleted files (<c>git add --all</c>).</summary>
    public Task<ProcessResult> StageAllAsync(CancellationToken ct = default) => RunAsync(["add", "--all"], ct: ct);

    /// <summary>Unstage the given paths, keeping their working-tree changes (<c>git restore --staged</c>).</summary>
    public Task<ProcessResult> UnstageAsync(IEnumerable<string> paths, CancellationToken ct = default) => RunAsync(["restore", "--staged", "--", .. paths], ct: ct);

    /// <summary>
    /// Throw away a file's changes, staged and unstaged, restoring it from HEAD; an untracked file is deleted from
    /// disk. This cannot be undone.
    /// </summary>
    public async Task<ProcessResult> DiscardAsync(GitChange change, CancellationToken ct = default)
    {
        if (change.IsUntracked)
        {
            string full = Path.Combine(Root, change.Path);
            if (File.Exists(full))
            {
                File.Delete(full);
            }
            return new ProcessResult(0, "");
        }
        return await RunAsync(["restore", "--staged", "--worktree", "--source=HEAD", "--", change.Path], ct: ct).ConfigureAwait(false);
    }

    /// <summary>Commit what is staged; with nothing staged, commit every change (as most IDEs do).</summary>
    public async Task<ProcessResult> CommitAsync(string message, CancellationToken ct = default)
    {
        GitStatus s = await StatusAsync(ct).ConfigureAwait(false);
        if (!s.Changes.Any(c => c.IsStaged))
        {
            await StageAllAsync(ct).ConfigureAwait(false);
        }
        return await RunAsync(["commit", "-m", message], ct: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Push the current branch. A branch with no upstream is pushed to <c>origin</c> under the same name and set to
    /// track it.
    /// </summary>
    public async Task<ProcessResult> PushAsync(Action<string>? onLine = null, CancellationToken ct = default)
    {
        GitStatus s = await StatusAsync(ct).ConfigureAwait(false);
        // A new branch has no upstream yet: publish it to origin under the same name.
        return s.Upstream is null && s.Branch is string b
            ? await RunAsync(["push", "--set-upstream", "origin", b], onLine, ct).ConfigureAwait(false)
            : await RunAsync(["push"], onLine, ct).ConfigureAwait(false);
    }

    /// <summary>Pull, fast-forward only: fails rather than merging when the branch and its upstream have diverged.</summary>
    public Task<ProcessResult> PullAsync(Action<string>? onLine = null, CancellationToken ct = default) => RunAsync(["pull", "--ff-only"], onLine, ct);

    /// <summary>Fetch from the default remote, removing remote-tracking branches that no longer exist there.</summary>
    public Task<ProcessResult> FetchAsync(Action<string>? onLine = null, CancellationToken ct = default) => RunAsync(["fetch", "--prune"], onLine, ct);

    /// <summary>The names of the local branches.</summary>
    public async Task<IReadOnlyList<string>> BranchesAsync(CancellationToken ct = default)
    {
        ProcessResult r = await RunAsync(["branch", "--format=%(refname:short)"], ct: ct).ConfigureAwait(false);
        return r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>Switch to an existing branch (<c>git switch</c>).</summary>
    public Task<ProcessResult> CheckoutAsync(string branch, CancellationToken ct = default) => RunAsync(["switch", branch], ct: ct);

    /// <summary>Create a branch at HEAD and switch to it (<c>git switch -c</c>).</summary>
    public Task<ProcessResult> CreateBranchAsync(string branch, CancellationToken ct = default) => RunAsync(["switch", "-c", branch], ct: ct);

    /// <summary>
    /// The unified diff of a file against HEAD (or of the staged version against HEAD, when <paramref name="staged"/>).
    /// When there is no diff, as for an untracked file, the whole file is shown as added (empty if it has not changed).
    /// </summary>
    public async Task<string> DiffAsync(string path, bool staged = false, CancellationToken ct = default)
    {
        ProcessResult r = await RunAsync(staged ? ["diff", "--cached", "--", path] : ["diff", "HEAD", "--", path], ct: ct).ConfigureAwait(false);
        if (r.Output.Trim().Length == 0)
        {
            // Untracked: show the whole file as added.
            ProcessResult u = await RunAsync(["diff", "--no-index", "--", OperatingSystem.IsWindows() ? "NUL" : "/dev/null", path], ct: ct).ConfigureAwait(false);
            return u.Output;
        }
        return r.Output;
    }

    /// <summary>
    /// Which lines of a file (on disk) differ from HEAD, for the gutter. Empty for an untracked file or when git fails.
    /// </summary>
    public async Task<IReadOnlyList<LineChange>> LineChangesAsync(string path, CancellationToken ct = default)
    {
        ProcessResult r = await RunAsync(["diff", "--no-color", "--no-ext-diff", "-U0", "HEAD", "--", path], ct: ct).ConfigureAwait(false);
        return ParseLineChanges(r.Output);
    }

    /// <summary>
    /// Read the hunk headers of a <c>-U0</c> diff: <c>@@ -a,b +c,d @@</c>. A hunk with no new lines is a deletion, one
    /// with no old lines an addition, anything else a modification.
    /// </summary>
    public static IReadOnlyList<LineChange> ParseLineChanges(string diff)
    {
        var list = new List<LineChange>();
        foreach (Match m in HunkHeader().Matches(diff))
        {
            int oldCount = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 1;
            int newStart = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            int newCount = m.Groups[4].Success ? int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) : 1;
            if (newCount == 0)
            {
                list.Add(new LineChange(Math.Max(1, newStart), 0, LineChangeKind.Deleted));
            }
            else
            {
                list.Add(new LineChange(newStart, newCount, oldCount == 0 ? LineChangeKind.Added : LineChangeKind.Modified));
            }
        }
        return list;
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Multiline)]
    private static partial Regex HunkHeader();

    /// <summary>The URL of <paramref name="remote"/>, or <see langword="null"/> when there is no such remote.</summary>
    public async Task<string?> RemoteUrlAsync(string remote = "origin", CancellationToken ct = default)
    {
        ProcessResult r = await RunAsync(["remote", "get-url", remote], ct: ct).ConfigureAwait(false);
        return r.Success ? r.Output.Trim() : null;
    }

    /// <summary>The full hash of HEAD, or <see langword="null"/> when there is none (no commits yet).</summary>
    public async Task<string?> HeadCommitAsync(CancellationToken ct = default)
    {
        ProcessResult r = await RunAsync(["rev-parse", "HEAD"], ct: ct).ConfigureAwait(false);
        return r.Success ? r.Output.Trim() : null;
    }

    /// <summary>
    /// The last <paramref name="count"/> commits on the current branch, newest first, one line each:
    /// <c>hash  subject  (author, relative date)</c>. Empty when git fails (for example, no commits yet).
    /// </summary>
    public async Task<IReadOnlyList<string>> LogAsync(int count = 30, CancellationToken ct = default)
    {
        ProcessResult r = await RunAsync(["log", $"-{count}", "--format=%h  %s  (%an, %ar)"], ct: ct).ConfigureAwait(false);
        return r.Success ? r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries) : [];
    }
}
