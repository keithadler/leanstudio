using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.App.Services;
using LeanStudio.Core.Git;
using LeanStudio.Core.Processes;
using LeanStudio.Core.Workflow;
using LeanStudio.Lsp;

namespace LeanStudio.App.ViewModels;

public sealed record MarkerItem(Marker Marker, string Root)
{
    public string File => System.IO.Path.GetRelativePath(Root, Marker.Path);
    public string Where => $"{Marker.Line + 1}";
    public string Kind => Marker.KindLabel;
    public string Declaration => Marker.Declaration ?? "";
    public string Text => Marker.LineText;
    public bool IsSorry => Marker.Kind != MarkerKind.Todo;
}

/// <summary>What makes it a daily workbench: auto-save and local history, the unfinished-work list, whole-project
/// build problems, line blame, tasks, and remembering where you were.</summary>
public sealed partial class MainViewModel
{
    public const int MarkersPanel = 4;

    public ObservableList<MarkerItem> Markers { get; } = new();

    [ObservableProperty]
    private string _markersTitle = "Sorries & TODOs";

    [ObservableProperty]
    private string _blameText = "";

    private readonly LocalHistory _history = new(Path.Combine(Settings.Directory, "history"));
    private readonly Dictionary<DocumentViewModel, CancellationTokenSource> _autoSave = new();
    private IReadOnlyList<BuildMessage> _buildMessages = [];
    private CancellationTokenSource? _markersCts;
    private CancellationTokenSource? _blameCts;
    private CancellationTokenSource? _taskCts;

    public LocalHistory History => _history;

    // ---- auto-save and local history ----

    /// <summary>With auto-save on, save a file a moment after the last keystroke.</summary>
    private void ScheduleAutoSave(DocumentViewModel d)
    {
        if (!Settings.AutoSave || d.IsVirtual || !d.IsDirty)
        {
            return;
        }
        if (_autoSave.TryGetValue(d, out CancellationTokenSource? old))
        {
            old.Cancel();
        }
        var cts = new CancellationTokenSource();
        _autoSave[d] = cts;
        _ = AutoSaveAsync(d, cts.Token);
    }

