using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Text.Json.Nodes;
using LeanStudio.Core.Agents;
using LeanStudio.App.Editor;
using LeanStudio.App.Services;
using LeanStudio.App.ViewModels;

namespace LeanStudio.App.Views;

public sealed partial class MainWindow : Window, IDialogs
{
    private readonly MainViewModel _vm;
    private readonly CancellationTokenSource _bridgeCts = new();
    private bool _closing;

    public MainWindow()
        : this(Settings.Load())
    {
    }

    public MainWindow(Settings settings)
    {
        AvaloniaXamlLoader.Load(this);
        _vm = new MainViewModel(this, settings);
        DataContext = _vm;
        this.FindControl<OutputView>("OutputView")!.DataContext = _vm;
        EditorControl.ApplySettings(settings);
        _vm.SelectionProvider = () => EditorControl.TextEditor.SelectedText;
        Opened += async (_, _) =>
        {
            BuildRecentMenu();
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
                await _vm.StartAsync();
            }
            BuildRecentMenu();
        };
        Closing += OnClosing;
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>A folder or file given on the command line, opened instead of the last session.</summary>
    public string? OpenOnStartup { get; set; }

    public MainViewModel ViewModel => _vm;

    private LeanEditor EditorControl => this.FindControl<LeanEditor>("Editor")!;

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
            var item = new MenuItem { Header = p };
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

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        bool cmd = Cmd(e), shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        Action? action = (e.Key, cmd, shift) switch
        {
            (Key.P, true, true) => () => _ = CommandPaletteAsync(),
            (Key.P, true, false) => () => _ = QuickOpenAsync(),
            (Key.T, true, false) => () => _ = GoToSymbolAsync(),
            (Key.F, true, true) => () => ShowFindInFiles(),
            (Key.OemPeriod, true, false) => () => _ = QuickFixAsync(),
            _ => null,
        };
        if (action is not null)
        {
            e.Handled = true;
            action();
        }
    }

    private void OnCommandPalette(object? sender, RoutedEventArgs e) => _ = CommandPaletteAsync();
    private void OnQuickOpen(object? sender, RoutedEventArgs e) => _ = QuickOpenAsync();
    private void OnGoToSymbol(object? sender, RoutedEventArgs e) => _ = GoToSymbolAsync();
    private void OnQuickFix(object? sender, RoutedEventArgs e) => _ = QuickFixAsync();
    private void OnFindInFiles(object? sender, RoutedEventArgs e) => ShowFindInFiles();
    private void OnShowGit(object? sender, RoutedEventArgs e) => _vm.SidebarTab = MainViewModel.GitTab;
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
        yield return ("Go: Show Declaration in Library", m + "⇧D", Cmd(_vm.ShowDeclarationAtCaretCommand));
        yield return ("Edit: Find in Files…", m + "⇧F", Act(ShowFindInFiles));
        yield return ("Edit: Find…", m + "F", Act(() => EditorControl.TextEditor.SearchPanel.Open()));
        yield return ("Lean: Quick Fix / Try This…", m + ".", QuickFixAsync);
        yield return ("Lean: Rename Symbol…", "F2", Cmd(_vm.RenameSymbolCommand));
        yield return ("Lean: Restart Server", m + "⇧R", Cmd(_vm.RestartServerCommand));
        yield return ("Lean: Refresh File Dependencies", "", Cmd(_vm.RefreshFileDependenciesCommand));
        yield return ("Lean: Build Project", m + "B", Cmd(_vm.BuildCommand));
        yield return ("Lean: Get Mathlib Cache", "", Cmd(_vm.GetMathlibCacheCommand));
        yield return ("Lean: Update Dependencies", "", Cmd(_vm.UpdateDependenciesCommand));
        yield return ("Lean: Clean Build", "", Cmd(_vm.CleanCommand));
        yield return ("Tenet: Verify Project", m + "⇧V", Cmd(_vm.VerifyCommand));
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
        yield return ("AI: Connect an AI Assistant…", "", Act(() => OnConnectAssistant(null, new RoutedEventArgs())));
        yield return ("View: Dark Theme", "", Act(() => SetTheme("Dark")));
        yield return ("View: Light Theme", "", Act(() => SetTheme("Light")));
        yield return ("View: Outline", "", Act(() => _vm.SidebarTab = MainViewModel.OutlineTab));
        yield return ("View: Library (declarations)", "", Act(() => _vm.SidebarTab = MainViewModel.LibraryTab));
        yield return ("View: Toolchains", "", Act(() => _vm.SidebarTab = MainViewModel.ToolchainsTab));
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

    private async void OnConnectAssistant(object? sender, RoutedEventArgs e) =>
        await Dialogs.ConnectAssistantAsync(this, AgentSetup.ForCurrentProcess(), _vm.Log);

    // ---- IDialogs ----

    public async Task<string?> PickFolderAsync(string title)
    {
        IReadOnlyList<IStorageFolder> r = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return r.FirstOrDefault()?.TryGetLocalPath();
    }

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

    public Task<bool> ConfirmAsync(string title, string message) => Dialogs.ConfirmAsync(this, title, message);

    public Task<string?> PromptAsync(string title, string message, string initial) => Dialogs.PromptAsync(this, title, message, initial);

    public async Task LaunchAsync(Uri uri) => await Launcher.LaunchUriAsync(uri);

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

    private void SetTheme(string theme)
    {
        _vm.Settings.Theme = theme;
        if (Avalonia.Application.Current is { } app)
        {
            app.RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        }
        ApplySettings();
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
        EditorControl.ApplySettings(_vm.Settings);
        _vm.Settings.Save();
    }

    private void OnProblemsClicked(object? sender, PointerPressedEventArgs e) => _vm.BottomTab = 0;

    private void OnProblemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ProblemItem p })
        {
            _vm.OpenProblemCommand.Execute(p);
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
            $"F12 or {mod}click  go to definition",
            $"{mod}⇧D  show the declaration under the cursor in the navigator",
            "Ctrl+Space  completion",
            $"{mod}/  comment or uncomment lines",
            $"{mod}F  find         {(OperatingSystem.IsMacOS() ? "⌘L" : "Ctrl+G")}  go to line",
            "\\name  Unicode input: \\alpha α, \\to →, \\N ℕ, \\forall ∀, \\<> ⟨⟩ (Tab completes)"), 520);
    }

    private async void OnAuthor(object? sender, RoutedEventArgs e) => await Launcher.LaunchUriAsync(new Uri(Credits.XUrl));

    private async void OnAbout(object? sender, RoutedEventArgs e) =>
        await Dialogs.AboutAsync(this, _vm.Server?.Command.ToString() ?? "not running");
}
