using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Git;
using LeanStudio.Core.Processes;

namespace LeanStudio.App.ViewModels;

/// <summary>A changed file in the Source Control panel.</summary>
/// <param name="Change">The change, as <c>git status</c> reports it.</param>
/// <param name="Root">The repository's root, which the change's path is relative to.</param>
public sealed record ChangeView(GitChange Change, string Root)
{
    /// <summary>
    /// The status letter shown beside the file: <c>M</c>, <c>A</c>, <c>D</c>, <c>R</c>, <c>U</c> (untracked), <c>!</c>
    /// (conflicted)…
    /// </summary>
    public string Letter => Change.Letter;
    /// <summary>The file's name.</summary>
    public string FileName => System.IO.Path.GetFileName(Change.Path);
    /// <summary>The folder it is in, relative to the repository's root.</summary>
    public string Folder => System.IO.Path.GetDirectoryName(Change.Path) ?? "";
    /// <summary>The file's full path.</summary>
    public string FullPath => System.IO.Path.Combine(Root, Change.Path);
    /// <summary>The change is staged.</summary>
    public bool IsStaged => Change.IsStaged;
    /// <summary>The tooltip: the relative path, and whether the change is untracked, staged or changed.</summary>
    public string Tip => $"{Change.Path}  ({(Change.IsUntracked ? "untracked" : Change.IsStaged ? "staged" : "changed")})";
}

/// <summary>
/// The Source Control panel: the branch and how it stands against its upstream, the changed files, and commit,
/// push, pull, branches, and GitHub (publish, pull requests, open in the browser) through <c>gh</c> when installed.
/// </summary>
/// <remarks>
/// Everything is read from and done with the <c>git</c> command line (and <c>gh</c> for GitHub), in the repository
/// found by <see cref="OpenAsync"/>. <see cref="MainViewModel"/> opens the project's repository and refreshes it when
/// files change; the window supplies opening files and diffs, dialogs, and the browser through the constructor's
/// callbacks. Output is written to the Output panel.
/// </remarks>
public sealed partial class SourceControlViewModel : ObservableObject
{
    private readonly Action<string> _log;
    private readonly Func<string, int?, Task> _openFile;
    private readonly Func<string, string, Task> _openDiff;
    private readonly Func<string, Task<bool>> _confirm;
    private readonly Func<string, string, string, Task<string?>> _prompt;
    private readonly Func<Uri, Task> _launch;
    private readonly Func<(string? Path, int Line)> _activeFile;

    /// <summary>Create the panel's state, with no repository until <see cref="OpenAsync"/>.</summary>
    /// <param name="log">Writes a line to the Output panel.</param>
    /// <param name="openFile">Opens a file in the editor, at a 0-based line if one is given.</param>
    /// <param name="openDiff">Shows a file's diff: the file's full path, and the diff's text.</param>
    /// <param name="confirm">Asks a yes-or-no question.</param>
    /// <param name="prompt">Asks for a line of text: title, message and initial text; null when cancelled.</param>
    /// <param name="launch">Opens a URL in the browser.</param>
    /// <param name="activeFile">
    /// The active file and its caret's 1-based line, for Open on GitHub; a null path when none.
    /// </param>
    public SourceControlViewModel(
        Action<string> log,
        Func<string, int?, Task> openFile,
        Func<string, string, Task> openDiff,
        Func<string, Task<bool>> confirm,
        Func<string, string, string, Task<string?>> prompt,
        Func<Uri, Task> launch,
        Func<(string? Path, int Line)> activeFile)
    {
        _log = log;
        _openFile = openFile;
        _openDiff = openDiff;
        _confirm = confirm;
        _prompt = prompt;
        _launch = launch;
        _activeFile = activeFile;
    }

    /// <summary>The repository the project is in, or null when it is in none.</summary>
    public GitRepository? Repository { get; private set; }

    /// <summary>The staged changes.</summary>
    public ObservableList<ChangeView> Staged { get; } = new();
    /// <summary>
    /// The changes not staged, untracked files included. A file staged and then changed again is in both lists.
    /// </summary>
    public ObservableList<ChangeView> Unstaged { get; } = new();
    /// <summary>The local branches.</summary>
    public ObservableList<string> Branches { get; } = new();
    /// <summary>The branch's last 20 commits, newest first, one line each.</summary>
    public ObservableList<string> History { get; } = new();

    /// <summary>The current branch; empty when there is no repository.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchLabel))]
    private string _branch = "";

    /// <summary>How many commits the branch has that its upstream does not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchLabel))]
    private int _ahead;

    /// <summary>How many commits its upstream has that the branch does not.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchLabel))]
    private int _behind;

    /// <summary>The commit message box.</summary>
    [ObservableProperty]
    private string _commitMessage = "";

