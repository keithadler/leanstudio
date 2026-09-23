using Avalonia.Threading;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.App.Services;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Toolchains;
using LeanStudio.Core.Verification;
using LeanStudio.Lsp;

namespace LeanStudio.App.ViewModels;

public sealed record NewProjectRequest(string Parent, string Name, ProjectTemplate Template, string Toolchain);

/// <summary>What the window provides to the view model: file pickers, prompts and confirmations.</summary>
public interface IDialogs
{
    Task<string?> PickFolderAsync(string title);
    Task<string?> PickFileAsync(string title);
    Task<string?> SaveFileAsync(string title, string suggestedName, string? folder);
    Task<bool> ConfirmAsync(string title, string message);
    Task<string?> PromptAsync(string title, string message, string initial);
    Task<NewProjectRequest?> NewProjectAsync(IReadOnlyList<string> toolchains, string defaultParent);
    Task LaunchAsync(Uri uri);
    /// <summary>Show a file in the system's file manager (or open it, where revealing is not possible).</summary>
    Task RevealAsync(string path);
    Task<string?> SaveWebPageAsync(string title, string suggestedName, string? folder);
    Task CopyTextAsync(string text);
}

/// <summary>
/// The window's state: the open project, the Lean server serving it, the open files, and every panel. Everything
/// that talks to Lean, Lake, elan or Tenet starts here.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IDialogs _dialogs;
    private readonly Dictionary<DocumentViewModel, CancellationTokenSource> _pendingChanges = new();
    private CancellationTokenSource? _caretCts;
    private CancellationTokenSource? _buildCts;
    private TenetWorkspace? _tenet;
    private LeanServer? _server;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _diskCts;

    public MainViewModel(IDialogs dialogs, Settings settings)
    {
        _dialogs = dialogs;
        Settings = settings;
        Info = new InfoViewModel();
        Info.NavigateRequested += (line, col) => ActiveDocument?.Reveal(line, col);
        Navigator = new NavigatorViewModel(() => _tenet);
        Navigator.OpenSourceRequested += (file, line, col) => _ = OpenFileAsync(file, line - 1, col);
        Navigator.OpenUrlRequested += uri => _ = _dialogs.LaunchAsync(uri);
        Toolchains = new ToolchainsViewModel(() => Project, Log, RestartServerAsync);
        Verification = new VerificationViewModel();
        InitFeatures();
        InitLearn();
        InitAssist();
    }

    public Settings Settings { get; }
    public InfoViewModel Info { get; }
    public NavigatorViewModel Navigator { get; }
    public ToolchainsViewModel Toolchains { get; }
    public VerificationViewModel Verification { get; }
    public ObservableList<DocumentViewModel> Documents { get; } = new();
    public ObservableList<ProblemItem> Problems { get; } = new();
    public ObservableList<FileNode> Files { get; } = new();
    public TextDocument Output { get; } = new();

    public LeanServer? Server => _server;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(HasProject), nameof(ProjectName))]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand), nameof(VerifyCommand), nameof(GetMathlibCacheCommand), nameof(UpdateDependenciesCommand), nameof(CleanCommand))]
    private LeanProject? _project;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(HasDocument))]
    private DocumentViewModel? _activeDocument;

    [ObservableProperty]
    private string _serverStatus = "Lean: not started";

    [ObservableProperty]
    private string _toolchainLabel = "";

    [ObservableProperty]
    private string _caretLabel = "";

    [ObservableProperty]
    private string _problemSummary = "No problems";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand), nameof(VerifyCommand), nameof(GetMathlibCacheCommand), nameof(UpdateDependenciesCommand), nameof(CleanCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyText = "";

    [ObservableProperty]
    private int _bottomTab;

    public bool HasProject => Project is not null;
    public bool HasDocument => ActiveDocument is not null;
    public string ProjectName => Project?.Name ?? "No project";

    public string WindowTitle =>
        (ActiveDocument is null ? "" : ActiveDocument.Title + " — ") + (Project is null ? "Lean Studio" : Project.Name + " — Lean Studio");

    partial void OnActiveDocumentChanged(DocumentViewModel? value)
    {
        if (value is null)
        {
            Info.Clear("No file open");
            CaretLabel = "";
        }
        else
        {
            CaretMoved(value, value.CaretLine, value.CaretColumn);
        }
        ScheduleOutline();
        UpdateCanRun();
        UpdateImportsStale();
    }

    // ---- logging ----

    public void Log(string line)
    {
        void Append()
        {
            Output.Insert(Output.TextLength, line + "\n");
            if (Output.TextLength > 2_000_000)
            {
                Output.Remove(0, Output.TextLength - 1_500_000);
            }
            OutputAppended?.Invoke();
        }
        if (Dispatcher.UIThread.CheckAccess())
        {
            Append();
        }
        else
        {
            Dispatcher.UIThread.Post(Append);
        }
    }

    public event Action? OutputAppended;

    // ---- startup ----

    public async Task StartAsync(bool restoreSession = true)
    {
        await Toolchains.RefreshAsync();
        LeanMissing = !Elan.IsInstalled;
        if (!restoreSession)
        {
            return;
        }
        if (!Elan.IsInstalled)
        {
            Log("elan was not found. Install it from " + Elan.InstallUrl + " and restart Lean Studio.");
            ServerStatus = "Lean: elan not installed";
        }
        if (Settings.LastProject is string last && Directory.Exists(last))
        {
            await OpenProjectAsync(last);
            string? active = Settings.LastActiveFile;
            foreach (string f in Settings.LastOpenFiles.Where(File.Exists).ToList())
            {
                await OpenFileAsync(f);
            }
            if (active is not null && Documents.FirstOrDefault(d => d.Path == active) is DocumentViewModel a)
            {
                ActiveDocument = a;
            }
        }
    }

    // ---- projects ----

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        string? dir = await _dialogs.PickFolderAsync("Open a Lean project");
        if (dir is not null)
        {
            await OpenProjectAsync(dir);
        }
    }

    [RelayCommand]
    public async Task OpenRecentAsync(string? dir)
    {
        if (dir is not null && Directory.Exists(dir))
        {
            await OpenProjectAsync(dir);
        }
    }

    public async Task OpenProjectAsync(string dir)
    {
        if (!await CloseAllAsync(force: false))
        {
            return;
        }
        var project = LeanProject.FindEnclosing(dir) is LeanProject enclosing && enclosing.Root == Path.GetFullPath(dir)
            ? enclosing
            : new LeanProject(dir);
        Project = project;
        Settings.RememberProject(project.Root);
        Settings.Save();
        OnPropertyChanged(nameof(RecentProjects));
        var root = new FileNode(project.Root, true);
        root.Load();
        Files.Reset(root.Children);
        Log($"Opened {project.Root}" + (project.IsLakeProject ? " (Lake project)" : "") + (project.Toolchain is string tc ? $", toolchain {tc}" : ""));
        WatchDisk(project.Root);
        _buildMessages = [];
        await SourceControl.OpenAsync(project.Root);
        _ = RefreshMarkersAsync();
        await StartServerAsync();
        await Toolchains.RefreshAsync();
        await ReopenTenetAsync();
    }

    /// <summary>A fresh copy each time, so bindings see a new list when a project is opened.</summary>
    public IReadOnlyList<string> RecentProjects => Settings.RecentProjects.ToList();

    [RelayCommand]
    private async Task NewProjectAsync()
    {
        IReadOnlyList<Toolchain> installed = await Elan.ListAsync();
        string parent = Project is null ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : Path.GetDirectoryName(Project.Root)!;
        NewProjectRequest? req = await _dialogs.NewProjectAsync(installed.Select(t => t.Name).ToList(), parent);
        if (req is null)
        {
            return;
        }
        await RunBusyAsync($"Creating {req.Name}…", async ct =>
        {
            if (!installed.Any(t => t.Name == req.Toolchain))
            {
                Log($"Installing {req.Toolchain} first…");
                await Elan.InstallAsync(req.Toolchain, Log, ct);
            }
            var (result, project) = await Lake.NewAsync(req.Parent, req.Name, req.Template, req.Toolchain, Log, ct);
            if (project is null)
            {
                Log($"lake new failed (exit {result.ExitCode})");
                return;
            }
            await OpenProjectAsync(project.Root);
            if (req.Template == ProjectTemplate.Math)
            {
                Log("This project depends on Mathlib: fetching the prebuilt cache so nothing has to compile for hours…");
                await Lake.UpdateAsync(project, Log, ct);
                await Lake.GetCacheAsync(project, Log, ct);
                await RestartServerAsync();
            }
            string main = Directory.EnumerateFiles(project.Root, "*.lean", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".lake" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .OrderBy(f => f.Contains("Basic", StringComparison.Ordinal) ? 0 : 1).FirstOrDefault() ?? "";
            if (main.Length > 0)
            {
                await OpenFileAsync(main);
            }
        });
    }

    // ---- changes made outside the editor (an AI assistant, git, another editor) ----

    private void WatchDisk(string root)
    {
        _watcher?.Dispose();
        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
            };
        }
        catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException)
        {
            return;
        }
        void Changed(object? sender, FileSystemEventArgs e)
        {
            string sep = Path.DirectorySeparatorChar.ToString();
            if (e.FullPath.Contains(sep + ".lake" + sep, StringComparison.Ordinal) || e.FullPath.Contains(sep + ".git" + sep, StringComparison.Ordinal))
            {
                return;
            }
            Dispatcher.UIThread.Post(ScheduleDiskSync);
        }
        _watcher.Changed += Changed;
        _watcher.Created += Changed;
        _watcher.Deleted += Changed;
        _watcher.Renamed += (s, e) => Changed(s, e);
        _watcher.EnableRaisingEvents = true;
    }

    private void ScheduleDiskSync()
    {
        _diskCts?.Cancel();
        var cts = new CancellationTokenSource();
        _diskCts = cts;
        _ = SyncFromDiskAsync(cts.Token);
    }

    /// <summary>
    /// Pick up files changed on disk: an open file with no unsaved edits takes the new text (and Lean re-checks it),
    /// one with unsaved edits is left alone and the change is logged, and the file tree is refreshed.
    /// </summary>
    private async Task SyncFromDiskAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        foreach (DocumentViewModel d in Documents.Where(d => !d.IsVirtual).ToList())
        {
            if (!File.Exists(d.Path))
            {
                continue;
            }
            string disk;
            try
            {
                disk = await File.ReadAllTextAsync(d.Path, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                continue;
            }
            if (disk == d.SavedText)
            {
                continue;
            }
            if (d.IsDirty)
            {
                Log($"{d.Path} changed on disk, but it has unsaved edits here; keeping yours.");
                continue;
            }
            d.ReloadFrom(disk);
            Log($"Reloaded {Path.GetFileName(d.Path)}: it changed on disk.");
        }
        RefreshFiles();
        ScheduleGitRefresh();
        _ = RefreshMarkersAsync();
    }

    // ---- the AI assistant bridge ----

    /// <summary>What the person is looking at, for an assistant that asks (see StudioBridge).</summary>
    public System.Text.Json.Nodes.JsonObject BridgeContext()
    {
        var o = new System.Text.Json.Nodes.JsonObject { ["project"] = Project?.Root };
        if (ActiveDocument is not DocumentViewModel d)
        {
            return o;
        }
        o["file"] = d.Path;
        o["line"] = d.CaretLine + 1;
        o["column"] = d.CaretColumn + 1;
        o["dirty"] = d.IsDirty;
        string[] lines = d.Lines();
        if (d.CaretLine < lines.Length)
        {
            o["lineText"] = lines[d.CaretLine];
        }
        o["selection"] = SelectionProvider?.Invoke() ?? "";
        o["goals"] = Info.PlainGoals;
        o["messages"] = new System.Text.Json.Nodes.JsonArray(Info.Messages.Select(m => (System.Text.Json.Nodes.JsonNode)m.Text).ToArray());
        return o;
    }

    /// <summary>The editor's selected text; set by the window.</summary>
    public Func<string>? SelectionProvider { get; set; }

    public async Task<string?> BridgeShowAsync(string path, int line, int column)
    {
        if (!File.Exists(path))
        {
            return "no such file: " + path;
        }
        await OpenFileAsync(path, Math.Max(0, line - 1), Math.Max(0, column - 1));
        return null;
    }

    // ---- the Lean server ----

    private async Task StartServerAsync()
    {
        await StopServerAsync();
        if (Project is null || !Elan.IsInstalled)
        {
            return;
        }
        string? fallback = Settings.FallbackToolchain;
        if (Project.Toolchain is null && fallback is null)
        {
            fallback = (await Elan.ListAsync()).Select(t => t.Name).OrderDescending(StringComparer.Ordinal).FirstOrDefault(n => !n.Contains("rc", StringComparison.Ordinal));
        }
        LeanServerCommand cmd = Project.ServerCommand(fallback);
        ToolchainLabel = Project.Toolchain ?? (fallback is null ? "elan default" : fallback + " (not pinned)");
        var server = new LeanServer(cmd);
        _server = server;
        server.StateChanged += s =>
        {
            if (s == LeanServerState.Crashed && _server == server)
            {
                Dispatcher.UIThread.Post(OnServerCrashed);
            }
        };
        server.StateChanged += s => Dispatcher.UIThread.Post(() => ServerStatus = s switch
        {
            LeanServerState.Running => "Lean: ready",
            LeanServerState.Starting => "Lean: starting…",
            LeanServerState.Crashed => "Lean: stopped (restart from the Lean menu)",
            _ => "Lean: stopped",
        });
        server.Log += line => Log("[lean] " + line);
        server.DiagnosticsPublished += (uri, diags) => Dispatcher.UIThread.Post(() => OnDiagnostics(uri, diags));
        server.FileProgress += (uri, ranges) => Dispatcher.UIThread.Post(() => OnProgress(uri, ranges));
        Log($"Starting {cmd} in {cmd.WorkingDirectory}");
        try
        {
            await server.StartAsync();
            foreach (DocumentViewModel d in Documents.Where(d => d.IsLean))
            {
                await server.OpenAsync(d.Uri, d.Document.Text);
            }
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or JsonRpcException)
        {
            Log("Could not start Lean: " + e.Message);
            ServerStatus = "Lean: failed to start";
        }
    }

    private async Task StopServerAsync()
    {
        if (_server is LeanServer s)
        {
            _server = null;
            await s.DisposeAsync();
        }
    }

    [RelayCommand]
    public async Task RestartServerAsync()
    {
        Log("Restarting the Lean server");
        foreach (DocumentViewModel d in Documents)
        {
            d.Diagnostics = [];
            d.Processing = [];
        }
        UpdateProblems();
        await StartServerAsync();
    }

    /// <summary>Re-elaborate the current file against freshly built imports.</summary>
    [RelayCommand]
    private async Task RefreshFileDependenciesAsync()
    {
        if (_server is not null && ActiveDocument is { IsLean: true } d)
        {
            await _server.RefreshDependenciesAsync(d.Uri);
        }
    }

    private void OnDiagnostics(string uri, IReadOnlyList<Diagnostic> diags)
    {
        DocumentViewModel? d = Documents.FirstOrDefault(x => x.Uri == uri);
        if (d is null)
        {
            return;
        }
        d.Diagnostics = diags;
        UpdateProblems();
        if (d == ActiveDocument)
        {
            UpdateImportsStale();
        }
        if (!d.IsProcessing)
        {
            Learn.FileChecked(d);
            _ = AutoFixAsync(d);
        }
    }

    private void OnProgress(string uri, IReadOnlyList<LeanFileProgressRange> ranges)
    {
        DocumentViewModel? d = Documents.FirstOrDefault(x => x.Uri == uri);
        if (d is null)
        {
            return;
        }
        bool wasProcessing = d.IsProcessing;
        d.Processing = ranges;
        if (wasProcessing && !d.IsProcessing)
        {
            // Diagnostics for the finished text trail the progress report slightly.
            DispatcherTimer.RunOnce(() =>
            {
                Learn.FileChecked(d);
                _ = AutoFixAsync(d);
            }, TimeSpan.FromMilliseconds(400));
        }
        if (d == ActiveDocument && wasProcessing && !d.IsProcessing)
        {
            Info.InvalidateSteps();
            CaretMoved(d, d.CaretLine, d.CaretColumn);
            ScheduleOutline();
            UpdateCanRun();
        }
    }

    private void UpdateProblems()
    {
        var items = Documents.SelectMany(d => d.Diagnostics.Where(x => x.Severity <= DiagnosticSeverity.Warning).Select(x => new ProblemItem(d, x)))
            .Concat(BuildProblems())
            .OrderBy(p => p.Severity).ThenBy(p => p.File, StringComparer.Ordinal).ThenBy(p => p.Diagnostic.Range.Start)
            .ToList();
        Problems.Reset(items);
        int errors = items.Count(p => p.Severity == DiagnosticSeverity.Error);
        int warnings = items.Count - errors;
        ProblemSummary = items.Count == 0 ? "No problems" : $"⛔ {errors}  ⚠ {warnings}";
    }

    // ---- documents ----

    [RelayCommand]
    private async Task OpenFileDialogAsync()
    {
        string? f = await _dialogs.PickFileAsync("Open a file");
        if (f is not null)
        {
            await OpenFileAsync(f);
        }
    }

    public async Task<DocumentViewModel?> OpenFileAsync(string path, int? line = null, int? column = null)
    {
        path = Path.GetFullPath(path);
        if (line is not null || !string.Equals(ActiveDocument?.Path, path, StringComparison.Ordinal))
        {
            PushLocation(); // so Back returns to where this jump started
        }
        DocumentViewModel? doc = Documents.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.Ordinal));
        if (doc is null)
        {
            if (!File.Exists(path))
            {
                Log("No such file: " + path);
                return null;
            }
            if (Project is null && LeanProject.FindEnclosing(path) is LeanProject enclosing)
            {
                await OpenProjectAsync(enclosing.Root);
            }
            else if (Project is null)
            {
                await OpenProjectAsync(Path.GetDirectoryName(path)!);
            }
            doc = new DocumentViewModel(path, await File.ReadAllTextAsync(path));
            if (line is null)
            {
                RestoreCaret(doc);
            }
            doc.TextChanged += OnDocumentEdited;
            Documents.Add(doc);
            if (doc.IsLean && _server is { State: LeanServerState.Running } s)
            {
                await s.OpenAsync(doc.Uri, doc.Document.Text);
            }
            ApplyVerdicts(doc);
            RememberOpenFiles();
            ScheduleGitRefresh(full: false);
        }
        ActiveDocument = doc;
        if (line is int l)
        {
            // Let the editor attach before moving the caret.
            Dispatcher.UIThread.Post(() => doc.Reveal(l, column ?? 0), DispatcherPriority.Background);
        }
        return doc;
    }

    [RelayCommand]
    private async Task NewFileAsync()
    {
        string folder = Project?.Root ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? path = await _dialogs.SaveFileAsync("New Lean file", "Untitled.lean", folder);
        if (path is null)
        {
            return;
        }
        if (!File.Exists(path))
        {
            await File.WriteAllTextAsync(path, "");
        }
        RefreshFiles();
        await OpenFileAsync(path);
    }

    private void OnDocumentEdited(DocumentViewModel doc)
    {
        ScheduleAutoSave(doc);
        if (doc.Timings.Count > 0)
        {
            doc.Timings = []; // they describe the text as it was
        }
        if (!doc.IsLean || _server is not { State: LeanServerState.Running } server)
        {
            return;
        }
        if (_pendingChanges.TryGetValue(doc, out CancellationTokenSource? old))
        {
            old.Cancel();
        }
        var cts = new CancellationTokenSource();
        _pendingChanges[doc] = cts;
        _ = SendChangeAsync(server, doc, cts.Token);
    }

    private static async Task SendChangeAsync(LeanServer server, DocumentViewModel doc, CancellationToken ct)
    {
        try
        {
            await Task.Delay(120, ct);
            await server.ChangeAsync(doc.Uri, doc.Document.Text);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    public void CaretMoved(DocumentViewModel doc, int line, int column)
    {
        doc.CaretLine = line;
        doc.CaretColumn = column;
        if (doc != ActiveDocument)
        {
            return;
        }
        CaretLabel = $"Ln {line + 1}, Col {column + 1}";
        RememberCaret(doc);
        ScheduleBlame(doc, line);
        ScheduleC(doc);
        _caretCts?.Cancel();
        var cts = new CancellationTokenSource();
        _caretCts = cts;
        if (!doc.IsLean)
        {
            Info.Clear("Not a Lean file");
            return;
        }
        if (_server is not { State: LeanServerState.Running } server)
        {
            Info.Clear("The Lean server is not running");
            return;
        }
        _ = RefreshInfoAsync(server, doc, new Position(line, column), cts.Token);
    }

    private async Task RefreshInfoAsync(LeanServer server, DocumentViewModel doc, Position pos, CancellationToken ct)
    {
        try
        {
            await Task.Delay(60, ct);
            // Make sure Lean has the text the caret position refers to.
            if (_pendingChanges.TryGetValue(doc, out CancellationTokenSource? pending) && !pending.IsCancellationRequested)
            {
                await Task.Delay(150, ct);
            }
            await Info.RefreshAsync(server, doc, pos);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (ActiveDocument is DocumentViewModel d)
        {
            await SaveDocumentAsync(d);
        }
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (ActiveDocument is not DocumentViewModel d)
        {
            return;
        }
        string? path = await _dialogs.SaveFileAsync("Save as", Path.GetFileName(d.Path), Path.GetDirectoryName(d.Path));
        if (path is null)
        {
            return;
        }
        if (d.IsLean && _server is { State: LeanServerState.Running } s)
        {
            await s.CloseAsync(d.Uri);
        }
        await d.SaveAsync(path);
        if (d.IsLean && _server is { State: LeanServerState.Running } s2)
        {
            await s2.OpenAsync(d.Uri, d.Document.Text);
        }
        RefreshFiles();
        RememberOpenFiles();
    }

    [RelayCommand]
    private async Task SaveAllAsync()
    {
        foreach (DocumentViewModel d in Documents.Where(d => d.IsDirty).ToList())
        {
            await SaveDocumentAsync(d);
        }
    }

    private async Task SaveDocumentAsync(DocumentViewModel d)
    {
        if (d.IsVirtual)
        {
            return;
        }
        try
        {
            await d.SaveAsync();
            if (d.IsLean && _server is { State: LeanServerState.Running } s)
            {
                await s.SaveAsync(d.Uri, d.Document.Text);
            }
            RecordHistory(d);
            ScheduleGitRefresh();
            _ = RefreshMarkersAsync();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"Could not save {d.Path}: {e.Message}");
        }
    }

    [RelayCommand]
    public async Task CloseDocumentAsync(DocumentViewModel? d)
    {
        d ??= ActiveDocument;
        if (d is null)
        {
            return;
        }
        if (d.IsDirty && !d.IsVirtual && !await _dialogs.ConfirmAsync("Unsaved changes", $"Close {Path.GetFileName(d.Path)} without saving?"))
        {
            return;
        }
        int index = Documents.IndexOf(d);
        Documents.Remove(d);
        if (d.IsLean && _server is { State: LeanServerState.Running } s)
        {
            await s.CloseAsync(d.Uri);
        }
        if (ActiveDocument == d)
        {
            ActiveDocument = Documents.Count == 0 ? null : Documents[Math.Clamp(index, 0, Documents.Count - 1)];
        }
        UpdateProblems();
        RememberOpenFiles();
    }

    /// <summary>Close every file; returns false if the user kept one with unsaved changes.</summary>
    public async Task<bool> CloseAllAsync(bool force)
    {
        if (!force && Documents.Any(d => d.IsDirty)
            && !await _dialogs.ConfirmAsync("Unsaved changes", "Some files have unsaved changes. Discard them?"))
        {
            return false;
        }
        Documents.Reset([]);
        ActiveDocument = null;
        UpdateProblems();
        return true;
    }

    private void RememberOpenFiles()
    {
        Settings.LastOpenFiles = Documents.Where(d => !d.IsVirtual).Select(d => d.Path).ToList();
        Settings.LastActiveFile = ActiveDocument is { IsVirtual: false } a ? a.Path : null;
        Settings.Save();
    }

    public void RefreshFiles()
    {
        if (Project is null)
        {
            return;
        }
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        void Collect(IEnumerable<FileNode> nodes)
        {
            foreach (FileNode n in nodes.Where(n => n.IsExpanded))
            {
                expanded.Add(n.Path);
                Collect(n.Children);
            }
        }
        Collect(Files);
        var root = new FileNode(Project.Root, true);
        root.Load();
        void Restore(IEnumerable<FileNode> nodes)
        {
            foreach (FileNode n in nodes.Where(n => expanded.Contains(n.Path)))
            {
                n.IsExpanded = true;
                Restore(n.Children);
            }
        }
        Restore(root.Children);
        Files.Reset(root.Children);
    }

    [RelayCommand]
    private async Task GoToDefinitionAsync()
    {
        if (ActiveDocument is not { IsLean: true } d || _server is not { State: LeanServerState.Running } s)
        {
            return;
        }
        try
        {
            IReadOnlyList<Location> locs = await s.DefinitionAsync(d.Uri, new Position(d.CaretLine, d.CaretColumn));
            if (locs.FirstOrDefault() is Location loc)
            {
                await OpenFileAsync(LeanServer.PathOf(loc.Uri), loc.Range.Start.Line, loc.Range.Start.Character);
            }
        }
        catch (JsonRpcException e)
        {
            Log("Go to definition: " + e.Message);
        }
    }

    [RelayCommand]
    private async Task GoToLineAsync()
    {
        if (ActiveDocument is not DocumentViewModel d)
        {
            return;
        }
        string? s = await _dialogs.PromptAsync("Go to line", "Line number:", (d.CaretLine + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (int.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out int line))
        {
            d.Reveal(Math.Max(0, line - 1), 0);
        }
    }

    [RelayCommand]
    private async Task OpenProblemAsync(ProblemItem? p)
    {
        if (p is null)
        {
            return;
        }
        if (p.Document is DocumentViewModel d && Documents.Contains(d))
        {
            ActiveDocument = d;
            d.Reveal(p.Diagnostic.Range.Start.Line, p.Diagnostic.Range.Start.Character);
        }
        else
        {
            await OpenFileAsync(p.Path, p.Diagnostic.Range.Start.Line, p.Diagnostic.Range.Start.Character);
        }
    }

    // ---- Lake ----

    private bool CanRunProjectTask => Project is { IsLakeProject: true } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRunProjectTask))]
    private async Task BuildAsync()
    {
        await SaveAllAsync();
        bool ok = false;
        await RunBusyAsync("Building…", async ct =>
        {
            BottomTab = 1;
            var r = await Lake.BuildAsync(Project!, onLine: Log, ct: ct);
            ok = r.Success;
            TakeBuildOutput(r.Output);
            Log(r.Success ? "Build succeeded." : $"Build failed (exit {r.ExitCode}).");
        });
        await ReopenTenetAsync();
        if (ok && Settings.VerifyAfterBuild)
        {
            await VerifyAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunProjectTask))]
    private Task GetMathlibCacheAsync() => RunBusyAsync("Fetching Mathlib's cache…", async ct =>
    {
        BottomTab = 1;
        await Lake.GetCacheAsync(Project!, Log, ct);
        await RestartServerAsync();
    });

    [RelayCommand(CanExecute = nameof(CanRunProjectTask))]
    private Task UpdateDependenciesAsync() => RunBusyAsync("Updating dependencies…", async ct =>
    {
        BottomTab = 1;
        await Lake.UpdateAsync(Project!, Log, ct);
        if (Project!.DependsOnMathlib)
        {
            await Lake.GetCacheAsync(Project!, Log, ct);
        }
        await RestartServerAsync();
    });

    [RelayCommand(CanExecute = nameof(CanRunProjectTask))]
    private Task CleanAsync() => RunBusyAsync("Cleaning…", async ct =>
    {
        BottomTab = 1;
        await Lake.CleanAsync(Project!, Log, ct);
        await ReopenTenetAsync();
    });

    [RelayCommand]
    private void CancelTask() => _buildCts?.Cancel();

    private async Task RunBusyAsync(string what, Func<CancellationToken, Task> action)
    {
        _buildCts?.Cancel();
        var cts = new CancellationTokenSource();
        _buildCts = cts;
        IsBusy = true;
        BusyText = what;
        Log(what);
        try
        {
            await action(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled.");
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
            RefreshFiles();
        }
    }

    // ---- Tenet ----

    private async Task ReopenTenetAsync()
    {
        TenetWorkspace? old = _tenet;
        _tenet = null;
        old?.Dispose();
        if (Project is null)
        {
            Navigator.WorkspaceChanged();
            return;
        }
        LeanProject project = Project;
        try
        {
            _tenet = await Task.Run(() => TenetWorkspace.Open(project));
            Log($"Tenet opened {_tenet.ModuleCount:N0} modules ({_tenet.OwnModules.Count} of the project's own).");
        }
        catch (Exception e) when (e is IOException or Tenet.Kernel.KernelException or Tenet.Olean.OleanFormatException or UnauthorizedAccessException)
        {
            Log("Tenet could not open the build: " + e.Message);
        }
        Navigator.WorkspaceChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRunProjectTask))]
    private async Task VerifyAsync()
    {
        if (_tenet is null || _tenet.OwnModules.Count == 0)
        {
            await ReopenTenetAsync();
        }
        if (_tenet is not TenetWorkspace ws || ws.OwnModules.Count == 0)
        {
            Verification.Summary = "Nothing built yet. Build the project first (Lean ▸ Build), then Verify.";
            BottomTab = 2;
            return;
        }
        BottomTab = 2;
        Verification.IsRunning = true;
        Verification.Summary = "Tenet is re-checking the project with an independent kernel…";
        var progress = new Progress<VerificationProgress>(p =>
        {
            Verification.Progress = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            Verification.ProgressText = $"{p.Module}  ({p.ModuleIndex + 1}/{p.ModuleCount})  {p.Done}/{p.Total}";
        });
        try
        {
            VerificationReport r = await ws.VerifyAsync(progress: progress);
            Verification.Show(r);
            Log($"Tenet: {r.Verified} verified, {r.Conditional} resting on an assumption, {r.Rejected} rejected ({r.Elapsed.TotalSeconds:F1}s)");
            foreach (DocumentViewModel d in Documents)
            {
                ApplyVerdicts(d);
            }
        }
        catch (Exception e) when (e is Tenet.Kernel.KernelException or IOException or InvalidOperationException)
        {
            Verification.Summary = "Tenet could not finish: " + e.Message;
            Log("Tenet: " + e);
        }
        finally
        {
            Verification.IsRunning = false;
            Verification.ProgressText = "";
        }
    }

    private void ApplyVerdicts(DocumentViewModel d)
    {
        if (Verification.Report is not VerificationReport r || Project?.ModuleNameOf(d.Path) is not string module)
        {
            d.Verdicts = new Dictionary<int, DeclarationVerdict>();
            return;
        }
        var map = new Dictionary<int, DeclarationVerdict>();
        string[] lines = d.Lines();
        foreach (DeclarationVerdict v in r.Declarations.Where(v => v.Module == module && v.Line is not null))
        {
            int line = Core.Proofs.ProofSteps.DeclarationLine(lines, v.Line!.Value);
            // Several constants can share a line (a structure and its projections); show the most serious.
            if (!map.TryGetValue(line, out DeclarationVerdict? existing) || v.Status > existing.Status)
            {
                map[line] = v;
            }
        }
        d.Verdicts = map;
    }

    [RelayCommand]
    private async Task OpenVerdictAsync(VerdictView? v)
    {
        if (v is null || _tenet is null)
        {
            return;
        }
        string? file = _tenet.SourceFileOf(v.Module);
        if (file is not null)
        {
            await OpenFileAsync(file, (v.Verdict.Line ?? 1) - 1, 0);
        }
    }

    [RelayCommand]
    private async Task ShowDeclarationAtCaretAsync()
    {
        if (ActiveDocument is not DocumentViewModel d)
        {
            return;
        }
        string word = WordAt(d.Document, d.CaretLine, d.CaretColumn);
        if (word.Length > 0)
        {
            Navigator.Query = word;
            await Navigator.ShowAsync(word);
            SidebarTab = LibraryTab;
        }
    }

    [ObservableProperty]
    private int _sidebarTab;

    private static string WordAt(TextDocument doc, int line, int column)
    {
        if (line >= doc.LineCount)
        {
            return "";
        }
        DocumentLine l = doc.GetLineByNumber(line + 1);
        string text = doc.GetText(l.Offset, l.Length);
        static bool IsName(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '\'' or '!' or '?' || char.IsSymbol(c) && c > 127 && c is not ('→' or '←' or '↔' or '∀' or '∃');
        int s = Math.Min(column, text.Length), e = s;
        while (s > 0 && IsName(text[s - 1]))
        {
            s--;
        }
        while (e < text.Length && IsName(text[e]))
        {
            e++;
        }
        return text[s..e].Trim('.');
    }

    public async ValueTask DisposeAsync()
    {
        _watcher?.Dispose();
        RememberOpenFiles();
        await StopServerAsync();
        _tenet?.Dispose();
    }
}
