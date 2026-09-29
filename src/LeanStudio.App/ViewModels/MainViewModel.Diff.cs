using CommunityToolkit.Mvvm.Input;
using LeanStudio.App.Views;
using LeanStudio.Core.Git;
using LeanStudio.Core.Workflow;

namespace LeanStudio.App.ViewModels;

/// <summary>
/// Side-by-side diffs: the active file against its last commit or a saved version, and a change in the Git panel,
/// each shown in a window of its own (see <see cref="DiffView"/>).
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>A diff should be shown; the window opens it.</summary>
    public event Action<DiffRequest>? DiffRequested;

    /// <summary>The last diff asked for (for checks).</summary>
    public DiffRequest? LastDiff { get; private set; }

    /// <summary>Show two texts side by side.</summary>
    public void ShowDiff(DiffRequest request)
    {
        LastDiff = request;
        DiffRequested?.Invoke(request);
    }

    /// <summary>Compare the active file, as it is in the editor, with its last commit.</summary>
    [RelayCommand]
    public Task DiffActiveWithHeadAsync() =>
        ActiveDocument is { IsVirtual: false } d ? DiffWithRevisionAsync(d.Path, "HEAD", d.Document.Text) : Task.CompletedTask;

    /// <summary>
    /// Compare a file with its text at <paramref name="rev"/>: the file as it is in the editor when it is open
    /// (<paramref name="now"/>), otherwise as saved.
    /// </summary>
    public async Task DiffWithRevisionAsync(string path, string rev, string? now = null)
    {
        if (GitRepository.Find(path) is not GitRepository git)
        {
            Log($"{Path.GetFileName(path)} is not in a git repository: there is no commit to compare it with.");
            return;
        }
        string? hash = await git.ShortHashAsync(rev);
        string then = hash is null ? "" : await git.FileAtAsync(hash, path) ?? "";
        now ??= Documents.FirstOrDefault(x => x.Path == path)?.Document.Text ?? (File.Exists(path) ? await File.ReadAllTextAsync(path) : "");
        string name = Path.GetFileName(path);
        ShowDiff(new DiffRequest($"{name}: {rev} ↔ now", hash is null ? $"{rev} (no such revision)" : $"{name} at {rev} ({hash})", then,
            $"{name} now", now, path));
    }

    /// <summary>Compare the active file with one of its saved versions from local history.</summary>
    public void DiffWithVersion(HistoryEntry entry)
    {
        DocumentViewModel? d = Documents.FirstOrDefault(x => x.Path == entry.Path);
        string now = d?.Document.Text ?? (File.Exists(entry.Path) ? File.ReadAllText(entry.Path) : "");
        string name = Path.GetFileName(entry.Path);
        ShowDiff(new DiffRequest($"{name}: {entry.Label} ↔ now", $"{name} as saved {entry.Label}", File.ReadAllText(entry.SnapshotFile), $"{name} now", now, entry.Path));
    }
}