    /// <summary>
    /// The panel's status line: the number of changes, what is running, or why the last command failed.
    /// </summary>
    [ObservableProperty]
    private string _status = "";

    /// <summary>The project is in a Git repository.</summary>
    [ObservableProperty]
    private bool _isRepository;

    /// <summary>The repository has an <c>origin</c> remote.</summary>
    [ObservableProperty]
    private bool _hasRemote;

    /// <summary>The GitHub repository <c>origin</c> points to, as <c>owner/repo</c>, or null.</summary>
    [ObservableProperty]
    private string? _gitHubRepository;

    /// <summary>
    /// The current branch's open pull request on GitHub, as <c>gh</c> describes it (its URL last), or null.
    /// </summary>
    [ObservableProperty]
    private string? _pullRequest;

    /// <summary>A Git command is running; another is not started until it finishes.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary><see cref="Staged"/> has any.</summary>
    [ObservableProperty]
    private bool _hasStaged;

    /// <summary><see cref="Unstaged"/> has any.</summary>
    [ObservableProperty]
    private bool _hasUnstaged;

    /// <summary>The GitHub CLI (<c>gh</c>) was found.</summary>
    public bool GhInstalled => GitHub.IsCliInstalled;
    /// <summary><c>git</c> was found.</summary>
    public bool GitInstalled => GitRepository.IsGitInstalled;
    /// <summary>There is a repository with no remote, which Publish to GitHub can create.</summary>
    public bool CanPublish => IsRepository && !HasRemote;
    /// <summary>The remote is on GitHub.</summary>
    public bool IsOnGitHub => GitHubRepository is not null;

    /// <summary>The branch for the status bar: name and ↑ahead ↓behind when there is anything to sync.</summary>
    public string BranchLabel => Branch.Length == 0 ? "" : "⎇ " + Branch + (Ahead > 0 ? $" ↑{Ahead}" : "") + (Behind > 0 ? $" ↓{Behind}" : "");

    /// <summary>Raised after anything that may change which lines differ from HEAD (commit, discard, checkout).</summary>
    public event Action? RepositoryChanged;

    partial void OnHasRemoteChanged(bool value) => OnPropertyChanged(nameof(CanPublish));
    partial void OnIsRepositoryChanged(bool value) => OnPropertyChanged(nameof(CanPublish));
    partial void OnGitHubRepositoryChanged(string? value) => OnPropertyChanged(nameof(IsOnGitHub));

    /// <summary>Find the repository containing a folder and show its state.</summary>
    /// <param name="folder">The project's folder, or null for none.</param>
    public async Task OpenAsync(string? folder)
    {
        Repository = folder is null ? null : GitRepository.Find(folder);
        IsRepository = Repository is not null;
        await RefreshAsync();
    }

