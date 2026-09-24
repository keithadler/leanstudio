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

/// <summary>What the New Project dialog returns: where to create the project, and how.</summary>
/// <param name="Parent">The folder the project's own folder is created in.</param>
/// <param name="Name">The project's name, which is also its folder's name (passed to <c>lake new</c>).</param>
/// <param name="Template">
/// Which <c>lake new</c> template to start from; <see cref="ProjectTemplate.Math"/> also fetches Mathlib's cache.
/// </param>
/// <param name="Toolchain">
/// The toolchain to create it with and pin, such as <c>leanprover/lean4:stable</c>; installed first if elan does not
/// have it.
/// </param>
public sealed record NewProjectRequest(string Parent, string Name, ProjectTemplate Template, string Toolchain);

/// <summary>What the window provides to the view model: file pickers, prompts and confirmations.</summary>
public interface IDialogs
{
    /// <summary>Ask for a folder.</summary>
    /// <param name="title">The dialog's title.</param>
    /// <returns>The chosen folder, or null if the person cancelled.</returns>
    Task<string?> PickFolderAsync(string title);
    /// <summary>Ask for an existing file to open.</summary>
    /// <param name="title">The dialog's title.</param>
    /// <returns>The chosen file, or null if the person cancelled.</returns>
    Task<string?> PickFileAsync(string title);
    /// <summary>Ask where to save a file. Nothing is written: the caller does that.</summary>
    /// <param name="title">The dialog's title.</param>
    /// <param name="suggestedName">The file name filled in to start with.</param>
    /// <param name="folder">The folder the dialog starts in, or null to let the system choose.</param>
    /// <returns>The chosen path, or null if the person cancelled.</returns>
    Task<string?> SaveFileAsync(string title, string suggestedName, string? folder);
    /// <summary>Ask a yes-or-no question. Also used just to tell the person something, ignoring the answer.</summary>
    /// <param name="title">The dialog's title.</param>
    /// <param name="message">The question or message.</param>
    /// <returns>True if the person agreed.</returns>
    Task<bool> ConfirmAsync(string title, string message);
    /// <summary>Ask for a line of text (a name, a line number, a URL…).</summary>
    /// <param name="title">The dialog's title.</param>
    /// <param name="message">What to enter, shown above the text box.</param>
    /// <param name="initial">The text the box starts with.</param>
    /// <returns>The text entered, or null if the person cancelled.</returns>
    Task<string?> PromptAsync(string title, string message, string initial);
    /// <summary>Show the New Project dialog.</summary>
    /// <param name="toolchains">The names of the installed toolchains, to choose from.</param>
    /// <param name="defaultParent">The folder offered to create the project in.</param>
    /// <returns>What to create, or null if the person cancelled.</returns>
    Task<NewProjectRequest?> NewProjectAsync(IReadOnlyList<string> toolchains, string defaultParent);
    /// <summary>Open a URL (a web page, or a local file) with the system's default application.</summary>
    /// <param name="uri">What to open.</param>
    Task LaunchAsync(Uri uri);
    /// <summary>Show a file in the system's file manager (or open it, where revealing is not possible).</summary>
    Task RevealAsync(string path);
    /// <summary>Ask where to save an HTML page, as <see cref="SaveFileAsync"/> does but for web pages.</summary>
    /// <param name="title">The dialog's title.</param>
    /// <param name="suggestedName">The file name filled in to start with.</param>
    /// <param name="folder">The folder the dialog starts in, or null to let the system choose.</param>
    /// <returns>The chosen path, or null if the person cancelled.</returns>
    Task<string?> SaveWebPageAsync(string title, string suggestedName, string? folder);
    /// <summary>Put text on the clipboard.</summary>
    /// <param name="text">The text to copy.</param>
    Task CopyTextAsync(string text);
}

