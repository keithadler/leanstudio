using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text.Json.Nodes;
using LeanStudio.Core.Agents;
using LeanStudio.App.Editor;
using LeanStudio.App.Services;
using LeanStudio.App.ViewModels;
using LeanStudio.Core.Editing;

namespace LeanStudio.App.Views;

/// <summary>
/// The IDE window: menus, sidebar, editor, infoview and bottom panel around one <see cref="MainViewModel"/>, for
/// which it also provides the dialogs (<see cref="IDialogs"/>). On opening it serves the <see cref="StudioBridge"/>
/// pipe if no other window does, and restores the last session or opens <see cref="OpenOnStartup"/>.
/// </summary>
public sealed partial class MainWindow : Window, IDialogs
{
    private readonly MainViewModel _vm;
    private readonly CancellationTokenSource _bridgeCts = new();
    private bool _closing;

    /// <summary>Create the window with the settings saved on disk.</summary>
    public MainWindow()
        : this(Settings.Load())
    {
    }

    /// <summary>Create the window and its view model.</summary>
    /// <param name="settings">The settings to start with; the window changes and saves them.</param>
    public MainWindow(Settings settings)
    {
        InitializeComponent(); // also fills in the fields for named controls (MainGrid, CenterGrid…)
        _vm = new MainViewModel(this, settings);
        DataContext = _vm;
        _vm.ProjectMapReady += map => ShowProjectMap(map);
        _vm.AiChatRequested += question => _ = ShowAiChatAsync(question);
        this.FindControl<OutputView>("OutputView")!.DataContext = _vm;
        this.FindControl<CodeView>("CView")!.DataContext = _vm;
        foreach (LeanEditor ed in Editors)
        {
            ed.QuickFixAtLineRequested += line => _ = QuickFixAtLineAsync(line);
            ed.ApplySettings(settings);
        }
        // The side that gets the focus is the one the commands, the Tactic State and the status bar follow.
        Editor.AddHandler(GotFocusEvent, (_, _) => _vm.EditorFocused(split: false), RoutingStrategies.Bubble);
        SplitEditor.AddHandler(GotFocusEvent, (_, _) => _vm.EditorFocused(split: true), RoutingStrategies.Bubble);
        DocTabs.SelectionChanged += (_, _) => { if (DocTabs.IsKeyboardFocusWithin || DocTabs.IsPointerOver) { _vm.EditorFocused(split: false); } };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSplit))
            {
                LayOutSplit();
            }
        };
        _vm.SelectionProvider = () => EditorControl.TextEditor.SelectedText;
        _vm.InsertRequested += text => EditorControl.InsertAtCaret(text);
        Opened += async (_, _) =>
        {
            BuildRecentMenu();
            if (Core.Platform.MacPlatform.RosettaNotice is string rosetta)
            {
                _vm.Log(rosetta);
            }
            _vm.ScheduleUpdateCheck();
            if (StudioBridge.TryServe(HandleBridgeAsync, _bridgeCts.Token))
            {
                _vm.Log("AI assistants connected through Lean Studio's MCP server can see this window (AI ▸ Connect an AI Assistant).");
            }
            if (OpenOnStartup is string path)
            {
                if (Directory.Exists(path))
                {
                    await _vm.OpenProjectAsync(path);
                }
                else if (File.Exists(path))
                {
                    await _vm.OpenFileAsync(path);
                }
                await _vm.Toolchains.RefreshAsync();
            }
            else
            {
                await _vm.StartAsync(restoreSession: !NewWindow);
            }
            BuildRecentMenu();
        };
        Closing += OnClosing;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        _vm.PluginHost.Load();
        Taskbar = new TaskbarProgress(() => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
        _vm.PropertyChanged += (_, e) =>
        {
            // The long task's progress on the Dock or taskbar icon too, and a nudge when a long one ends.
            if (e.PropertyName is nameof(MainViewModel.BusyFraction) or nameof(MainViewModel.HasBusyFraction))
            {
                if (_vm.HasBusyFraction)
                {
                    Taskbar.Set(_vm.BusyFraction);
                }
                else
                {
                    Taskbar.Clear();
                }
            }
            else if (e.PropertyName == nameof(MainViewModel.DoneNotice) && _vm.DoneNotice.Length > 0)
            {
                Taskbar.RequestAttention();
            }
        };
        WatchUserKeys();
        Deactivated += async (_, _) => await _vm.SaveAllIfAutoSaveAsync();
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.DataTransfer.Contains(Avalonia.Input.DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        InfoviewHost.DataContext = _vm;
        InfoviewSlot.SizeChanged += (_, _) => PlaceInfoview();
        InfoviewSlot.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(PlaceInfoview, DispatcherPriority.Loaded);
        InfoviewSlot.DetachedFromVisualTree += (_, _) => PlaceInfoview();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.RightTab))
            {
                Dispatcher.UIThread.Post(PlaceInfoview, DispatcherPriority.Loaded);
            }
        };
    }

    /// <summary>
    /// Lay the infoview over its tab's area while that tab is shown, and hide it otherwise. The pane lives outside
    /// the tab so its web view (a native control, torn down when it leaves the window) keeps its page.
    /// </summary>
    private void PlaceInfoview()
    {
        bool show = _vm.RightTab == MainViewModel.InfoviewTab && InfoviewSlot.IsAttachedToVisualTree()
            && InfoviewSlot.Bounds.Width > 1 && InfoviewSlot.Bounds.Height > 1;
        if (show && InfoviewSlot.TranslatePoint(default, RightColumn) is Point at)
        {
            InfoviewHost.Margin = new Thickness(at.X, at.Y, 0, 0);
            InfoviewHost.Width = InfoviewSlot.Bounds.Width;
            InfoviewHost.Height = InfoviewSlot.Bounds.Height;
            InfoviewHost.EnsureLoaded();
        }
        InfoviewHost.IsVisible = show;
    }

    /// <summary>The infoview pane (for checks).</summary>
    public InfoviewPane Infoview => InfoviewHost;

    /// <summary>Opened with --new-window: start empty rather than reopening the last session.</summary>
    public bool NewWindow { get; set; }

    /// <summary>A folder or file given on the command line, opened instead of the last session.</summary>
    public string? OpenOnStartup { get; set; }

    /// <summary>The view model the window is bound to.</summary>
    public MainViewModel ViewModel => _vm;

    /// <summary>The editor with the focus: the split (right) side when it has it, else the main one.</summary>
    private LeanEditor EditorControl => _vm.SplitFocused ? SplitEditor : Editor;

    /// <summary>Both editor sides, for settings and events that apply to each.</summary>
    private IEnumerable<LeanEditor> Editors => [Editor, SplitEditor];

    /// <summary>The main (left) editor (for checks).</summary>
    public LeanEditor MainEditorControl => Editor;

    /// <summary>The split (right) editor (for checks).</summary>
    public LeanEditor SplitEditorControl => SplitEditor;

    /// <summary>Give the split side half the width when it is shown, and nothing when it isn't.</summary>
    private void LayOutSplit()
    {
        EditorGrid.ColumnDefinitions[1].Width = new GridLength(_vm.IsSplit ? 4 : 0);
        EditorGrid.ColumnDefinitions[2].Width = _vm.IsSplit ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closing)
        {
            return;
        }
        e.Cancel = true;
        if (_vm.Documents.Any(d => d.IsDirty)
            && !await ConfirmAsync("Unsaved changes", "Some files have unsaved changes. Quit without saving them?"))
        {
            return;
        }
        _closing = true;
        _bridgeCts.Cancel();
        _vm.Settings.Save();
        await _vm.DisposeAsync();
        Close();
    }

    private void BuildRecentMenu()
    {
        MenuItem recent = this.FindControl<MenuItem>("RecentMenu")!;
        recent.Items.Clear();
        foreach (string p in _vm.Settings.RecentProjects)
        {
            // A TextBlock, not a string: in a string header "_" marks a keyboard shortcut and disappears.
            var item = new MenuItem { Header = new TextBlock { Text = p } };
            item.Click += async (_, _) =>
            {
                await _vm.OpenProjectAsync(p);
                BuildRecentMenu();
            };
            recent.Items.Add(item);
        }
        recent.IsEnabled = recent.Items.Count > 0;
    }

    /// <summary>Answer an assistant's request (see StudioBridge), on the UI thread.</summary>
    private Task<JsonObject> HandleBridgeAsync(JsonObject request) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            switch (request["method"]?.GetValue<string>())
            {
                case "context":
                    return _vm.BridgeContext();
                case "show":
                    string? error = await _vm.BridgeShowAsync(
                        request["path"]?.GetValue<string>() ?? "",
                        request["line"]?.GetValue<int>() ?? 1,
                        request["column"]?.GetValue<int>() ?? 1);
                    if (error is null)
                    {
                        Activate();
                    }
                    return error is null ? new JsonObject { ["ok"] = true } : new JsonObject { ["error"] = error };
                default:
                    return new JsonObject { ["error"] = "unknown request" };
            }
        });

    // ---- pickers, quick fix, and the keys that open them ----

    private static bool Cmd(KeyEventArgs e) =>
        OperatingSystem.IsMacOS() ? e.KeyModifiers.HasFlag(KeyModifiers.Meta) : e.KeyModifiers.HasFlag(KeyModifiers.Control);

    // ---- the person's own shortcuts (keybindings.json) ----

    private IReadOnlyList<(KeyChord Chord, string Command)> _userKeys = [];
    private FileSystemWatcher? _keysWatcher;

    /// <summary>Where the person's own shortcuts are kept.</summary>
    public static string KeybindingsPath => Path.Combine(Settings.Directory, KeyBindingsFile.FileName);

    /// <summary>Read keybindings.json (again), saying in Output what couldn't be read. Returns how many bindings there are.</summary>
    public int LoadUserKeys()
    {
        string path = KeybindingsPath;
        if (!File.Exists(path))
        {
            _userKeys = [];
            return 0;
        }
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return _userKeys.Count; // being written; the watcher calls again
        }
        var (bindings, problems) = KeyBindingsFile.Parse(json);
        _userKeys = bindings.Select(b => (KeyChord.Parse(b.Key, OperatingSystem.IsMacOS())!, b.Command)).ToList();
        var titles = new HashSet<string>(Commands().Select(c => c.Title), StringComparer.OrdinalIgnoreCase);
        foreach (string p in problems.Concat(bindings.Where(b => !titles.Contains(b.Command)).Select(b => $"no command is called \"{b.Command}\" (use the name the command palette shows)")))
        {
            _vm.Log("keybindings.json: " + p);
        }
        return _userKeys.Count;
    }

    private void WatchUserKeys()
    {
        Directory.CreateDirectory(Settings.Directory);
        _keysWatcher = new FileSystemWatcher(Settings.Directory, "*.json") { EnableRaisingEvents = true };
        void Changed(FileSystemEventArgs e) => Dispatcher.UIThread.Post(() =>
        {
            if (e.Name == KeyBindingsFile.FileName)
            {
                LoadUserKeys();
            }
            else if (e.Name == AbbreviationsFileName)
            {
                LoadAbbreviations();
            }
        });
        _keysWatcher.Changed += (_, e) => Changed(e);
        _keysWatcher.Created += (_, e) => Changed(e);
        LoadUserKeys();
        LoadAbbreviations();
    }

    // ---- the person's own Unicode abbreviations (abbreviations.json) ----

    private const string AbbreviationsFileName = "abbreviations.json";

    /// <summary>Where the person's own Unicode abbreviations are kept.</summary>
    public static string AbbreviationsPath => Path.Combine(Settings.Directory, AbbreviationsFileName);

    /// <summary>Read abbreviations.json (again) into Lean's Unicode input. Returns how many there are.</summary>
    public int LoadAbbreviations()
    {
        if (!File.Exists(AbbreviationsPath))
        {
            Abbreviations.SetCustom(new Dictionary<string, string>());
            return 0;
        }
        try
        {
            var (custom, problems) = Abbreviations.ParseCustom(File.ReadAllText(AbbreviationsPath));
            Abbreviations.SetCustom(custom);
            foreach (string p in problems)
            {
                _vm.Log("abbreviations.json: " + p);
            }
            return custom.Count;
        }
        catch (IOException)
        {
            return 0; // being written; the watcher calls again
        }
    }

    /// <summary>Open abbreviations.json, starting it with an example if it doesn't exist.</summary>
    public async Task EditAbbreviationsAsync()
    {
        if (!File.Exists(AbbreviationsPath))
        {
            Directory.CreateDirectory(Settings.Directory);
            await File.WriteAllTextAsync(AbbreviationsPath,
                "// Your own Unicode input: type \\name and get the symbol, as with \\alpha. Yours win over built-in ones.\n"
                + "// The same format as VS Code's lean4.input.customTranslations. Save to apply.\n"
                + "{\n  // \"zeta5\": \"ζ(5)\",\n}\n");
        }
        await _vm.OpenFileAsync(AbbreviationsPath);
    }

    /// <summary>Run the command bound to this key in keybindings.json, if there is one.</summary>
    private bool RunUserKey(KeyEventArgs e)
    {
        string key = e.Key.ToString();
        KeyModifiers m = e.KeyModifiers;
        foreach ((KeyChord chord, string command) in _userKeys)
        {
            if (chord.Key == key && chord.Ctrl == m.HasFlag(KeyModifiers.Control) && chord.Alt == m.HasFlag(KeyModifiers.Alt)
                && chord.Shift == m.HasFlag(KeyModifiers.Shift) && chord.Meta == m.HasFlag(KeyModifiers.Meta))
            {
                var run = Commands().FirstOrDefault(c => string.Equals(c.Title, command, StringComparison.OrdinalIgnoreCase)).Run;
                if (run is null)
                {
                    return false;
                }
                _ = run();
                return true;
            }
        }
        return false;
    }

    /// <summary>Open keybindings.json, starting it from a template that lists every command if it doesn't exist.</summary>
    public async Task EditKeybindingsAsync()
    {
        string path = KeybindingsPath;
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Settings.Directory);
            await File.WriteAllTextAsync(path, KeyBindingsFile.Template(Commands().Select(c => (c.Title, c.Keys))));
        }
        await _vm.OpenFileAsync(path);
    }

    /// <summary>
    /// Lean's file workers, biggest first, with the file, memory and time running of each. Picking one stops it; if
    /// its file is open, Lean restarts it fresh. For a file that has Lean stuck or eating memory.
    /// </summary>
    public async Task LeanProcessesAsync()
    {
        IReadOnlyList<Core.Toolchains.LeanWorker> workers = await Core.Toolchains.LeanProcesses.ListAsync(_vm.Project?.Root);
        if (workers.Count == 0)
        {
            _vm.Log("Lean has no file open for this project.");
            return;
        }
        long total = workers.Sum(w => w.MemoryBytes);
        _vm.Log($"Lean has {workers.Count} file{(workers.Count == 1 ? "" : "s")} open, using {new Core.Toolchains.LeanWorker(0, "", total, default).Memory} in all.");
        var items = workers.Select(w => new PickerItem(
            $"{Path.GetFileName(w.File)}    {w.Memory}",
            $"running {Core.Workflow.TaskProgress.Format(w.Running)} · {w.File} · pick to stop it (and restart the file if it's open)",
            async () =>
            {
                bool stopped = Core.Toolchains.LeanProcesses.Kill(w.Pid);
                _vm.Log(stopped ? $"Stopped Lean's worker for {Path.GetFileName(w.File)} ({w.Memory})." : $"Couldn't stop Lean's worker for {Path.GetFileName(w.File)}.");
                if (stopped && _vm.Documents.FirstOrDefault(d => d.Path == w.File) is DocumentViewModel open)
                {
                    _vm.ActiveDocument = open;
                    await _vm.RestartFileCommand.ExecuteAsync(null);
                }
            })).ToList();
        await Picker.ShowAsync(this, "Lean's file workers, biggest first: pick one to stop it",
            (q, _) => Task.FromResult<IReadOnlyList<PickerItem>>(Core.Editing.Fuzzy.Filter(items, q, i => i.Title).ToList()));
    }

    private void OnLeanProcesses(object? sender, RoutedEventArgs e) => _ = LeanProcessesAsync();

    private async void OnUpdateMathlib(object? sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync("Update Mathlib?", "Lake moves Mathlib to the newest version your lakefile allows, and the project to its toolchain. Then Lean Studio fetches the cache, builds, and shows what broke and which deprecated names it can rename for you.\n\nlake-manifest.json and lean-toolchain are backed up first: Lean ▸ Undo Last Dependency Update puts them back."))
        {
            await _vm.UpdateDependencyAsync();
        }
    }

    /// <summary>
    /// The instances of the type class at the cursor (or one asked for), asked of Lean itself for this file's
    /// imports. Picking one shows it in the Library, with its source.
    /// </summary>
    public async Task InstancesOfClassAsync()
    {
        if (_vm.ActiveDocument is not { IsLean: true } d)
        {
            return;
        }
        string? word = Core.Editing.MultiCursor.WordAt(d.Document.Text, EditorControl.TextEditor.CaretOffset) is { } w
            ? d.Document.Text[w.Start..w.End] : null;
        string? cls = await PromptAsync("Instances of a class", "The type class to list the instances of:", word ?? "");
        if (string.IsNullOrWhiteSpace(cls))
        {
            return;
        }
        _vm.Log($"Asking Lean for the instances of {cls.Trim()}…");
        try
        {
            var (name, instances) = await Core.Workflow.Instances.OfAsync(
                _vm.Project ?? new Core.Projects.LeanProject(Path.GetDirectoryName(d.Path)!), d.Path, d.Document.Text, cls.Trim());
            _vm.Log($"{name}: {instances.Count} instance{(instances.Count == 1 ? "" : "s")} visible from this file's imports.");
            var items = instances.Select(i => new PickerItem(i.Name, i.Type, async () =>
            {
                _vm.SidebarTab = MainViewModel.LibraryTab;
                await _vm.Navigator.ShowAsync(i.Name);
            })).ToList();
            await Picker.ShowAsync(this, $"{instances.Count} instances of {name}",
                (q, _) => Task.FromResult<IReadOnlyList<PickerItem>>(Core.Editing.Fuzzy.Filter(items, q, i => i.Title + " " + i.Detail).ToList()));
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            _vm.Log("Instances: " + e.Message.Split('\n')[0]);
        }
    }

    private void OnInstancesOfClass(object? sender, RoutedEventArgs e) => _ = InstancesOfClassAsync();

    private void OnEditKeybindings(object? sender, RoutedEventArgs e) => _ = EditKeybindingsAsync();

    private void OnEditAbbreviations(object? sender, RoutedEventArgs e) => _ = EditAbbreviationsAsync();

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (RunUserKey(e))
        {
            e.Handled = true;
            return;
        }
        bool cmd = Cmd(e), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        Action? action = (e.Key, cmd, shift) switch
        {
            (Key.P, true, true) => () => _ = CommandPaletteAsync(),
            (Key.P, true, false) when e.KeyModifiers.HasFlag(KeyModifiers.Alt) => () => _vm.ProveItCommand.Execute(null),
            (Key.A, true, false) when e.KeyModifiers.HasFlag(KeyModifiers.Alt) => () => _vm.AskAiToProveCommand.Execute(null),
            (Key.K, true, false) when e.KeyModifiers.HasFlag(KeyModifiers.Alt) => () => _vm.AskAiCommand.Execute(null),
            (Key.R, true, false) when e.KeyModifiers.HasFlag(KeyModifiers.Alt) => () => OnShowRepl(null, new RoutedEventArgs()),
            (Key.H, true, false) when e.KeyModifiers.HasFlag(KeyModifiers.Alt) => () => _vm.ShowCallersCommand.Execute(null),
            (Key.P, true, false) => () => _ = QuickOpenAsync(),
            (Key.T, true, false) => () => _ = GoToSymbolAsync(),
            (Key.F, true, true) => () => ShowFindInFiles(),
            (Key.OemPeriod, true, false) when e.KeyModifiers.HasFlag(KeyModifiers.Alt) => () => _vm.FixAllInFileCommand.Execute(null),
            (Key.OemPeriod, true, false) => () => _ = QuickFixAsync(),
            (Key.B, true, true) => () => _ = RunTaskAsync(),
            (Key.OemComma, true, false) => () => OnPreferences(null, new RoutedEventArgs()),
            (Key.Left, false, false) when e.KeyModifiers == KeyModifiers.Alt && !OperatingSystem.IsMacOS() => () => _vm.GoBackCommand.Execute(null),
            (Key.Right, false, false) when e.KeyModifiers == KeyModifiers.Alt && !OperatingSystem.IsMacOS() => () => _vm.GoForwardCommand.Execute(null),
            (Key.OemMinus, false, false) when OperatingSystem.IsMacOS() && e.KeyModifiers == KeyModifiers.Control => () => _vm.GoBackCommand.Execute(null),
            (Key.OemMinus, false, true) when OperatingSystem.IsMacOS() && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift) => () => _vm.GoForwardCommand.Execute(null),
            (Key.J, true, false) => () => TogglePanel(),
            (Key.B, true, false) when e.KeyModifiers.HasFlag(KeyModifiers.Alt) => () => ToggleSidebar(),
            (Key.I, true, false) when e.KeyModifiers.HasFlag(KeyModifiers.Alt) => () => ToggleInfo(),
            (Key.Z, true, false) when e.KeyModifiers.HasFlag(KeyModifiers.Alt) => () => Zen(),
            (Key.Z, false, false) when e.KeyModifiers == KeyModifiers.Alt => () => { _vm.Settings.WordWrap = !_vm.Settings.WordWrap; ApplySettings(); },
            _ => null,
        };
        if (action is not null)
        {
            e.Handled = true;
            action();
        }
    }

    private void OnCommandPalette(object? sender, RoutedEventArgs e) => _ = CommandPaletteAsync();
    private void OnRunTask(object? sender, RoutedEventArgs e) => _ = RunTaskAsync();
    private void OnRunShell(object? sender, RoutedEventArgs e) => _ = RunShellAsync();
    private void OnLocalHistory(object? sender, RoutedEventArgs e) => _ = LocalHistoryAsync();
    private void OnToggleSidebar(object? sender, RoutedEventArgs e) => ToggleSidebar();
    private void OnTogglePanel(object? sender, RoutedEventArgs e) => TogglePanel();
    private void OnToggleInfo(object? sender, RoutedEventArgs e) => ToggleInfo();
    private void OnZen(object? sender, RoutedEventArgs e) => Zen();

    private ProjectMapWindow? _mapWindow;

    /// <summary>The project map window, while one is open.</summary>
    public ProjectMapWindow? MapWindow => _mapWindow;

    /// <summary>Show a project map, replacing one that is open.</summary>
    public ProjectMapWindow ShowProjectMap(Core.Verification.ProjectMap map)
    {
        _mapWindow?.Close();
        _mapWindow = new ProjectMapWindow(map, _vm.ProjectName, n => { _vm.OpenMapNode(n); Activate(); });
        _mapWindow.Closed += (_, _) => _mapWindow = null;
        _mapWindow.Show(this);
        return _mapWindow;
    }

    private async void OnCheckFfi(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<Core.Workflow.FfiProblem> problems = await _vm.CheckFfiNowAsync();
        _vm.Log(problems.Count == 0 ? "C bindings: every @[extern] has its C function, with the right number of arguments." : $"C bindings: {problems.Count} problem(s), in Problems.");
        if (problems.Count > 0)
        {
            _vm.BottomTab = MainViewModel.ProblemsPanel;
        }
    }

    private void OnShowRepl(object? sender, RoutedEventArgs e)
    {
        _vm.BottomTab = MainViewModel.ReplPanel;
        Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("ReplBox")?.Focus(), DispatcherPriority.Background);
    }

    private void OnReplKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _ = RunReplAsync();
                break;
            case Key.Up:
                e.Handled = true;
                _vm.ReplHistory(-1);
                break;
            case Key.Down:
                e.Handled = true;
                _vm.ReplHistory(1);
                break;
        }
    }

    private async Task RunReplAsync()
    {
        await _vm.RunReplCommand.ExecuteAsync(null);
        if (this.FindControl<ListBox>("ReplList") is { } list && _vm.ReplEntries.Count > 0)
        {
            list.ScrollIntoView(_vm.ReplEntries[^1]);
        }
        this.FindControl<TextBox>("ReplBox")?.Focus();
    }

    private void OnTimingTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: TimingItem t })
        {
            _vm.OpenTimingCommand.Execute(t);
        }
    }

    private void OnModuleTimingTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ModuleTiming t })
        {
            _vm.OpenModuleTimingCommand.Execute(t);
        }
    }

    private void OnMarkerTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: MarkerItem m })
        {
            _vm.OpenMarkerCommand.Execute(m);
        }
    }

    // ---- layout ----

    private GridLength _sidebarWidth = new(300), _infoWidth = new(420), _panelHeight = new(220);

    private static bool Hidden(GridLength g) => g.IsAbsolute && g.Value == 0;

    /// <summary>Hide the sidebar, or show it again at the width it had.</summary>
    public void ToggleSidebar() => Toggle(MainGrid.ColumnDefinitions[0], MainGrid.ColumnDefinitions[1], ref _sidebarWidth);

    /// <summary>Hide the infoview on the right, or show it again at the width it had.</summary>
    public void ToggleInfo() => Toggle(MainGrid.ColumnDefinitions[4], MainGrid.ColumnDefinitions[3], ref _infoWidth);

    /// <summary>Hide the bottom panel (problems, output), or show it again at the height it had.</summary>
    public void TogglePanel()
    {
        RowDefinition row = CenterGrid.RowDefinitions[3], splitter = CenterGrid.RowDefinitions[2];
        if (Hidden(row.Height))
        {
            row.Height = _panelHeight;
            splitter.Height = new GridLength(4);
        }
        else
        {
            _panelHeight = row.Height;
            row.Height = new GridLength(0);
            splitter.Height = new GridLength(0);
        }
    }

    private static void Toggle(ColumnDefinition col, ColumnDefinition splitter, ref GridLength remembered)
    {
        if (Hidden(col.Width))
        {
            col.Width = remembered;
            splitter.Width = new GridLength(4);
        }
        else
        {
            remembered = col.Width;
            col.Width = new GridLength(0);
            splitter.Width = new GridLength(0);
        }
    }

    /// <summary>Zen mode: only the editor and the goals. Again to bring everything back.</summary>
    public void Zen()
    {
        bool anyVisible = !Hidden(MainGrid.ColumnDefinitions[0].Width) || !Hidden(CenterGrid.RowDefinitions[3].Height);
        if (anyVisible)
        {
            if (!Hidden(MainGrid.ColumnDefinitions[0].Width))
            {
                ToggleSidebar();
            }
            if (!Hidden(CenterGrid.RowDefinitions[3].Height))
            {
                TogglePanel();
            }
        }
        else
        {
            ToggleSidebar();
            TogglePanel();
        }
    }

    // ---- tasks and history ----

    private Task RunTaskAsync()
    {
        IReadOnlyList<Core.Workflow.ProjectTask> tasks = _vm.Tasks();
        if (tasks.Count == 0)
        {
            _vm.Log("Tasks need a Lake project (a folder with a lakefile).");
            return Task.CompletedTask;
        }
        return Picker.ShowAsync(this, "Run a task", (q, _) => Task.FromResult<IReadOnlyList<PickerItem>>(
            Core.Editing.Fuzzy.Filter(tasks, q, t => t.Title).Select(t => new PickerItem(t.Title, t.Detail, () => _vm.RunTaskAsync(t))).ToList()));
    }

    private async Task RunShellAsync()
    {
        if (_vm.Project is null)
        {
            return;
        }
        string? command = await PromptAsync("Run a shell command", $"In {_vm.Project.Root}:", _vm.Settings.LastShellCommand ?? "");
        if (!string.IsNullOrWhiteSpace(command))
        {
            _vm.Settings.LastShellCommand = command;
            await _vm.RunTaskAsync(Core.Workflow.ProjectTasks.Shell(command));
        }
    }

    private Task LocalHistoryAsync()
    {
        IReadOnlyList<Core.Workflow.HistoryEntry> versions = _vm.VersionsOfActive();
        if (versions.Count == 0)
        {
            _vm.Log("No saved versions of this file yet: each save keeps one.");
            return Task.CompletedTask;
        }
        return Picker.ShowAsync(this, "Bring back a saved version (undo with ⌘Z / Ctrl+Z)", (q, _) => Task.FromResult<IReadOnlyList<PickerItem>>(
            versions.Where(v => v.Label.Contains(q, StringComparison.OrdinalIgnoreCase))
                .Select(v => new PickerItem(v.Label, $"{new FileInfo(v.SnapshotFile).Length:N0} bytes", () => { _vm.RestoreVersion(v); return Task.CompletedTask; }))
                .ToList()));
    }
    private void OnQuickOpen(object? sender, RoutedEventArgs e) => _ = QuickOpenAsync();
    private void OnGoToSymbol(object? sender, RoutedEventArgs e) => _ = GoToSymbolAsync();
    private void OnQuickFix(object? sender, RoutedEventArgs e) => _ = QuickFixAsync();
    private void OnFindInFiles(object? sender, RoutedEventArgs e) => ShowFindInFiles();
    private void OnShowGit(object? sender, RoutedEventArgs e) => _vm.SidebarTab = MainViewModel.GitTab;
    private void OnShowC(object? sender, RoutedEventArgs e) => _vm.RightTab = MainViewModel.CodeTab;

    private async void OnRenameModule(object? sender, RoutedEventArgs e)
    {
        if (_vm.Project is null || _vm.ActiveDocument is not { IsLean: true } d)
        {
            return;
        }
        string current = Core.Workflow.Refactor.ModuleOf(_vm.Project.Root, d.Path);
        string? name = await PromptAsync("Rename module", $"New name for {current} (imports of it are updated):", current);
        if (!string.IsNullOrWhiteSpace(name) && name.Trim() != current && await _vm.RenameModuleAsync(name) is string problem)
        {
            await Dialogs.InfoAsync(this, "Rename module", problem);
        }
    }

    private async void OnReplaceAll(object? sender, RoutedEventArgs e) =>
        await _vm.ReplaceInFilesAsync((matches, files) =>
            ConfirmAsync("Replace in files", $"Replace {matches} match{(matches == 1 ? "" : "es")} of \"{_vm.SearchQuery}\" with \"{_vm.ReplaceWith}\" in {files} file{(files == 1 ? "" : "s")}?\n\nOpen files are changed in the editor, unsaved. Other files are written; File ▸ Local History keeps their previous version."));

    /// <summary>The lightbulb: Lean's fixes for one line, and "Add import" for a name it does not know, as a menu.</summary>
    private async Task QuickFixAtLineAsync(int line)
    {
        IReadOnlyList<Lsp.CodeAction> actions = await _vm.CodeActionsAtLineAsync(line);
        var items = actions.Select(a => new PickerItem(a.Title, a.Kind, () => _vm.ApplyCodeActionAsync(a))).ToList();
        foreach (Core.Workflow.ImportSuggestion s in await _vm.ImportSuggestionsAsync(line))
        {
            items.Add(new PickerItem($"Add import {s.Module}", $"defines {s.Name}", () => { _vm.AddImport(s.Module); return Task.CompletedTask; }));
        }
        if (items.Count == 0)
        {
            _vm.Log("No fixes found for that line.");
            return;
        }
        await Picker.ShowAsync(this, $"Fixes for line {line + 1}", (q, _) => Task.FromResult<IReadOnlyList<PickerItem>>(
            Core.Editing.Fuzzy.Filter(items, q, i => i.Title).ToList()));
    }

    // ---- files: the explorer's context menu, and dropping files on the window ----

    private FileNode? SelectedNode => this.FindControl<TreeView>("FileTree")?.SelectedItem as FileNode;

    /// <summary>The folder a new file goes in: the selected folder, the selected file's folder, or the project root.</summary>
    private string? TargetFolder => SelectedNode is { } n ? (n.IsDirectory ? n.Path : Path.GetDirectoryName(n.Path)) : _vm.Project?.Root;

    private async void OnTreeNewFile(object? sender, RoutedEventArgs e)
    {
        if (TargetFolder is not string folder)
        {
            return;
        }
        string? name = await PromptAsync("New file", $"In {Path.GetFileName(folder)}:", "NewFile.lean");
        if (!string.IsNullOrWhiteSpace(name) && await _vm.CreateFileAsync(folder, name.Trim()) is string problem)
        {
            await Dialogs.InfoAsync(this, "New file", problem);
        }
    }

    private async void OnTreeNewFolder(object? sender, RoutedEventArgs e)
    {
        if (TargetFolder is not string folder)
        {
            return;
        }
        string? name = await PromptAsync("New folder", $"In {Path.GetFileName(folder)}:", "NewFolder");
        if (!string.IsNullOrWhiteSpace(name) && _vm.CreateFolder(folder, name.Trim()) is string problem)
        {
            await Dialogs.InfoAsync(this, "New folder", problem);
        }
    }

    private async void OnTreeRename(object? sender, RoutedEventArgs e)
    {
        if (SelectedNode is not { } n)
        {
            return;
        }
        string? name = await PromptAsync("Rename", n.Path.EndsWith(".lean", StringComparison.Ordinal) ? "New name (imports of this module are updated):" : "New name:", n.Name);
        if (!string.IsNullOrWhiteSpace(name) && name.Trim() != n.Name && await _vm.RenamePathAsync(n.Path, name.Trim()) is string problem)
        {
            await Dialogs.InfoAsync(this, "Rename", problem);
        }
    }

    private async void OnTreeTrash(object? sender, RoutedEventArgs e)
    {
        if (SelectedNode is not { } n || !await ConfirmAsync("Move to Trash", $"Move {n.Name} to the trash?"))
        {
            return;
        }
        if (await _vm.TrashAsync(n.Path) is string problem)
        {
            await Dialogs.InfoAsync(this, "Move to Trash", problem);
        }
    }

    private async void OnTreeReveal(object? sender, RoutedEventArgs e)
    {
        if (SelectedNode is { } n)
        {
            await RevealAsync(n.Path);
        }
    }

    private void OnTreeTerminal(object? sender, RoutedEventArgs e)
    {
        if (TargetFolder is string folder && !Core.Workflow.FileOps.OpenTerminal(folder))
        {
            _vm.Log("Could not find a terminal to open.");
        }
    }

    private async void OnTreeCopyPath(object? sender, RoutedEventArgs e)
    {
        if (SelectedNode is { } n && Clipboard is { } cb)
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetValueAsync(cb, Avalonia.Input.DataFormat.Text, n.Path);
        }
    }

    private async void OnTreeCopyRelative(object? sender, RoutedEventArgs e)
    {
        if (SelectedNode is { } n && _vm.Project is { } p && Clipboard is { } cb)
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetValueAsync(cb, Avalonia.Input.DataFormat.Text, Path.GetRelativePath(p.Root, n.Path));
        }
    }

    /// <summary>Drop a folder to open it as the project, or files to open them.</summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        IStorageItem[]? items = e.DataTransfer.TryGetFiles();
        foreach (IStorageItem item in items ?? [])
        {
            if (item.TryGetLocalPath() is not string path)
            {
                continue;
            }
            if (Directory.Exists(path))
            {
                await _vm.OpenProjectAsync(path);
                BuildRecentMenu();
                return;
            }
            await _vm.OpenFileAsync(path);
        }
    }

    private async void OnPreferences(object? sender, RoutedEventArgs e) => await ShowPreferencesAsync();
    private void OnShowLearn(object? sender, RoutedEventArgs e) => _vm.SidebarTab = MainViewModel.LearnTab;
    private void OnInsertSnippet(object? sender, RoutedEventArgs e) => _ = InsertSnippetAsync();

    /// <summary>Pick a snippet and insert it at the caret, indented to match, with the caret where it belongs.</summary>
    private Task InsertSnippetAsync() =>
        Picker.ShowAsync(this, "Insert a snippet", (q, _) => Task.FromResult<IReadOnlyList<PickerItem>>(
            Core.Editing.Fuzzy.Filter(Core.Learn.Snippets.All, q, s => s.Name + " " + s.Description)
                .Select(s => new PickerItem(s.Name, s.Description, () => { EditorControl.InsertSnippet(s); return Task.CompletedTask; }))
                .ToList()));
    private void OnBranchClicked(object? sender, PointerPressedEventArgs e) => _vm.SidebarTab = MainViewModel.GitTab;

    private async void OnCommitAll(object? sender, RoutedEventArgs e)
    {
        _vm.SidebarTab = MainViewModel.GitTab;
        string? message = await PromptAsync("Commit", "Commit message:", _vm.SourceControl.CommitMessage);
        if (!string.IsNullOrWhiteSpace(message))
        {
            await _vm.SaveAllCommand.ExecuteAsync(null);
            _vm.SourceControl.CommitMessage = message;
            await _vm.SourceControl.CommitCommand.ExecuteAsync(null);
        }
    }

    private void ShowFindInFiles()
    {
        string selected = EditorControl.TextEditor.SelectedText;
        _vm.ShowSearch(selected.Contains('\n', StringComparison.Ordinal) ? null : selected);
        Dispatcher.UIThread.Post(() => this.FindControl<TextBox>("SearchBox")?.Focus(), DispatcherPriority.Background);
    }

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.SearchInFilesCommand.Execute(null);
        }
    }

    private void OnOutlineTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: OutlineItem item })
        {
            _vm.GoToOutlineCommand.Execute(item);
        }
    }

    private void OnLocationDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: LocationItem item })
        {
            _vm.OpenLocationCommand.Execute(item);
        }
    }

    /// <summary>Every command, by name, runnable from the palette.</summary>
    private IEnumerable<(string Title, string Keys, Func<Task> Run)> Commands()
    {
        string m = OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";
        Func<Task> Cmd(System.Windows.Input.ICommand c) => () => { if (c.CanExecute(null)) { c.Execute(null); } return Task.CompletedTask; };
        Func<Task> Act(Action a) => () => { a(); return Task.CompletedTask; };
        yield return ("File: New Project…", "", Cmd(_vm.NewProjectCommand));
        yield return ("File: New File…", m + "N", Cmd(_vm.NewFileCommand));
        yield return ("File: Open Folder…", "", Cmd(_vm.OpenFolderCommand));
        yield return ("File: Open File…", m + "O", Cmd(_vm.OpenFileDialogCommand));
        yield return ("File: Save", m + "S", Cmd(_vm.SaveCommand));
        yield return ("File: Save All", m + "⇧S", Cmd(_vm.SaveAllCommand));
        yield return ("File: Close File", m + "W", Cmd(_vm.CloseDocumentCommand));
        yield return ("Go: Go to File…", m + "P", QuickOpenAsync);
        yield return ("Go: Go to Symbol in Workspace…", m + "T", GoToSymbolAsync);
        yield return ("Go: Go to Line…", OperatingSystem.IsMacOS() ? "⌘L" : "Ctrl+G", Cmd(_vm.GoToLineCommand));
        yield return ("Go: Go to Definition", "F12", Cmd(_vm.GoToDefinitionCommand));
        yield return ("Go: Find References", "⇧F12", Cmd(_vm.FindReferencesCommand));
        yield return ("Go: Who Uses This (callers)", m + "⌥H", Cmd(_vm.ShowCallersCommand));
        yield return ("Go: What This Uses (callees)", "", Cmd(_vm.ShowCalleesCommand));
        yield return ("Go: Show Declaration in Library", m + "⇧D", Cmd(_vm.ShowDeclarationAtCaretCommand));
        yield return ("Edit: Find in Files…", m + "⇧F", Act(ShowFindInFiles));
        yield return ("Edit: Find…", m + "F", Act(() => EditorControl.TextEditor.SearchPanel.Open()));
        yield return ("Lean: Quick Fix / Try This…", m + ".", QuickFixAsync);
        yield return ("Lean: Rename Symbol…", "F2", Cmd(_vm.RenameSymbolCommand));
        yield return ("Lean: Imports and Imported By", "", Cmd(_vm.ShowImportGraphCommand));
        yield return ("Lean: Count Heartbeats in File", "", Cmd(_vm.CountHeartbeatsCommand));
        yield return ("Tenet: Check the Blueprint Against Lean", "", Cmd(_vm.CheckBlueprintCommand));
        yield return ("Lean: Update Mathlib (and see what broke)…", "", () => { OnUpdateMathlib(null, new RoutedEventArgs()); return Task.CompletedTask; });
        yield return ("Lean: Undo Last Dependency Update", "", Cmd(_vm.UndoDependencyUpdateCommand));
        yield return ("Lean: Instances of Class at Cursor…", "", InstancesOfClassAsync);
        yield return ("Lean: Lean's Processes (memory, stop a runaway file)…", "", LeanProcessesAsync);
        yield return ("Lean: Remove Unused Imports", "", Cmd(_vm.RemoveUnusedImportsCommand));
        yield return ("Lean: Lint File (the linters CI runs)", "", Cmd(_vm.LintFileCommand));
        yield return ("Lean: Import Every Module in the Library Root", "", Cmd(_vm.ImportAllModulesCommand));
        yield return ("Lean: Get Mathlib Cache for Open Files", "", Cmd(_vm.GetCacheForOpenFilesCommand));
        yield return ("Lean: Restart Server", m + "⇧R", Cmd(_vm.RestartServerCommand));
        yield return ("Lean: Refresh File Dependencies", "", Cmd(_vm.RefreshFileDependenciesCommand));
        yield return ("Lean: Build Project", m + "B", Cmd(_vm.BuildCommand));
        yield return ("Lean: Get Mathlib Cache", "", Cmd(_vm.GetMathlibCacheCommand));
        yield return ("Lean: Update Dependencies", "", Cmd(_vm.UpdateDependenciesCommand));
        yield return ("Lean: Clean Build", "", Cmd(_vm.CleanCommand));
        yield return ("Tenet: Verify Project", m + "⇧V", Cmd(_vm.VerifyCommand));
        yield return ("Lean: Run Task…", m + "⇧B", RunTaskAsync);
        yield return ("Lean: Restart File (rebuild its imports)", "", Cmd(_vm.RestartFileCommand));
        yield return ("Lean: Install Lean (elan and the latest stable Lean)", "", Cmd(_vm.InstallLeanCommand));
        yield return ("Go: Back", OperatingSystem.IsMacOS() ? "⌃-" : "Alt+←", Cmd(_vm.GoBackCommand));
        yield return ("Go: Forward", OperatingSystem.IsMacOS() ? "⌃⇧-" : "Alt+→", Cmd(_vm.GoForwardCommand));
        yield return ("Go: Next Problem", "F8", Cmd(_vm.NextProblemCommand));
        yield return ("Go: Previous Problem", "⇧F8", Cmd(_vm.PreviousProblemCommand));
        yield return ("File: New Window", m + "⇧N", Cmd(_vm.NewWindowCommand));
        yield return ("File: Preferences…", m + ",", Act(() => OnPreferences(null, new RoutedEventArgs())));
        yield return ("Lean: Fix All in File", m + "⌥.", Cmd(_vm.FixAllInFileCommand));
        yield return ("Lean: Toggle Applying Suggestions Automatically", "", Act(() => { _vm.Settings.AutoApplyFixes = !_vm.Settings.AutoApplyFixes; ApplySettings(); _vm.Log("Apply suggestions automatically: " + (_vm.Settings.AutoApplyFixes ? "on" : "off")); }));
        yield return ("Lean: Show Compiled C", "", Act(() => _vm.RightTab = MainViewModel.CodeTab));
        yield return ("Refactor: Rename Symbol…", "F2", Cmd(_vm.RenameSymbolCommand));
        yield return ("Refactor: Rename This Module…", "", Act(() => OnRenameModule(null, new RoutedEventArgs())));
        yield return ("Refactor: Replace in Files…", "", Act(() => { _vm.ShowSearch(null); }));
        yield return ("Lean: Run Shell Command…", "", RunShellAsync);
        yield return ("File: Local History…", "", LocalHistoryAsync);
        yield return ("File: Toggle Auto Save", "", Act(() => { _vm.Settings.AutoSave = !_vm.Settings.AutoSave; ApplySettings(); _vm.Log("Auto save " + (_vm.Settings.AutoSave ? "on" : "off")); }));
        yield return ("View: Toggle Sidebar", m + "⌥B", Act(ToggleSidebar));
        yield return ("View: Toggle Bottom Panel", m + "J", Act(TogglePanel));
        yield return ("View: Toggle Tactic State", m + "⌥I", Act(ToggleInfo));
        yield return ("View: Zen Mode", m + "⌥Z", Act(Zen));
        yield return ("View: Toggle Word Wrap", "⌥Z", Act(() => { _vm.Settings.WordWrap = !_vm.Settings.WordWrap; ApplySettings(); }));
        yield return ("View: Toggle Vim Mode", "", Act(() => { _vm.Settings.VimMode = !_vm.Settings.VimMode; ApplySettings(); _vm.Log("Vim mode " + (_vm.Settings.VimMode ? "on" : "off")); }));
        yield return ("View: Sorries & TODOs", "", Act(() => { _vm.BottomTab = MainViewModel.MarkersPanel; _ = _vm.RefreshMarkersAsync(); }));
        yield return ("Library: Search Mathlib with Loogle", "", Act(() => _vm.SidebarTab = MainViewModel.LibraryTab));
        yield return ("Tactic State: Pin the Current Goals", "", Cmd(_vm.Info.PinCommand));
        yield return ("Learn: Start the Lean Tutorial", "", Cmd(_vm.Learn.StartTutorialCommand));
        yield return ("Learn: Open the Playground", "", Cmd(_vm.Learn.OpenPlaygroundCommand));
        yield return ("Learn: Famous Theorems and Symbols", "", Act(() => _vm.SidebarTab = MainViewModel.LearnTab));
        yield return ("Learn: Insert a Snippet…", "", InsertSnippetAsync);
        yield return ("Run: Run This File's main", "", Cmd(_vm.RunProgramCommand));
        yield return ("Help: Check for Updates…", "", Cmd(_vm.CheckForUpdatesNowCommand));
        yield return ("Git: Show Source Control", "", Act(() => _vm.SidebarTab = MainViewModel.GitTab));
        yield return ("Git: Commit All…", "", Act(() => OnCommitAll(null, new RoutedEventArgs())));
        yield return ("Git: Push", "", Cmd(_vm.SourceControl.PushCommand));
        yield return ("Git: Pull", "", Cmd(_vm.SourceControl.PullCommand));
        yield return ("Git: Sync", "", Cmd(_vm.SourceControl.SyncCommand));
        yield return ("Git: New Branch…", "", Cmd(_vm.SourceControl.NewBranchCommand));
        yield return ("Git: Clone Repository…", "", Cmd(_vm.CloneRepositoryCommand));
        yield return ("GitHub: Publish to GitHub…", "", Cmd(_vm.SourceControl.PublishToGitHubCommand));
        yield return ("GitHub: Create Pull Request…", "", Cmd(_vm.SourceControl.CreatePullRequestCommand));
        yield return ("GitHub: Open on GitHub", "", Cmd(_vm.SourceControl.OpenOnGitHubCommand));
        yield return ("GitHub: Add Lean CI Workflow", "", Cmd(_vm.SourceControl.AddCiWorkflowCommand));
        yield return ("AI: Ask AI to Prove This Sorry (Lean checks every suggestion)", m + "⌥A", Cmd(_vm.AskAiToProveCommand));
        yield return ("AI: Explain This (the error or goal at the cursor)", "", Cmd(_vm.ExplainWithAiCommand));
        yield return ("AI: Ask AI…", m + "⌥K", Cmd(_vm.AskAiCommand));
        yield return ("AI: Choose a Model (local or cloud)…", "", () => AiDialogs.ChooseModelAsync(this, _vm));
        yield return ("AI: Connect an AI Assistant…", "", Act(() => OnConnectAssistant(null, new RoutedEventArgs())));
        yield return ("View: Dark Theme", "", Act(() => SetTheme("Dark")));
        yield return ("View: Light Theme", "", Act(() => SetTheme("Light")));
        yield return ("View: Outline", "", Act(() => _vm.SidebarTab = MainViewModel.OutlineTab));
        yield return ("View: Library (declarations)", "", Act(() => _vm.SidebarTab = MainViewModel.LibraryTab));
        yield return ("View: Toolchains", "", Act(() => _vm.SidebarTab = MainViewModel.ToolchainsTab));
        yield return ("Lean: Prove It (try tactics on this sorry)", m + "⌥P", Cmd(_vm.ProveItCommand));
        yield return ("Lean: Prove Every Sorry in File", "", Cmd(_vm.ProveAllSorriesCommand));
        yield return ("Refactor: Extract Goal as Lemma…", "", Cmd(_vm.ExtractLemmaCommand));
        yield return ("FFI: New C Binding…", "", Cmd(_vm.NewFfiBindingCommand));
        yield return ("FFI: Write C Stub for This Extern", "", Cmd(_vm.WriteCStubCommand));
        yield return ("FFI: Check C Bindings", "", Act(() => OnCheckFfi(null, new RoutedEventArgs())));
        yield return ("Lean: Profile File (where the time goes)", "", Cmd(_vm.ProfileFileCommand));
        yield return ("Tenet: Why Isn't This Proved?", "", Cmd(_vm.WhyNotProvedAtCaretCommand));
        yield return ("Tenet: Project Map…", "", Cmd(_vm.ShowProjectMapCommand));
        yield return ("File: Export Proof Walkthrough…", "", Cmd(_vm.ExportWalkthroughCommand));
        yield return ("Share: Open in the Lean 4 Web Editor", "", Cmd(_vm.OpenInWebEditorCommand));
        yield return ("Share: Copy Share Link", "", Cmd(_vm.CopyShareLinkCommand));
        yield return ("Library: Ask Mathlib in Plain English (LeanSearch)", "", Act(() => _vm.SidebarTab = MainViewModel.LibraryTab));
        yield return ("View: Timing", "", Act(() => _vm.BottomTab = MainViewModel.TimingPanel));
        yield return ("Project: Edit This Project's Commands (commands.json)", "", Cmd(_vm.EditProjectCommandsCommand));
        foreach (Core.Workflow.ProjectCommand pc in _vm.ProjectCommandList())
        {
            yield return ("Project: " + pc.Title, "", () => _vm.RunProjectCommandAsync(pc));
        }
        foreach ((string title, Func<Task> run) in _vm.PluginHost.Commands)
        {
            yield return (title, "", run);
        }
        yield return ("Remote: Open a Project on Another Machine (SSH)…", "", OpenRemoteProjectAsync);
        yield return ("Remote: Run This Project's Lean Here Again", "", Cmd(_vm.ForgetRemoteCommand));
        yield return ("Plugins: Open the Plugins Folder", "", OpenPluginsFolderAsync);
        yield return ("Plugins: List Loaded Plugins", "", Act(ListPlugins));
        yield return ("Preferences: Keyboard Shortcuts File (keybindings.json)", "", EditKeybindingsAsync);
        yield return ("Preferences: Unicode Abbreviations File (abbreviations.json)", "", EditAbbreviationsAsync);
        yield return ("View: Toggle Emacs Keys", "", Act(() => { _vm.Settings.EmacsMode = !_vm.Settings.EmacsMode; ApplySettings(); _vm.Log("Emacs keys: " + (_vm.Settings.EmacsMode ? "on" : "off")); }));
        yield return ("View: Split Editor", m + "\\", Cmd(_vm.SplitEditorCommand));
        yield return ("View: Close Split", "", Cmd(_vm.CloseSplitCommand));
        yield return ("View: Lean Infoview (ProofWidgets and other widgets)", "", Cmd(_vm.ShowInfoviewCommand));
        yield return ("View: Lean Infoview in Browser", "", Cmd(_vm.OpenInfoviewCommand));
        yield return ("View: REPL (evaluate Lean at the cursor)", "", Act(() => { _vm.BottomTab = MainViewModel.ReplPanel; this.FindControl<TextBox>("ReplBox")?.Focus(); }));
        yield return ("Help: Keyboard Shortcuts", "", Act(() => OnShortcuts(null, new RoutedEventArgs())));
        yield return ("Help: About Lean Studio", "", Act(() => OnAbout(null, new RoutedEventArgs())));
    }

    private Task CommandPaletteAsync()
    {
        var all = Commands().ToList();
        return Picker.ShowAsync(this, "Type a command", (q, _) => Task.FromResult<IReadOnlyList<PickerItem>>(
            Core.Editing.Fuzzy.Filter(all, q, c => c.Title).Select(c => new PickerItem(c.Title, c.Keys, c.Run)).ToList()));
    }

    private Task QuickOpenAsync()
    {
        IReadOnlyList<string> files = _vm.ProjectFiles();
        string root = _vm.Project?.Root ?? "";
        return Picker.ShowAsync(this, "Go to file", (q, _) => Task.FromResult<IReadOnlyList<PickerItem>>(
            Core.Editing.Fuzzy.Filter(files, q, f => f).Take(200)
                .Select(f => new PickerItem(Path.GetFileName(f), Path.GetDirectoryName(f), async () => await _vm.OpenFileAsync(Path.Combine(root, f))))
                .ToList()));
    }

    private Task GoToSymbolAsync() =>
        Picker.ShowAsync(this, "Go to symbol in workspace (Lean's index: project, dependencies, core)", async (q, ct) =>
        {
            await Task.Delay(150, ct);
            IReadOnlyList<Lsp.SymbolLocation> symbols = await _vm.WorkspaceSymbolsAsync(q, ct);
            return symbols.Take(200).Select(s => new PickerItem(s.Name, Path.GetFileName(Lsp.LeanServer.PathOf(s.Location.Uri)) + ":" + (s.Location.Range.Start.Line + 1),
                async () => await _vm.OpenFileAsync(Lsp.LeanServer.PathOf(s.Location.Uri), s.Location.Range.Start.Line, s.Location.Range.Start.Character))).ToList();
        });

    /// <summary>Lean's code actions at the cursor (its "Try this" suggestions and quick fixes), as a menu.</summary>
    private async Task QuickFixAsync()
    {
        IReadOnlyList<Lsp.CodeAction> actions = await _vm.CodeActionsAtCaretAsync();
        if (actions.Count == 0)
        {
            _vm.Log("No suggestions at the cursor. Try `exact?`, `apply?` or `simp?` there, and Lean will offer some.");
            return;
        }
        if (actions.Count == 1)
        {
            await _vm.ApplyCodeActionAsync(actions[0]);
            return;
        }
        await Picker.ShowAsync(this, "Apply a suggestion", (q, _) => Task.FromResult<IReadOnlyList<PickerItem>>(
            Core.Editing.Fuzzy.Filter(actions, q, a => a.Title).Select(a => new PickerItem(a.Title, a.Kind, () => _vm.ApplyCodeActionAsync(a))).ToList()));
    }

    private AiChatWindow? _aiWindow;

    /// <summary>The AI window, while one is open.</summary>
    public AiChatWindow? AiWindow => _aiWindow;

    /// <summary>Show the AI window (one per main window), and ask <paramref name="question"/> in it if there is one.</summary>
    public async Task<AiChatWindow> ShowAiChatAsync(string? question)
    {
        if (_aiWindow is null)
        {
            _aiWindow = new AiChatWindow(_vm, () => AiDialogs.ChooseModelAsync(this, _vm));
            _aiWindow.Closed += (_, _) => _aiWindow = null;
            _aiWindow.Show(this);
        }
        else
        {
            _aiWindow.Activate();
        }
        if (question is not null)
        {
            await _aiWindow.AskAsync(question);
        }
        return _aiWindow;
    }

    private async void OnChooseAiModel(object? sender, RoutedEventArgs e) => await AiDialogs.ChooseModelAsync(this, _vm);

    private void OnSaveSettings(object? sender, RoutedEventArgs e) => _vm.Settings.Save();

    private async void OnConnectAssistant(object? sender, RoutedEventArgs e) =>
        await Dialogs.ConnectAssistantAsync(this, AgentSetup.ForCurrentProcess(), _vm.Log);

    // ---- IDialogs ----

    /// <inheritdoc/>
    public async Task<string?> PickFolderAsync(string title)
    {
        IReadOnlyList<IStorageFolder> r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return r.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <inheritdoc/>
    public async Task<string?> PickFileAsync(string title)
    {
        IReadOnlyList<IStorageFile> r = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Lean") { Patterns = ["*.lean"] },
                new FilePickerFileType("All files") { Patterns = ["*"] },
            ],
        });
        return r.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <inheritdoc/>
    public async Task<string?> SaveFileAsync(string title, string suggestedName, string? folder)
    {
        IStorageFolder? start = folder is null ? null : await StorageProvider.TryGetFolderFromPathAsync(folder);
        IStorageFile? f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = start,
            DefaultExtension = "lean",
            FileTypeChoices = [new FilePickerFileType("Lean") { Patterns = ["*.lean"] }],
        });
        return f?.TryGetLocalPath();
    }

    /// <inheritdoc/>
    public async Task<string?> SaveWebPageAsync(string title, string suggestedName, string? folder)
    {
        IStorageFolder? start = folder is null ? null : await StorageProvider.TryGetFolderFromPathAsync(folder);
        IStorageFile? f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = start,
            DefaultExtension = "html",
            FileTypeChoices = [new FilePickerFileType("Web page") { Patterns = ["*.html"] }],
        });
        return f?.TryGetLocalPath();
    }

    /// <inheritdoc/>
    public async Task CopyTextAsync(string text)
    {
        if (Clipboard is { } cb)
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetValueAsync(cb, Avalonia.Input.DataFormat.Text, text);
        }
    }

    /// <inheritdoc/>
    public Task<bool> ConfirmAsync(string title, string message) => Dialogs.ConfirmAsync(this, title, message);

    /// <inheritdoc/>
    public Task<string?> PromptAsync(string title, string message, string initial) => Dialogs.PromptAsync(this, title, message, initial);

    /// <inheritdoc/>
    public async Task LaunchAsync(Uri uri) => await Launcher.LaunchUriAsync(uri);

    private async Task OpenRemoteProjectAsync()
    {
        string? destination = await Dialogs.PromptAsync(this, "Open a Project on Another Machine",
            "Where is the project? Write it as user@host:/path/to/project. Lean, Lake and elan run there over SSH (it must log in without a password); you edit the files through a folder where it is mounted here, such as an sshfs mount or a network drive, which you choose next.",
            "");
        if (string.IsNullOrWhiteSpace(destination))
        {
            return;
        }
        if (await PickFolderAsync("Where is " + destination.Trim() + " mounted here?") is string local)
        {
            await _vm.OpenRemoteProjectAsync(destination, local);
        }
    }

    /// <summary>The long task's progress on the app's Dock or taskbar icon.</summary>
    public TaskbarProgress Taskbar { get; private set; } = null!;

    private async Task OpenPluginsFolderAsync()
    {
        Directory.CreateDirectory(PluginHost.Folder);
        _vm.Log("Plugins load from " + PluginHost.Folder + " when Lean Studio starts: Name.dll, or Name/Name.dll with its dependencies.");
        await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(PluginHost.Folder));
    }

    private void ListPlugins()
    {
        _vm.BottomTab = MainViewModel.OutputPanel;
        if (_vm.PluginHost.Plugins.Count == 0)
        {
            _vm.Log("No plugins are loaded. Put them in " + PluginHost.Folder + " and restart Lean Studio.");
            return;
        }
        foreach (Core.Plugins.LoadedPlugin p in _vm.PluginHost.Plugins)
        {
            _vm.Log($"Plugin: {p.Plugin.Name} ({p.Path})");
        }
    }

    /// <summary>Show a file selected in Finder or Explorer (by starting <c>open -R</c> or <c>explorer.exe</c>); elsewhere open its folder.</summary>
    public async Task RevealAsync(string path)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("open", ["-R", path])?.Dispose();
                return;
            }
            if (OperatingSystem.IsWindows())
            {
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"")?.Dispose();
                return;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(Path.GetDirectoryName(path)!));
    }

    /// <inheritdoc/>
    public Task<NewProjectRequest?> NewProjectAsync(IReadOnlyList<string> toolchains, string defaultParent) =>
        Dialogs.NewProjectAsync(this, toolchains, defaultParent, () => PickFolderAsync("Where to create the project"));

    // ---- menu handlers ----

    private void OnExit(object? sender, RoutedEventArgs e) => Close();

    private void OnUndo(object? sender, RoutedEventArgs e) => EditorControl.TextEditor.Undo();

    private void OnRedo(object? sender, RoutedEventArgs e) => EditorControl.TextEditor.Redo();

    private void OnFind(object? sender, RoutedEventArgs e) => EditorControl.TextEditor.SearchPanel.Open();

    private void OnReplace(object? sender, RoutedEventArgs e)
    {
        EditorControl.TextEditor.SearchPanel.IsReplaceMode = true;
        EditorControl.TextEditor.SearchPanel.Open();
    }

    private void OnDarkTheme(object? sender, RoutedEventArgs e) => SetTheme("Dark");

    private void OnLightTheme(object? sender, RoutedEventArgs e) => SetTheme("Light");

    /// <summary>Switch to the <c>Dark</c> or <c>Light</c> theme, apply it to the editor and save the settings.</summary>
    public void SetTheme(string theme)
    {
        _vm.Settings.Theme = theme;
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        }
        ApplySettings();
        _vm.InfoviewThemeChanged();
    }

    private void OnFontBigger(object? sender, RoutedEventArgs e)
    {
        _vm.Settings.EditorFontSize = Math.Min(32, _vm.Settings.EditorFontSize + 1);
        ApplySettings();
    }

    private void OnFontSmaller(object? sender, RoutedEventArgs e)
    {
        _vm.Settings.EditorFontSize = Math.Max(8, _vm.Settings.EditorFontSize - 1);
        ApplySettings();
    }

    private void OnApplySettings(object? sender, RoutedEventArgs e) => ApplySettings();

    private void ApplySettings()
    {
        foreach (LeanEditor ed in Editors)
        {
            ed.ApplySettings(_vm.Settings);
        }
        _vm.Info.ExplainErrors = _vm.Settings.ExplainErrors;
        _vm.Settings.Save();
    }

    private void OnProblemsClicked(object? sender, PointerPressedEventArgs e) => _vm.BottomTab = 0;

    private void OnProblemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ProblemRow { IsGroup: false } p })
        {
            _vm.OpenProblemCommand.Execute(p.Item);
        }
    }

    // A group of repeated problems opens and folds with one click.
    private void OnProblemTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ProblemRow { IsGroup: true } g } list)
        {
            _vm.ToggleProblemGroup(g);
            list.SelectedItem = null;
        }
    }

    private void OnFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        // Single click opens files, as in most editors; folders expand.
        if (sender is TreeView { SelectedItem: FileNode { IsDirectory: false } f })
        {
            _ = _vm.OpenFileAsync(f.Path);
        }
    }

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is TreeView { SelectedItem: FileNode { IsDirectory: true } d })
        {
            d.IsExpanded = !d.IsExpanded;
        }
    }

    private async void OnShortcuts(object? sender, RoutedEventArgs e)
    {
        string mod = OperatingSystem.IsMacOS() ? "⌘" : "Ctrl+";
        await Dialogs.InfoAsync(this, "Keyboard Shortcuts", string.Join('\n',
            $"{mod}S  save          {mod}⇧S  save all",
            $"{mod}O  open file     {mod}N  new file     {mod}W  close file",
            $"{mod}B  build         {mod}⇧V  verify with Tenet",
            $"{mod}⇧R  restart Lean",
            $"{mod}⌥P  Prove It: try a portfolio of tactics on the sorry at the cursor",
            $"F12 or {mod}click  go to definition",
            $"{mod}⇧D  show the declaration under the cursor in the navigator",
            "Ctrl+Space  completion",
            $"{mod}/  comment or uncomment lines",
            $"{mod}F  find         {(OperatingSystem.IsMacOS() ? "⌘L" : "Ctrl+G")}  go to line",
            "\\name  Unicode input: \\alpha α, \\to →, \\N ℕ, \\forall ∀, \\<> ⟨⟩ (Tab completes)"), 520);
    }

    private async void OnAuthor(object? sender, RoutedEventArgs e) => await Launcher.LaunchUriAsync(new Uri(Credits.XUrl));

    private async void OnAbout(object? sender, RoutedEventArgs e) => await ShowAboutAsync();

    /// <summary>Show the About box.</summary>
    public Task ShowAboutAsync() => Dialogs.AboutAsync(this, _vm.Server?.Command.ToString() ?? "not running");

    /// <summary>Show how to connect AI assistants to Lean Studio's MCP server, with one-click setup where possible.</summary>
    public Task ShowConnectAssistantAsync() => Dialogs.ConnectAssistantAsync(this, AgentSetup.ForCurrentProcess(), _vm.Log);

    /// <summary>Show the Preferences dialog; if OK is pressed, the settings are saved and applied to the window.</summary>
    public async Task ShowPreferencesAsync()
    {
        await Dialogs.PreferencesAsync(this, _vm.Settings);
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = _vm.Settings.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        }
        ApplySettings();
    }
}
