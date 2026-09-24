using System.Text.RegularExpressions;
using LeanStudio.Core.Processes;

namespace LeanStudio.Core.Git;

/// <summary>
/// GitHub, through its <c>gh</c> command when it is installed (so the person's own login is used and Lean Studio
/// never handles a token), plus what can be done from a remote URL alone: links to a file or line on github.com.
/// </summary>
public static partial class GitHub
{
    /// <summary>
    /// The path of the <c>gh</c> executable: on PATH (or in elan's bin), else in the usual Homebrew and system
    /// locations; <see langword="null"/> when it is not installed. Searched on every access.
    /// </summary>
    public static string? Gh => Toolchains.Elan.FindExecutable("gh")
        ?? new[] { "/opt/homebrew/bin/gh", "/usr/local/bin/gh", "/usr/bin/gh" }.FirstOrDefault(File.Exists);

    /// <summary>The <c>gh</c> command line is installed (see <see cref="Gh"/>).</summary>
    public static bool IsCliInstalled => Gh is not null;

    /// <summary><c>owner/repo</c> becomes https://github.com/owner/repo.git; anything else is taken as a URL or path.</summary>
    public static string ExpandShorthand(string source)
    {
        string s = source.Trim();
        return Shorthand().IsMatch(s) ? $"https://github.com/{s}.git" : s;
    }

    [GeneratedRegex(@"^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$")]
    private static partial Regex Shorthand();

    /// <summary>owner/repo from a GitHub remote URL (https or ssh), or null for anything that is not GitHub.</summary>
    public static string? RepositoryOf(string? remoteUrl)
    {
        if (remoteUrl is null)
        {
            return null;
        }
        Match m = RemotePattern().Match(remoteUrl.Trim());
        return m.Success ? m.Groups["owner"].Value + "/" + m.Groups["repo"].Value : null;
    }

    [GeneratedRegex(@"github\.com[:/](?<owner>[^/]+)/(?<repo>[^/]+?)(\.git)?/?$")]
    private static partial Regex RemotePattern();

    /// <summary>
    /// The page for a file (and line) on github.com at a commit or branch. <paramref name="repository"/> is
    /// <c>owner/repo</c>, <paramref name="relativePath"/> is relative to the repository root (either slash), and
    /// <paramref name="line"/> is 1-based.
    /// </summary>
    public static string FileUrl(string repository, string gitRef, string relativePath, int? line = null) =>
        $"https://github.com/{repository}/blob/{Uri.EscapeDataString(gitRef)}/{string.Join('/', relativePath.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString))}"
        + (line is int l ? $"#L{l}" : "");

    /// <summary>
    /// Whether <c>gh</c> is installed and logged in, by running <c>gh auth status</c>. <see langword="false"/> when it
    /// is not installed.
    /// </summary>
    public static async Task<bool> IsLoggedInAsync(CancellationToken ct = default)
    {
        if (Gh is not string gh)
        {
            return false;
        }
        ProcessResult r = await ProcessRunner.RunAsync(gh, ["auth", "status"], ct: ct).ConfigureAwait(false);
        return r.Success;
    }

    /// <summary>
    /// Create a GitHub repository named <paramref name="name"/> from a local one, add it as the <c>origin</c> remote and
    /// push (<c>gh repo create --source . --push</c>). Requires <c>gh</c> to be installed and logged in.
    /// </summary>
    public static Task<ProcessResult> PublishAsync(GitRepository repo, string name, bool isPrivate, string? description, Action<string>? onLine = null, CancellationToken ct = default)
    {
        var args = new List<string> { "repo", "create", name, isPrivate ? "--private" : "--public", "--source", repo.Root, "--remote", "origin", "--push" };
        if (!string.IsNullOrWhiteSpace(description))
        {
            args.AddRange(["--description", description]);
        }
        return ProcessRunner.RunAsync(Gh ?? "gh", args, repo.Root, onLine, ct: ct);
    }

    /// <summary>
    /// Open a pull request for the current branch with <c>gh pr create</c>; returns gh's output (the PR URL). With no
    /// <paramref name="title"/>, the title and body are filled from the branch's commits and <paramref name="body"/>
    /// is ignored.
    /// </summary>
    public static Task<ProcessResult> CreatePullRequestAsync(GitRepository repo, string? title, string? body, bool draft, Action<string>? onLine = null, CancellationToken ct = default)
    {
        var args = new List<string> { "pr", "create" };
        if (string.IsNullOrWhiteSpace(title))
        {
            args.Add("--fill");
        }
        else
        {
            args.AddRange(["--title", title, "--body", body ?? ""]);
        }
        if (draft)
        {
            args.Add("--draft");
        }
        return ProcessRunner.RunAsync(Gh ?? "gh", args, repo.Root, onLine, ct: ct);
    }

    /// <summary>
    /// The pull request for the current branch, if there is one, as one line: <c>#number title (STATE) url</c>.
    /// <see langword="null"/> when there is none, or <c>gh</c> is missing or fails.
    /// </summary>
    public static async Task<string?> CurrentPullRequestAsync(GitRepository repo, CancellationToken ct = default)
    {
        if (Gh is not string gh)
        {
            return null;
        }
        ProcessResult r = await ProcessRunner.RunAsync(gh, ["pr", "view", "--json", "number,title,state,url", "--template", "#{{.number}} {{.title}} ({{.state}}) {{.url}}"], repo.Root, ct: ct).ConfigureAwait(false);
        return r.Success ? r.Output.Trim() : null;
    }

    /// <summary>The standard GitHub Actions workflow for a Lean project (leanprover/lean-action builds and tests it).</summary>
    public const string LeanActionWorkflow = """
        name: Lean Action CI

        on:
          push:
          pull_request:
          workflow_dispatch:

        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@v4
              - uses: leanprover/lean-action@v1
        """;

    /// <summary>
    /// Add <see cref="LeanActionWorkflow"/> to a project as <c>.github/workflows/lean_action_ci.yml</c> unless a
    /// <c>.yml</c> workflow there already uses <c>lean-action</c>; returns the file written, or null. Overwrites a
    /// file of that name that does not mention <c>lean-action</c>.
    /// </summary>
    public static string? AddLeanWorkflow(string projectRoot)
    {
        string dir = Path.Combine(projectRoot, ".github", "workflows");
        if (Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.yml").Any(f => File.ReadAllText(f).Contains("lean-action", StringComparison.Ordinal)))
        {
            return null;
        }
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "lean_action_ci.yml");
        File.WriteAllText(path, LeanActionWorkflow + "\n");
        return path;
    }
}
