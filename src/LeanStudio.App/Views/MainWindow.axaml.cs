using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using LeanStudio.App.Editor;
using LeanStudio.App.Services;
using LeanStudio.App.ViewModels;

namespace LeanStudio.App.Views;

public sealed partial class MainWindow : Window, IDialogs
{
    private readonly MainViewModel _vm;
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
        Opened += async (_, _) =>
        {
            BuildRecentMenu();
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