    /// <summary>
    /// Read the repository's state again with <c>git</c>: branch, changes, remote, branches, recent history, and (with
    /// <c>gh</c>) the branch's pull request.
    /// </summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Repository is not GitRepository repo)
        {
            Staged.Reset([]);
            Unstaged.Reset([]);
            Branch = "";
            Ahead = Behind = 0;
            HasStaged = HasUnstaged = false;
            Status = GitInstalled ? "This folder is not a Git repository." : "Git is not installed.";
            return;
        }
        GitStatus s = await repo.StatusAsync();
        Branch = s.Branch ?? "";
        Ahead = s.Ahead;
        Behind = s.Behind;
        Staged.Reset(s.Changes.Where(c => c.IsStaged).Select(c => new ChangeView(c, repo.Root)));
        // A file both staged and changed again appears in both lists, as in git itself.
        Unstaged.Reset(s.Changes.Where(c => !c.IsStaged || c.WorkTree is not ".").Select(c => new ChangeView(c, repo.Root)));
        HasStaged = Staged.Count > 0;
        HasUnstaged = Unstaged.Count > 0;
        string? remote = await repo.RemoteUrlAsync();
        HasRemote = remote is not null;
        GitHubRepository = GitHub.RepositoryOf(remote);
        Branches.Reset(await repo.BranchesAsync());
        History.Reset(await repo.LogAsync(20));
        Status = s.IsClean ? "No changes." : $"{s.Changes.Count} changed file{(s.Changes.Count == 1 ? "" : "s")}";
        PullRequest = GitHubRepository is not null && GhInstalled ? await GitHub.CurrentPullRequestAsync(repo) : null;
    }

    private async Task RunAsync(string what, Func<GitRepository, Task<ProcessResult>> action, bool announce = true)
    {
        if (Repository is not GitRepository repo || IsBusy)
        {
            return;
        }
        IsBusy = true;
        Status = what;
        _log(what);
        try
        {
            ProcessResult r = await action(repo);
            string output = r.Output.Trim();
            if (output.Length > 0)
            {
                _log(output);
            }
            if (!r.Success)
            {
                Status = "Failed: " + (output.Split('\n').LastOrDefault(l => l.Trim().Length > 0) ?? $"exit {r.ExitCode}");
            }
            else if (announce)
            {
                Status = "Done.";
            }
        }
        finally
        {
            IsBusy = false;
            string keep = Status;
            await RefreshAsync();
            if (keep.StartsWith("Failed", StringComparison.Ordinal))
            {
                Status = keep;
            }
            RepositoryChanged?.Invoke();
        }
    }

    /// <summary>Make a folder a Git repository (<c>git init</c>) and open it.</summary>
    /// <param name="folder">The folder; null does nothing.</param>
    [RelayCommand]
    private async Task InitAsync(string? folder)
    {
        if (folder is null || !GitInstalled)
        {
            return;
        }
        var (r, _) = await GitRepository.InitAsync(folder);
        _log(r.Output.Trim());
        await OpenAsync(folder);
    }

    /// <summary>Stage a file's changes.</summary>
    /// <param name="c">The change; null does nothing.</param>
    [RelayCommand]
    private Task StageAsync(ChangeView? c) => c is null ? Task.CompletedTask : RunAsync($"Staging {c.Change.Path}", r => r.StageAsync([c.Change.Path]), announce: false);

    /// <summary>Unstage a file's changes, keeping them in the working tree.</summary>
    /// <param name="c">The change; null does nothing.</param>
    [RelayCommand]
    private Task UnstageAsync(ChangeView? c) => c is null ? Task.CompletedTask : RunAsync($"Unstaging {c.Change.Path}", r => r.UnstageAsync([c.Change.Path]), announce: false);

    /// <summary>Stage every change.</summary>
    [RelayCommand]
    private Task StageAllAsync() => RunAsync("Staging all changes", r => r.StageAllAsync(), announce: false);

    /// <summary>Throw away a file's changes (deleting it, if untracked), after asking: this cannot be undone.</summary>
    /// <param name="c">The change; null does nothing.</param>
    [RelayCommand]
    private async Task DiscardAsync(ChangeView? c)
    {
        if (c is null || !await _confirm($"Discard your changes to {c.Change.Path}? This cannot be undone."))
        {
            return;
        }
        await RunAsync($"Discarding {c.Change.Path}", r => r.DiscardAsync(c.Change));
    }

    /// <summary>
    /// Commit with <see cref="CommitMessage"/>: the staged changes, or every change when none are staged. The message
    /// is cleared on success.
    /// </summary>
    [RelayCommand]
    private async Task CommitAsync()
    {
        string message = CommitMessage.Trim();
        if (message.Length == 0)
        {
            Status = "Write a commit message first.";
            return;
        }
        await RunAsync(HasStaged ? "Committing staged changes" : "Committing all changes", r => r.CommitAsync(message));
        if (!Status.StartsWith("Failed", StringComparison.Ordinal))
        {
            CommitMessage = "";
        }
    }

    /// <summary>Push the branch.</summary>
    [RelayCommand]
    private Task PushAsync() => RunAsync("Pushing", r => r.PushAsync(_log));

    /// <summary>Pull into the branch.</summary>
    [RelayCommand]
    private Task PullAsync() => RunAsync("Pulling", r => r.PullAsync(_log));

    /// <summary>Pull, then push if the pull succeeded.</summary>
    [RelayCommand]
    private async Task SyncAsync()
    {
        await RunAsync("Pulling", r => r.PullAsync(_log), announce: false);
        if (!Status.StartsWith("Failed", StringComparison.Ordinal))
        {
            await RunAsync("Pushing", r => r.PushAsync(_log));
        }
    }

    /// <summary>Fetch from the remote, to update <see cref="Ahead"/> and <see cref="Behind"/>.</summary>
    [RelayCommand]
    private Task FetchAsync() => RunAsync("Fetching", r => r.FetchAsync(_log));

    /// <summary>Check out another branch.</summary>
    /// <param name="branch">The branch; null or the current one does nothing.</param>
    [RelayCommand]
    private async Task SwitchBranchAsync(string? branch)
    {
        if (branch is not null && branch != Branch)
        {
            await RunAsync($"Switching to {branch}", r => r.CheckoutAsync(branch));
        }
    }

    /// <summary>Ask for a name and create a branch.</summary>
    [RelayCommand]
    private async Task NewBranchAsync()
    {
        string? name = await _prompt("New branch", "Branch name:", "");
        if (!string.IsNullOrWhiteSpace(name))
        {
            await RunAsync($"Creating branch {name.Trim()}", r => r.CreateBranchAsync(name.Trim()));
        }
    }

    /// <summary>
    /// Show a file's diff in a read-only tab: the staged diff for a change that is only staged, else the working
    /// tree's.
    /// </summary>
    /// <param name="c">The change; null does nothing.</param>
    [RelayCommand]
    private async Task OpenChangeAsync(ChangeView? c)
    {
        if (c is null || Repository is not GitRepository repo)
        {
            return;
        }
        string diff = await repo.DiffAsync(c.Change.Path, staged: c.IsStaged && c.Change.WorkTree == ".");
        await _openDiff(c.FullPath, diff);
    }

    /// <summary>Open a changed file in the editor, unless it was deleted.</summary>
    /// <param name="c">The change; null does nothing.</param>
    [RelayCommand]
    private Task OpenChangedFileAsync(ChangeView? c) => c is null || c.Change.Index == "D" || c.Change.WorkTree == "D" ? Task.CompletedTask : _openFile(c.FullPath, null);

    // ---- GitHub ----

    /// <summary>
    /// Create a private GitHub repository for this one with <c>gh</c> and push to it, asking for its name. A repository
    /// with no commits gets an initial one first. Says what to do if <c>gh</c> is missing or not logged in.
    /// </summary>
    [RelayCommand]
    private async Task PublishToGitHubAsync()
    {
        if (Repository is not GitRepository repo)
        {
            return;
        }
        if (!GhInstalled)
        {
            Status = "Publishing needs the GitHub CLI (gh). Install it from cli.github.com and run `gh auth login`.";
            await _launch(new Uri("https://cli.github.com"));
            return;
        }
        if (!await GitHub.IsLoggedInAsync())
        {
            Status = "Log in to GitHub first: run `gh auth login` in a terminal.";
            return;
        }
        string? name = await _prompt("Publish to GitHub", "Repository name (it will be private; change that on GitHub later):", System.IO.Path.GetFileName(repo.Root));
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        GitStatus s = await repo.StatusAsync();
        if (await repo.HeadCommitAsync() is null && !s.IsClean)
        {
            // gh pushes commits; a brand-new repository needs one.
            await repo.CommitAsync("Initial commit");
        }
        await RunAsync($"Publishing to GitHub as {name.Trim()} (private)", r => GitHub.PublishAsync(r, name.Trim(), isPrivate: true, description: null, _log));
    }

    /// <summary>
    /// Push the branch if needed, ask for a title, create a pull request with <c>gh</c>, and open it in the browser.
    /// </summary>
    [RelayCommand]
    private async Task CreatePullRequestAsync()
    {
        if (Repository is not GitRepository repo)
        {
            return;
        }
        if (!GhInstalled)
        {
            Status = "Pull requests need the GitHub CLI (gh): cli.github.com";
            return;
        }
        if (Ahead > 0 || (await repo.StatusAsync()).Upstream is null)
        {
            await RunAsync("Pushing the branch first", r => r.PushAsync(_log), announce: false);
        }
        string? title = await _prompt("Create pull request", "Title (leave empty to use the commits):", "");
        if (title is null)
        {
            return;
        }
        await RunAsync("Creating a pull request", r => GitHub.CreatePullRequestAsync(r, title, null, draft: false, _log));
        if (PullRequest?.Split(' ').LastOrDefault() is string url && url.StartsWith("https://", StringComparison.Ordinal))
        {
            await _launch(new Uri(url));
        }
    }

    /// <summary>Open the active file at its line on GitHub (at the current commit), or the repository's page.</summary>
    [RelayCommand]
    private async Task OpenOnGitHubAsync()
    {
        if (Repository is not GitRepository repo || GitHubRepository is not string gh)
        {
            return;
        }
        (string? path, int line) = _activeFile();
        string gitRef = await repo.HeadCommitAsync() ?? (Branch.Length > 0 ? Branch : "HEAD");
        Uri url = path is not null && path.StartsWith(repo.Root, StringComparison.Ordinal)
            ? new Uri(GitHub.FileUrl(gh, gitRef, System.IO.Path.GetRelativePath(repo.Root, path), line))
            : new Uri($"https://github.com/{gh}");
        await _launch(url);
    }

    /// <summary>Open the branch's pull request in the browser.</summary>
    [RelayCommand]
    private async Task OpenPullRequestAsync()
    {
        if (PullRequest?.Split(' ').LastOrDefault() is string url && url.StartsWith("https://", StringComparison.Ordinal))
        {
            await _launch(new Uri(url));
        }
    }

    /// <summary>
    /// Add a GitHub Actions workflow that builds the project with <c>lean-action</c>, unless there is one. Not
    /// committed.
    /// </summary>
    [RelayCommand]
    private async Task AddCiWorkflowAsync()
    {
        if (Repository is not GitRepository repo)
        {
            return;
        }
        string? written = GitHub.AddLeanWorkflow(repo.Root);
        Status = written is null ? "The project already has a lean-action workflow." : $"Added {System.IO.Path.GetRelativePath(repo.Root, written)}. Commit and push it to run CI on GitHub.";
        _log(Status);
        await RefreshAsync();
    }
}