/// <summary>
/// The window's state: the open project, the Lean server serving it, the open files, and every panel. Everything
/// that talks to Lean, Lake, elan or Tenet starts here.
/// </summary>
/// <remarks>
/// The window binds to this one object. It owns the panels' view models (<see cref="Info"/>, <see cref="Navigator"/>,
/// <see cref="Toolchains"/>, <see cref="Verification"/>, <see cref="SourceControl"/>, <see cref="Learn"/>) and passes
/// them the callbacks they need. Its data comes from the <see cref="LeanServer"/> it starts for the open project
/// (diagnostics, progress, goals, code actions), from running <c>lake</c>, <c>elan</c> and <c>git</c>, from a
/// <see cref="TenetWorkspace"/> opened on what Lake built, and from watching the project folder for changes on disk.
/// It is split by area across partial files: this one holds projects, files, the Lean server, Lake and Tenet.
/// Everything here is meant to be used on the UI thread; events from Lean are posted there.
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IDialogs _dialogs;
    private readonly Dictionary<DocumentViewModel, CancellationTokenSource> _pendingChanges = new();
    private CancellationTokenSource? _caretCts;
    private CancellationTokenSource? _buildCts;
    private TenetWorkspace? _tenet;

    /// <summary>The Lean server's process id while it runs, or null (for checks).</summary>
    public int? ServerProcessId => _server?.ProcessId;

    /// <summary>The project's build opened with Tenet, once it has been (for scripts and checks).</summary>
    public TenetWorkspace? TenetBuild => _tenet;
    private LeanServer? _server;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _diskCts;

    /// <summary>
    /// Create the window's state and its panels. Nothing is opened or started until <see cref="StartAsync"/>.
    /// </summary>
    /// <param name="dialogs">The window's pickers, prompts and confirmations.</param>
    /// <param name="settings">The settings to read, and to update and save as the person works.</param>
    public MainViewModel(IDialogs dialogs, Settings settings)
    {
        _dialogs = dialogs;
        Settings = settings;
        Info = new InfoViewModel
        {
            HideTypes = settings.HideTypeAssumptions,
            HideInstances = settings.HideInstanceAssumptions,
            HideInaccessible = settings.HideInaccessibleNames,
            HideLetValues = settings.HideLetValues,
            TargetFirst = settings.GoalBeforeAssumptions,
        };
        Info.PropertyChanged += (_, e) =>
        {
            // The Tactic State's view choices are kept for next time.
            switch (e.PropertyName)
            {
                case nameof(InfoViewModel.HideTypes) or nameof(InfoViewModel.HideInstances) or nameof(InfoViewModel.HideInaccessible)
                    or nameof(InfoViewModel.HideLetValues) or nameof(InfoViewModel.TargetFirst):
                    Settings.HideTypeAssumptions = Info.HideTypes;
                    Settings.HideInstanceAssumptions = Info.HideInstances;
                    Settings.HideInaccessibleNames = Info.HideInaccessible;
                    Settings.HideLetValues = Info.HideLetValues;
                    Settings.GoalBeforeAssumptions = Info.TargetFirst;
                    Settings.Save();
                    break;
                case nameof(InfoViewModel.Paused) when !Info.Paused && ActiveDocument is DocumentViewModel d:
                    CaretMoved(d, d.CaretLine, d.CaretColumn); // catch up with the cursor
                    break;
            }
        };
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

    /// <summary>What Lean Studio remembers between runs; changed and saved as the person works.</summary>
    public Settings Settings { get; }
    /// <summary>The Tactic State panel.</summary>
    public InfoViewModel Info { get; }
    /// <summary>The Library panel: declarations read from the build by Tenet, and online search.</summary>
    public NavigatorViewModel Navigator { get; }
    /// <summary>The Toolchains panel.</summary>
    public ToolchainsViewModel Toolchains { get; }
    /// <summary>The Tenet panel.</summary>
    public VerificationViewModel Verification { get; }
    /// <summary>The open files, in tab order.</summary>
    public ObservableList<DocumentViewModel> Documents { get; } = new();
    /// <summary>
    /// The Problems panel: errors and warnings from Lean for the open files and from the last build for the others,
    /// errors first.
    /// </summary>
    public ObservableList<ProblemItem> Problems { get; } = new();
    /// <summary>The top level of the project's file tree.</summary>
    public ObservableList<FileNode> Files { get; } = new();
    /// <summary>
    /// The Output panel's text: everything <see cref="Log"/> writes, trimmed when it grows past about two million
    /// characters.
    /// </summary>
    public TextDocument Output { get; } = new();

    /// <summary>
    /// The Lean server for the open project, or null when none was started (no project, or elan is missing). It may
    /// have stopped since: check its <see cref="LeanServer.State"/>.
    /// </summary>
    public LeanServer? Server => _server;

    /// <summary>The open project, or null. Set by <see cref="OpenProjectAsync"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(HasProject), nameof(ProjectName))]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand), nameof(VerifyCommand), nameof(GetMathlibCacheCommand), nameof(UpdateDependenciesCommand), nameof(CleanCommand))]
    private LeanProject? _project;

    /// <summary>
    /// The file in the editor, or null when none is open. Changing it refreshes the Tactic State and the outline.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(HasDocument), nameof(ShowDashboard), nameof(ShowProgressBanner))]
    private DocumentViewModel? _activeDocument;

    /// <summary>The Lean server's state for the status bar, such as <c>Lean: ready</c>.</summary>
    [ObservableProperty]
    private string _serverStatus = "Lean: not started";

    /// <summary>The toolchain Lean runs on, for the status bar; says so when the project does not pin one.</summary>
    [ObservableProperty]
    private string _toolchainLabel = "";

    /// <summary>The caret's line and column for the status bar, 1-based.</summary>
    [ObservableProperty]
    private string _caretLabel = "";

    /// <summary>The error and warning counts for the status bar.</summary>
    [ObservableProperty]
    private string _problemSummary = "No problems";

    /// <summary>
    /// A long task (a build, a clone, fetching a cache…) is running; project tasks cannot start meanwhile.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand), nameof(VerifyCommand), nameof(GetMathlibCacheCommand), nameof(UpdateDependenciesCommand), nameof(CleanCommand))]
    private bool _isBusy;

    /// <summary>What the running long task is doing, for the status bar.</summary>
    [ObservableProperty]
    private string _busyText = "";

    /// <summary>
    /// The bottom panel shown: one of <see cref="ProblemsPanel"/>, <see cref="OutputPanel"/> and the other
    /// <c>…Panel</c> constants.
    /// </summary>
    [ObservableProperty]
    private int _bottomTab;

    /// <summary>A project is open.</summary>
    public bool HasProject => Project is not null;
    /// <summary>A file is open in the editor.</summary>
    public bool HasDocument => ActiveDocument is not null;
    /// <summary>The open project's name, or <c>No project</c>.</summary>
    public string ProjectName => Project?.Name ?? "No project";

    /// <summary>The window's title: the active file, then the project, then Lean Studio.</summary>
    public string WindowTitle =>
        (ActiveDocument is null ? "" : ActiveDocument.Title + " — ") + (Project is null ? "Lean Studio" : Project.Name + " — Lean Studio");

    partial void OnActiveDocumentChanged(DocumentViewModel? value)
    {
        FollowActiveDocument(value);
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

    /// <summary>
    /// Append a line to the Output panel. Safe to call from any thread: off the UI thread it is posted there.
    /// </summary>
    /// <param name="line">The line, without a newline.</param>
    public void Log(string line)
    {
        void Append()
        {
            FeedProgress(line);
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

    /// <summary>
    /// Raised on the UI thread after a line is added to <see cref="Output"/>, so the view can scroll to it.
    /// </summary>
    public event Action? OutputAppended;

    // ---- startup ----

    /// <summary>
    /// Called once the window is shown: list the toolchains, note whether elan is installed, and (unless starting empty)
    /// reopen the last project with the files that were open and the one that was active.
    /// </summary>
    /// <param name="restoreSession">False for a new, empty window.</param>
    public async Task StartAsync(bool restoreSession = true)
    {
        RegisterRemotes();
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

    /// <summary>Ask for a folder and open it as the project.</summary>
    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        string? dir = await _dialogs.PickFolderAsync("Open a Lean project");
        if (dir is not null)
        {
            await OpenProjectAsync(dir);
        }
    }

    /// <summary>Open a project from the recent list. Does nothing if the folder no longer exists.</summary>
    /// <param name="dir">The project's folder.</param>
    [RelayCommand]
    public async Task OpenRecentAsync(string? dir)
    {
        if (dir is not null && Directory.Exists(dir))
        {
            await OpenProjectAsync(dir);
        }
    }

    /// <summary>
    /// Open a folder as the project, in place of the current one. Asks first if files have unsaved changes (and does
    /// nothing if the person keeps them), then closes every file, remembers it among the recent projects, loads the file
    /// tree, watches the folder, opens its Git repository, starts Lean on it, and opens its build with Tenet.
    /// </summary>
    /// <param name="dir">The project's root folder; any folder works, a Lake project or not.</param>
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
        _buildMarks.Clear();
        ModuleTimings.Clear();
        var root = new FileNode(project.Root, true, BuildMarkOf);
        root.Load();
        Files.Reset(root.Children);
        Log($"Opened {project.Root}" + (project.IsLakeProject ? " (Lake project)" : "") + (project.Toolchain is string tc ? $", toolchain {tc}" : ""));
        WatchDisk(project.Root);
        _buildMessages = [];
        await SourceControl.OpenAsync(project.Root);
        _ = RefreshMarkersAsync();
        await StopClangdAsync();
        ScheduleFfiCheck();
        await StartServerAsync();
        await Toolchains.RefreshAsync();
        await ReopenTenetAsync();
    }

    /// <summary>A fresh copy each time, so bindings see a new list when a project is opened.</summary>
    public IReadOnlyList<string> RecentProjects => Settings.RecentProjects.ToList();

    /// <summary>
    /// Create a project with <c>lake new</c> from the New Project dialog, installing its toolchain first if needed,
    /// and open it. A Mathlib project also gets its dependencies and Mathlib's prebuilt cache.
    /// </summary>
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
    /// <returns>
    /// A JSON object with <c>project</c> and, when a file is open, <c>file</c>, 1-based <c>line</c> and <c>column</c>,
    /// <c>dirty</c>, <c>lineText</c>, <c>selection</c>, <c>goals</c> and <c>messages</c>.
    /// </returns>
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

    /// <summary>Show a place in the editor for an assistant, opening the file if needed.</summary>
    /// <param name="path">The file to show.</param>
    /// <param name="line">The 1-based line.</param>
    /// <param name="column">The 1-based column.</param>
    /// <returns>What went wrong, or null.</returns>
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
        RemoteTarget? remote = Project is null ? null : RemoteTargets.For(Project.Root);
        if (Project is null || (!Elan.IsInstalled && remote is null))
        {
            return;
        }
        string? fallback = Settings.FallbackToolchain;
        if (Project.Toolchain is null && fallback is null && remote is null)
        {
            fallback = (await Elan.ListAsync()).Select(t => t.Name).OrderDescending(StringComparer.Ordinal).FirstOrDefault(n => !n.Contains("rc", StringComparison.Ordinal));
        }
        string[] leanArguments = Settings.LeanServerArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        LeanServerCommand cmd = Project.ServerCommand(fallback, leanArguments);
        ToolchainLabel = Project.Toolchain ?? (fallback is null ? "elan default" : fallback + " (not pinned)");
        if (remote is not null)
        {
            ToolchainLabel += " on " + remote.Host;
        }
        var server = new LeanServer(cmd);
        if (Settings.LogServerMessages)
        {
            server.MessageLogPath = Path.Combine(Services.Settings.Directory, "logs", $"lean-server-{DateTime.Now:yyyy-MM-dd-HHmmss}.log");
            Log("Logging every message with Lean's server to " + server.MessageLogPath);
        }
        _server = server;
        server.StateChanged += s =>
        {
            if (s == LeanServerState.Crashed && _server == server)
            {
                Dispatcher.UIThread.Post(OnServerCrashed);
            }
            if (s == LeanServerState.Running && _server == server)
            {
                _infoviewBridge?.ServerRestarted();
            }
        };
        server.StateChanged += s => Dispatcher.UIThread.Post(() => ServerStatus = s switch
        {
            LeanServerState.Running => "Lean: ready",
            LeanServerState.Starting => "Lean: starting…",
            LeanServerState.Crashed => "Lean: stopped (restart from the Lean menu)",
            _ => "Lean: stopped",
        });
        server.Log += line =>
        {
            Log("[lean] " + line);
            ServerLogLine(line);
        };
        server.DiagnosticsPublished += (uri, diags) => Dispatcher.UIThread.Post(() => OnDiagnostics(uri, diags));
        server.FileProgress += (uri, ranges) => Dispatcher.UIThread.Post(() => OnProgress(uri, ranges));
        Log(remote is null
            ? $"Starting {cmd} in {cmd.WorkingDirectory}"
            : $"Starting {Path.GetFileNameWithoutExtension(cmd.FileName)} {string.Join(' ', cmd.Arguments)} on {remote.Host}, in {remote.RemoteRoot}");
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

    /// <summary>
    /// Stop the Lean server and start a new one, clearing every file's diagnostics; open files are sent to it again.
    /// </summary>
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
        d.ProofMarks = ProofMark.From(diags, _server?.SilentDiagnosticsOf(uri) ?? []);
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

    /// <summary>The Problems panel's rows: <see cref="Problems"/>, with repeated messages grouped.</summary>
    public ObservableList<ProblemRow> ProblemRows { get; } = new();

    private readonly HashSet<string> _openProblemGroups = new(StringComparer.Ordinal);

    /// <summary>Open or fold a group of problems with the same message.</summary>
    public void ToggleProblemGroup(ProblemRow row)
    {
        string key = ProblemRow.KeyOf(row.Item);
        if (!_openProblemGroups.Remove(key))
        {
            _openProblemGroups.Add(key);
        }
        ProblemRows.Reset(ProblemRow.Group(Problems, _openProblemGroups));
    }

    private void UpdateProblems()
    {
        var items = Documents.SelectMany(d => d.Diagnostics.Where(x => x.Severity <= DiagnosticSeverity.Warning).Select(x => new ProblemItem(d, x)))
            .Concat(BuildProblems())
            .Concat(FfiProblems())
            .Concat(ToolProblems())
            .OrderBy(p => p.Severity).ThenBy(p => p.File, StringComparer.Ordinal).ThenBy(p => p.Diagnostic.Range.Start)
            .ToList();
        Problems.Reset(items);
        ProblemRows.Reset(ProblemRow.Group(items, _openProblemGroups));
        int errors = items.Count(p => p.Severity == DiagnosticSeverity.Error);
        int warnings = items.Count - errors;
        ProblemSummary = items.Count == 0 ? "No problems" : $"✕ {errors}   ▲ {warnings}";
    }

    // ---- documents ----

    /// <summary>Ask for a file and open it.</summary>
    [RelayCommand]
    private async Task OpenFileDialogAsync()
    {
        string? f = await _dialogs.PickFileAsync("Open a file");
        if (f is not null)
        {
            await OpenFileAsync(f);
        }
    }

    /// <summary>
    /// Open a file in the editor and make it the active document, or switch to it if it is already open. With no
    /// project open, the file's enclosing Lake project (or else its folder) is opened first. A newly opened Lean file
    /// is sent to Lean. The place left is remembered for Back.
    /// </summary>
    /// <param name="path">The file; made absolute.</param>
    /// <param name="line">
    /// The 0-based line to put the caret on, or null to keep it where it was (last time, for a newly opened file).
    /// </param>
    /// <param name="column">The 0-based column, with <paramref name="line"/>.</param>
    /// <returns>The document, or null (with a line logged) if the file does not exist.</returns>
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
            else if (doc.IsC)
            {
                _ = COpenedAsync(doc);
            }
            ApplyVerdicts(doc);
            RememberOpenFiles();
            ScheduleGitRefresh(full: false);
            FileOpened?.Invoke(path);
        }
        ActiveDocument = doc;
        if (line is int l)
        {
            // Let the editor attach before moving the caret.
            Dispatcher.UIThread.Post(() => doc.Reveal(l, column ?? 0), DispatcherPriority.Background);
        }
        return doc;
    }

    /// <summary>Lean Studio as compiled plugins see it: what they add, and loading them.</summary>
    public Services.PluginHost PluginHost => _pluginHost ??= new Services.PluginHost(this);

    private Services.PluginHost? _pluginHost;

    /// <summary>A file was opened in the editor (its full path); raised once per file, when it is first opened.</summary>
    public event Action<string>? FileOpened;

    /// <summary>A file was saved (its full path).</summary>
    public event Action<string>? FileSaved;

    /// <summary>Ask for a new file's name and place, create it empty (unless it exists), and open it.</summary>
    [RelayCommand]
    private async Task NewFileAsync()
    {
        string folder = Project?.Root ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? path = await _dialogs.SaveFileAsync("New Lean file", "Untitled.lean", folder);
        if (path is null)
        {
            return;
        }
        bool created = !File.Exists(path);
        if (created)
        {
            await File.WriteAllTextAsync(path, "");
        }
        RefreshFiles();
        await OpenFileAsync(path);
        if (created)
        {
            await AddToLibraryRootAsync(path);
        }
    }

    private void OnDocumentEdited(DocumentViewModel doc)
    {
        ScheduleAutoSave(doc);
        ForgetToolProblems(doc.Path);
        if (doc.IsC)
        {
            CEdited(doc);
            return;
        }
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
        _ = SendChangeAsync(server, doc, cts);
    }

    /// <summary>
    /// Send a document's edit to Lean now if it is still being held back (edits wait a moment, so typing sends one
    /// change, not one per key). Anything that asks Lean about the text as it is, such as completion, calls this first.
    /// </summary>
    public async Task FlushChangesAsync(DocumentViewModel doc)
    {
        if (_pendingChanges.Remove(doc, out CancellationTokenSource? pending) && !pending.IsCancellationRequested
            && _server is { State: LeanServerState.Running } server && server.IsOpen(doc.Uri))
        {
            pending.Cancel();
            await server.ChangeAsync(doc.Uri, doc.Document.Text);
        }
    }

    private async Task SendChangeAsync(LeanServer server, DocumentViewModel doc, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(120, cts.Token);
            if (_pendingChanges.TryGetValue(doc, out CancellationTokenSource? current) && current == cts)
            {
                _pendingChanges.Remove(doc);
            }
            await server.ChangeAsync(doc.Uri, doc.Document.Text);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// Called by the editor when the caret moves. Records the position; for the active document it also updates the
    /// status bar, blame and the C view, and asks Lean for the Tactic State there after a short pause.
    /// </summary>
    /// <param name="doc">The document whose caret moved.</param>
    /// <param name="line">The 0-based line.</param>
    /// <param name="column">The 0-based column.</param>
    public void CaretMoved(DocumentViewModel doc, int line, int column)
    {
        InfoviewCursor(doc, line, column);
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
        if (doc.IsC)
        {
            Info.Clear(_clangd is { IsRunning: true }
                ? "A C file. clangd checks it as you type, with Lean's headers: hover for types, Ctrl+Space to complete. Go to definition (F12) on a function Lean calls opens its @[extern] declaration."
                : "A C file. Install clangd for errors, hover and completion here. Go to definition (F12) on a function Lean calls opens its @[extern] declaration.");
            return;
        }
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
            await FlushChangesAsync(doc);
            await Info.RefreshAsync(server, doc, pos);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Save the active file, tell Lean, and keep the saved text in local history. A failure is logged, not thrown.
    /// </summary>
    [RelayCommand]
    public async Task SaveAsync()
    {
        if (ActiveDocument is DocumentViewModel d)
        {
            await SaveDocumentAsync(d);
        }
    }

    /// <summary>
    /// Save the active file under a new name; Lean is told it closed the old file and opened the new one.
    /// </summary>
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

    /// <summary>Save every file with unsaved changes.</summary>
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
            FileSaved?.Invoke(d.Path);
            RecordHistory(d);
            ScheduleGitRefresh();
            _ = RefreshMarkersAsync();
            if (d.IsLean || d.IsC)
            {
                ScheduleFfiCheck();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"Could not save {d.Path}: {e.Message}");
        }
    }

    /// <summary>
    /// Close a file, asking first if it has unsaved changes, and tell Lean. The neighbouring tab becomes active.
    /// </summary>
    /// <param name="d">The file to close, or null for the active one.</param>
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
        else if (d.IsC)
        {
            await CClosedAsync(d);
        }
        SidesForget(d, index);
        if (ActiveDocument == d)
        {
            ActiveDocument = Documents.Count == 0 ? null : Documents[Math.Clamp(index, 0, Documents.Count - 1)];
        }
        UpdateProblems();
        RememberOpenFiles();
    }

    /// <summary>Close every file; returns false if the user kept one with unsaved changes.</summary>
    /// <param name="force">Close without asking, discarding unsaved changes.</param>
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

    /// <summary>Reload the project's file tree from disk, keeping the folders that were expanded.</summary>
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
        var root = new FileNode(Project.Root, true, BuildMarkOf);
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

    /// <summary>Ask Lean where the name at the caret is defined and open it there.</summary>
    [RelayCommand]
    private async Task GoToDefinitionAsync()
    {
        // Across the FFI boundary first: an @[extern] to its C function, a C function to its Lean declaration.
        if (ActiveDocument is DocumentViewModel fd && (fd.IsLean || fd.IsC) && await GoAcrossFfiAsync(fd))
        {
            return;
        }
        if (ActiveDocument is { IsC: true } cd && _clangd is { IsRunning: true } c)
        {
            try
            {
                IReadOnlyList<Location> cl = await c.DefinitionAsync(cd.Uri, new Position(cd.CaretLine, cd.CaretColumn));
                if (cl.FirstOrDefault() is Location loc)
                {
                    await OpenFileAsync(LeanServer.PathOf(loc.Uri), loc.Range.Start.Line, loc.Range.Start.Character);
                }
            }
            catch (Exception e) when (e is JsonRpcException or IOException or InvalidOperationException)
            {
                Log("Go to definition: " + e.Message);
            }
            return;
        }
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

    /// <summary>Ask for a 1-based line number and move the caret there.</summary>
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

    /// <summary>Show a problem's place in the editor, opening its file if needed.</summary>
    /// <param name="p">The problem; null does nothing.</param>
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

    /// <summary>
    /// Save everything and run <c>lake build</c>, showing the output and taking its errors into the Problems panel. Then
    /// Tenet reopens the build, and, if the build succeeded and the setting is on, verifies it.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunProjectTask))]
    private async Task BuildAsync()
    {
        await SaveAllAsync();
        bool ok = false;
        // Files opened during the build are checked against a half-built project; they are checked again after it.
        var openedDuringBuild = new HashSet<DocumentViewModel>();
        void Opened(string path)
        {
            if (Documents.FirstOrDefault(d => d.Path == path) is { IsLean: true } d)
            {
                openedDuringBuild.Add(d);
                Log($"{Path.GetFileName(path)} is checked against what's built so far; it will be checked again when the build ends.");
            }
        }
        FileOpened += Opened;
        try
        {
            await RunBusyAsync("Building…", async ct =>
            {
                BottomTab = 1;
                // Warnings and errors reach Problems as the build prints them, not only at the end.
                var printed = new System.Text.StringBuilder();
                DateTimeOffset parsed = default;
                void OnLine(string line)
                {
                    Log(line);
                    lock (printed)
                    {
                        printed.AppendLine(line);
                    }
                    if ((line.StartsWith("warning:", StringComparison.Ordinal) || line.StartsWith("error:", StringComparison.Ordinal))
                        && DateTimeOffset.Now - parsed > TimeSpan.FromSeconds(1))
                    {
                        parsed = DateTimeOffset.Now;
                        string sofar;
                        lock (printed)
                        {
                            sofar = printed.ToString();
                        }
                        Dispatcher.UIThread.Post(() => TakeBuildOutput(sofar));
                    }
                }
                BeginBuildView();
                Core.Processes.ProcessResult r;
                try
                {
                    r = await Lake.BuildAsync(Project!, onLine: OnLine, ct: ct);
                }
                finally
                {
                    StopWatchingCompiles();
                }
                ok = r.Success;
                TakeBuildOutput(r.Output);
                EndBuildView();
                Log(r.Success ? "Build succeeded." : $"Build failed (exit {r.ExitCode}).");
            });
        }
        finally
        {
            FileOpened -= Opened;
        }
        await RecheckAfterBuildAsync(openedDuringBuild);
        await ReopenTenetAsync();
        if (ok && Settings.VerifyAfterBuild)
        {
            await VerifyAsync();
        }
    }

    /// <summary>Download Mathlib's prebuilt files (<c>lake exe cache get</c>), then restart Lean.</summary>
    [RelayCommand(CanExecute = nameof(CanRunProjectTask))]
    private Task GetMathlibCacheAsync() => RunBusyAsync("Fetching Mathlib's cache…", async ct =>
    {
        BottomTab = 1;
        await Lake.GetCacheAsync(Project!, Log, ct);
        await RestartServerAsync();
    });

    /// <summary>
    /// Run <c>lake update</c> (and fetch Mathlib's cache for a project that uses it), then restart Lean.
    /// </summary>
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

    /// <summary>Remove the build (<c>lake clean</c>) and reopen what is left with Tenet.</summary>
    [RelayCommand(CanExecute = nameof(CanRunProjectTask))]
    private Task CleanAsync() => RunBusyAsync("Cleaning…", async ct =>
    {
        BottomTab = 1;
        await Lake.CleanAsync(Project!, Log, ct);
        await ReopenTenetAsync();
    });

    /// <summary>Cancel the running long task: a build, a cache fetch, a task from the Tasks menu, or Tenet's verification.</summary>
    [RelayCommand]
    private void CancelTask()
    {
        _buildCts?.Cancel();
        _taskCts?.Cancel();
        _verifyCts?.Cancel();
    }

    private CancellationTokenSource? _verifyCts;

    /// <summary>
    /// Let the output lines already printed reach the Output panel and the progress reader. They are posted to the
    /// UI thread as they arrive; a task that has just ended must not be summed up before its last lines are read.
    /// </summary>
    private static Task DrainOutputAsync() => Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background).GetTask();

    private async Task RunBusyAsync(string what, Func<CancellationToken, Task> action)
    {
        _buildCts?.Cancel();
        var cts = new CancellationTokenSource();
        _buildCts = cts;
        IsBusy = true;
        BusyText = what;
        BeginProgress();
        Log(what);
        bool cancelled = false;
        try
        {
            await action(cts.Token);
            await DrainOutputAsync();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            Log("Cancelled.");
        }
        finally
        {
            EndProgress(what, cancelled);
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

    /// <summary>
    /// Re-check the project's built modules with Tenet's independent kernel, showing progress and then the verdicts in
    /// the Tenet panel and beside each declaration in the open files. Asks for a build first if there is none.
    /// </summary>
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
        // The same progress in the status bar, whatever panel is open: overall, not just the current module.
        IsBusy = true;
        BusyText = "Verifying with Tenet…";
        BeginProgress();
        _progressReader = null; // Tenet reports its progress directly
        var started = DateTimeOffset.Now;
        var progress = new Progress<VerificationProgress>(p =>
        {
            double overall = p.ModuleCount == 0 ? 0 : (p.ModuleIndex + (p.Total == 0 ? 0 : p.Done / (double)p.Total)) / p.ModuleCount;
            Verification.Progress = 100.0 * overall;
            TimeSpan elapsed = DateTimeOffset.Now - started;
            string left = overall > 0.05 && elapsed > TimeSpan.FromSeconds(20)
                ? " · about " + Core.Workflow.TaskProgress.Format(TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - overall) / overall)) + " left" : "";
            Verification.ProgressText = $"{p.Module}  (module {p.ModuleIndex + 1} of {p.ModuleCount}, {p.Done}/{p.Total} here){left}";
            HasBusyFraction = true;
            BusyFraction = overall;
            BusyPercent = $"{Math.Floor(overall * 100):0}%";
            BusyShort = $"module {p.ModuleIndex + 1} of {p.ModuleCount}{left}";
            BusyDetail = $"module {p.ModuleIndex + 1} of {p.ModuleCount} · {p.Module}{left}";
            BusyElapsed = "Running for " + Core.Workflow.TaskProgress.Format(elapsed);
        });
        _verifyCts?.Cancel();
        var verifyCts = new CancellationTokenSource();
        _verifyCts = verifyCts;
        bool stopped = false;
        try
        {
            VerificationReport r = await ws.VerifyAsync(progress: progress, ct: verifyCts.Token);
            Verification.Show(r);
            Log($"Tenet: {r.Verified} verified, {r.Conditional} resting on an assumption, {r.Rejected} rejected ({r.Elapsed.TotalSeconds:F1}s)");
            foreach (DocumentViewModel d in Documents)
            {
                ApplyVerdicts(d);
            }
        }
        catch (OperationCanceledException)
        {
            stopped = true;
            Verification.Summary = "Verification was cancelled.";
            Log("Tenet: cancelled.");
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
            EndProgress("Tenet's verification", cancelled: stopped);
            IsBusy = false;
            BusyText = "";
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

    /// <summary>Open a verified declaration's source at its line.</summary>
    /// <param name="v">The verdict; null does nothing.</param>
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

    /// <summary>Look up the name at the caret in the Library panel and switch to it.</summary>
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

    /// <summary>
    /// The side panel shown: one of <see cref="FilesTab"/>, <see cref="OutlineTab"/> and the other <c>…Tab</c>
    /// constants.
    /// </summary>
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

    /// <summary>
    /// Save which files are open, stop watching the disk, stop the Lean server, and close Tenet's workspace.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _watcher?.Dispose();
        RememberOpenFiles();
        await StopServerAsync();
        await StopClangdAsync();
        await StopInfoviewAsync();
        _tenet?.Dispose();
    }
}