    private async Task AutoSaveAsync(DocumentViewModel d, CancellationToken ct)
    {
        try
        {
            await Task.Delay(1500, ct);
            if (d.IsDirty && Documents.Contains(d))
            {
                await SaveDocumentAsync(d);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Called when the window loses focus: with auto-save on, nothing is left unsaved.</summary>
    public async Task SaveAllIfAutoSaveAsync()
    {
        if (Settings.AutoSave)
        {
            await SaveAllCommand.ExecuteAsync(null);
        }
    }

    private void RecordHistory(DocumentViewModel d)
    {
        try
        {
            _history.Record(d.Path, d.Document.Text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log("Local history: " + e.Message);
        }
    }

    public IReadOnlyList<HistoryEntry> VersionsOfActive() =>
        ActiveDocument is { IsVirtual: false } d ? _history.Versions(d.Path) : [];

    /// <summary>Bring back an earlier version into the editor (undoable, unsaved until you save).</summary>
    public void RestoreVersion(HistoryEntry entry)
    {
        DocumentViewModel? d = Documents.FirstOrDefault(x => x.Path == entry.Path);
        if (d is null)
        {
            return;
        }
        d.ReplaceAll(File.ReadAllText(entry.SnapshotFile));
        Log($"Restored {Path.GetFileName(d.Path)} as it was on {entry.Label}. Save to keep it, or undo.");
    }

    // ---- sorries and TODOs ----

    [RelayCommand]
    public async Task RefreshMarkersAsync()
    {
        _markersCts?.Cancel();
        var cts = new CancellationTokenSource();
        _markersCts = cts;
        if (Project is null)
        {
            Markers.Reset([]);
            MarkersTitle = "Sorries & TODOs";
            return;
        }
        string root = Project.Root;
        try
        {
            IReadOnlyList<Marker> found = await Task.Run(() => Core.Workflow.Markers.Scan(root, cts.Token), cts.Token);
            Markers.Reset(found.OrderBy(m => m.Kind == MarkerKind.Todo ? 1 : 0).ThenBy(m => m.Path, StringComparer.Ordinal).ThenBy(m => m.Line)
                .Select(m => new MarkerItem(m, root)));
            int sorries = found.Count(m => m.Kind != MarkerKind.Todo), todos = found.Count - sorries;
            MarkersTitle = found.Count == 0 ? "Sorries & TODOs" : $"Sorries {sorries} · TODOs {todos}";
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private async Task OpenMarkerAsync(MarkerItem? m)
    {
        if (m is not null)
        {
            // Opening puts the cursor on the sorry, so the Tactic State shows exactly what is left to prove there.
            await OpenFileAsync(m.Marker.Path, m.Marker.Line, m.Marker.Column);
        }
    }

    // ---- whole-project problems from the last build ----

    private void TakeBuildOutput(string output)
    {
        if (Project is null)
        {
            return;
        }
        _buildMessages = LakeOutput.Parse(output, Project.Root);
        UpdateProblems();
    }

    /// <summary>Build messages for files that are not open: open files have Lean's live diagnostics instead.</summary>
    private IEnumerable<ProblemItem> BuildProblems() =>
        _buildMessages
            .Where(b => !Documents.Any(d => string.Equals(d.Path, b.Path, StringComparison.Ordinal)))
            .Select(b => new ProblemItem(null, b.Path, new Diagnostic(
                new Lsp.Range(new Position(b.Line, b.Column), new Position(b.Line, b.Column)),
                b.IsError ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                b.Message, "lake build")));

    // ---- blame ----

    private void ScheduleBlame(DocumentViewModel d, int line)
    {
        _blameCts?.Cancel();
        var cts = new CancellationTokenSource();
        _blameCts = cts;
        _ = BlameAsync(d, line, cts.Token);
    }

    private async Task BlameAsync(DocumentViewModel d, int line, CancellationToken ct)
    {
        try
        {
            await Task.Delay(500, ct);
            if (!Settings.ShowBlame || d.IsVirtual || d.IsDirty || SourceControl.Repository is not GitRepository repo)
            {
                BlameText = "";
                return;
            }
            BlameLine? b = await Blame.LineAsync(repo, d.Path, line + 1, ct);
            if (!ct.IsCancellationRequested)
            {
                BlameText = b is null ? "" : b.Describe(DateTimeOffset.Now);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---- tasks ----

    public IReadOnlyList<ProjectTask> Tasks() => Project is { IsLakeProject: true } p ? ProjectTasks.For(p) : [];

    public async Task RunTaskAsync(ProjectTask task)
    {
        if (Project is null)
        {
            return;
        }
        await SaveAllCommand.ExecuteAsync(null);
        _taskCts?.Cancel();
        var cts = new CancellationTokenSource();
        _taskCts = cts;
        BottomTab = OutputPanel;
        IsBusy = true;
        BusyText = task.Title;
        Log($"▶ {task.Title}");
        try
        {
            ProcessResult r = await ProcessRunner.RunAsync(task.FileName, task.Arguments, Project.Root, Log, ct: cts.Token);
            Log(r.Success ? "■ Done." : $"■ Exited with code {r.ExitCode}.");
            if (task.Arguments.FirstOrDefault() == "build")
            {
                TakeBuildOutput(r.Output);
                await ReopenTenetAsync();
            }
        }
        catch (OperationCanceledException)
        {
            Log("■ Stopped.");
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            RefreshFiles();
            _ = RefreshMarkersAsync();
        }
    }

    // ---- remembering where you were ----

    private void RememberCaret(DocumentViewModel d)
    {
        if (!d.IsVirtual)
        {
            Settings.CaretPositions[d.Path] = [d.CaretLine, d.CaretColumn];
        }
    }

    /// <summary>
    /// Put a newly opened document's caret where it was last time. Done before the document is shown: showing it
    /// moves the caret (to the start), and that move would otherwise be remembered in place of the real one.
    /// </summary>
    private void RestoreCaret(DocumentViewModel d)
    {
        if (Settings.CaretPositions.TryGetValue(d.Path, out int[]? pos) && pos.Length == 2)
        {
            d.CaretLine = pos[0];
            d.CaretColumn = pos[1];
        }
    }
}
