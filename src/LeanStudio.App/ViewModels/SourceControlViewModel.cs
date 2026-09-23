using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Git;
using LeanStudio.Core.Processes;

namespace LeanStudio.App.ViewModels;

public sealed record ChangeView(GitChange Change, string Root)
{
    public string Letter => Change.Letter;
    public string FileName => System.IO.Path.GetFileName(Change.Path);
    public string Folder => System.IO.Path.GetDirectoryName(Change.Path) ?? "";
    public string FullPath => System.IO.Path.Combine(Root, Change.Path);
    public bool IsStaged => Change.IsStaged;
    public string Tip => $"{Change.Path}  ({(Change.IsUntracked ? "untracked" : Change.IsStaged ? "staged" : "changed")})";
}

/// <summary>
/// The Source Control panel: the branch and how it stands against its upstream, the changed files, and commit,
/// push, pull, branches, and GitHub (publish, pull requests, open in the browser) through <c>gh</c> when installed.
/// </summary>
public sealed partial class SourceControlViewModel : ObservableObject
{
    private readonly Action<string> _log;
    private readonly Func<string, int?, Task> _openFile;
    private readonly Func<string, string, Task> _openDiff;
    private readonly Func<string, Task<bool>> _confirm;
    private readonly Func<string, string, string, Task<string?>> _prompt;
    private readonly Func<Uri, Task> _launch;
    private readonly Func<(string? Path, int Line)> _activeFile;

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

    public GitRepository? Repository { get; private set; }

    public ObservableList<ChangeView> Staged { get; } = new();
    public ObservableList<ChangeView> Unstaged { get; } = new();
    public ObservableList<string> Branches { get; } = new();
    public ObservableList<string> History { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchLabel))]
    private string _branch = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchLabel))]
    private int _ahead;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchLabel))]
    private int _behind;

    [ObservableProperty]
    private string _commitMessage = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _isRepository;

    [ObservableProperty]
    private bool _hasRemote;

    [ObservableProperty]
    private string? _gitHubRepository;

    [ObservableProperty]
    private string? _pullRequest;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _hasStaged;

    [ObservableProperty]
    private bool _hasUnstaged;

    public bool GhInstalled => GitHub.IsCliInstalled;
    public bool GitInstalled => GitRepository.IsGitInstalled;
    public bool CanPublish => IsRepository && !HasRemote;
    public bool IsOnGitHub => GitHubRepository is not null;

    /// <summary>The branch for the status bar: name and ↑ahead ↓behind when there is anything to sync.</summary>
    public string BranchLabel => Branch.Length == 0 ? "" : "⎇ " + Branch + (Ahead > 0 ? $" ↑{Ahead}" : "") + (Behind > 0 ? $" ↓{Behind}" : "");

    /// <summary>Raised after anything that may change which lines differ from HEAD (commit, discard, checkout).</summary>
    public event Action? RepositoryChanged;

    partial void OnHasRemoteChanged(bool value) => OnPropertyChanged(nameof(CanPublish));
    partial void OnIsRepositoryChanged(bool value) => OnPropertyChanged(nameof(CanPublish));
    partial void OnGitHubRepositoryChanged(string? value) => OnPropertyChanged(nameof(IsOnGitHub));

    public async Task OpenAsync(string? folder)
    {
        Repository = folder is null ? null : GitRepository.Find(folder);
        IsRepository = Repository is not null;
        await RefreshAsync();
    }

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

    [RelayCommand]
    private Task StageAsync(ChangeView? c) => c is null ? Task.CompletedTask : RunAsync($"Staging {c.Change.Path}", r => r.StageAsync([c.Change.Path]), announce: false);

    [RelayCommand]
    private Task UnstageAsync(ChangeView? c) => c is null ? Task.CompletedTask : RunAsync($"Unstaging {c.Change.Path}", r => r.UnstageAsync([c.Change.Path]), announce: false);

    [RelayCommand]
    private Task StageAllAsync() => RunAsync("Staging all changes", r => r.StageAllAsync(), announce: false);

    [RelayCommand]
    private async Task DiscardAsync(ChangeView? c)
    {
        if (c is null || !await _confirm($"Discard your changes to {c.Change.Path}? This cannot be undone."))
        {
            return;
        }
        await RunAsync($"Discarding {c.Change.Path}", r => r.DiscardAsync(c.Change));
    }

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

    [RelayCommand]
    private Task PushAsync() => RunAsync("Pushing", r => r.PushAsync(_log));

    [RelayCommand]
    private Task PullAsync() => RunAsync("Pulling", r => r.PullAsync(_log));

    [RelayCommand]
    private async Task SyncAsync()
    {
        await RunAsync("Pulling", r => r.PullAsync(_log), announce: false);
        if (!Status.StartsWith("Failed", StringComparison.Ordinal))
        {
            await RunAsync("Pushing", r => r.PushAsync(_log));
        }
    }

    [RelayCommand]
    private Task FetchAsync() => RunAsync("Fetching", r => r.FetchAsync(_log));

    [RelayCommand]
    private async Task SwitchBranchAsync(string? branch)
    {
        if (branch is not null && branch != Branch)
        {
            await RunAsync($"Switching to {branch}", r => r.CheckoutAsync(branch));
        }
    }

    [RelayCommand]
    private async Task NewBranchAsync()
    {
        string? name = await _prompt("New branch", "Branch name:", "");
        if (!string.IsNullOrWhiteSpace(name))
        {
            await RunAsync($"Creating branch {name.Trim()}", r => r.CreateBranchAsync(name.Trim()));
        }
    }

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

    [RelayCommand]
    private Task OpenChangedFileAsync(ChangeView? c) => c is null || c.Change.Index == "D" || c.Change.WorkTree == "D" ? Task.CompletedTask : _openFile(c.FullPath, null);

    // ---- GitHub ----

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

    [RelayCommand]
    private async Task OpenPullRequestAsync()
    {
        if (PullRequest?.Split(' ').LastOrDefault() is string url && url.StartsWith("https://", StringComparison.Ordinal))
        {
            await _launch(new Uri(url));
        }
    }

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
