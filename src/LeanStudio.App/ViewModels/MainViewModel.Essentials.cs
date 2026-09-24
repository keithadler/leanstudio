using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Toolchains;
using LeanStudio.Core.Workflow;
using LeanStudio.Lsp;

namespace LeanStudio.App.ViewModels;

/// <summary>The things whose absence makes an editor hard to live in: getting Lean installed, moving around and
/// back, keeping imports fresh, adding a missing import, file management, recovering when Lean crashes.</summary>
public sealed partial class MainViewModel
{
    // ---- navigation history: back and forward across jumps ----

    private readonly Stack<(string Path, int Line, int Column)> _back = new();
    private readonly Stack<(string Path, int Line, int Column)> _forward = new();
    private bool _navigating;

    /// <summary>Remember where the cursor is before a jump (go to definition, a search result, a problem…).</summary>
    private void PushLocation()
    {
        if (_navigating || ActiveDocument is not { IsVirtual: false } d)
        {
            return;
        }
        var here = (d.Path, d.CaretLine, d.CaretColumn);
        if (_back.Count == 0 || _back.Peek() != here)
        {
            _back.Push(here);
        }
        _forward.Clear();
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    /// <summary>There is a place to go back to.</summary>
    public bool CanGoBack => _back.Count > 0;
    /// <summary>There is a place to go forward to (after going back).</summary>
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>
    /// Return to where the caret was before the last jump, skipping places in files that no longer exist.
    /// </summary>
    [RelayCommand]
    private async Task GoBackAsync() => await TravelAsync(_back, _forward);

    /// <summary>Undo a <see cref="GoBackCommand"/>.</summary>
    [RelayCommand]
    private async Task GoForwardAsync() => await TravelAsync(_forward, _back);

    private async Task TravelAsync(Stack<(string Path, int Line, int Column)> from, Stack<(string Path, int Line, int Column)> to)
    {
        while (from.Count > 0)
        {
            var target = from.Pop();
            if (!File.Exists(target.Path))
            {
                continue;
            }
            if (ActiveDocument is { IsVirtual: false } d)
            {
                to.Push((d.Path, d.CaretLine, d.CaretColumn));
            }
            _navigating = true;
            try
            {
                await OpenFileAsync(target.Path, target.Line, target.Column);
            }
            finally
            {
                _navigating = false;
            }
            break;
        }
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    // ---- next and previous problem ----

    /// <summary>Move the caret to the next error or warning in the active file, wrapping around.</summary>
    [RelayCommand]
    private void NextProblem() => StepProblem(+1);

    /// <summary>Move the caret to the previous error or warning in the active file, wrapping around.</summary>
    [RelayCommand]
    private void PreviousProblem() => StepProblem(-1);

    /// <summary>Move to the next (or previous) error or warning in the file, wrapping around.</summary>
    private void StepProblem(int direction)
    {
        if (ActiveDocument is not DocumentViewModel d)
        {
            return;
        }
        var problems = d.Diagnostics.Where(x => x.Severity <= DiagnosticSeverity.Warning).Select(x => x.Range.Start).Distinct().OrderBy(p => p).ToList();
        if (problems.Count == 0)
        {
            Log("No errors or warnings in this file.");
            return;
        }
        var here = new Position(d.CaretLine, d.CaretColumn);
        Position target = direction > 0
            ? problems.FirstOrDefault(p => p > here, problems[0])
            : problems.LastOrDefault(p => p < here, problems[^1]);
        d.Reveal(target.Line, target.Character);
    }

    // ---- stale imports: restart the file ----

    /// <summary>Lean says the active file's imports are out of date: offer <see cref="RestartFileAsync"/>.</summary>
    [ObservableProperty]
    private bool _importsStale;

    private void UpdateImportsStale() =>
        ImportsStale = ActiveDocument?.Diagnostics.Any(x => x.Message.StartsWith("Imports are out of date", StringComparison.Ordinal)) == true;

    /// <summary>
    /// Close and reopen the file in Lean, which rebuilds its imports first (what other editors call Restart File).
    /// Needed after changing a file that this one imports.
    /// </summary>
    /// <remarks>Saves every file first. Does nothing without an active Lean file and a running server.</remarks>
    [RelayCommand]
    public async Task RestartFileAsync()
    {
        if (ActiveDocument is not { IsLean: true } d || _server is not { State: LeanServerState.Running } s)
        {
            return;
        }
        await SaveAllCommand.ExecuteAsync(null);
        Log($"Restarting {Path.GetFileName(d.Path)}: rebuilding its imports, then checking it again.");
        await s.CloseAsync(d.Uri);
        d.Diagnostics = [];
        await s.OpenAsync(d.Uri, d.Document.Text);
        ImportsStale = false;
    }

    // ---- adding a missing import ----

    /// <summary>The name an "unknown identifier" (or "unknown constant") message on a line complains about.</summary>
    /// <param name="d">The document whose diagnostics to read.</param>
    /// <param name="line">The 0-based line.</param>
    /// <returns>The name, or null if no message on the line is about a missing name.</returns>
    public static string? MissingNameOn(DocumentViewModel d, int line) =>
        d.Diagnostics.Where(x => x.Extent.Start.Line <= line && line <= x.Extent.End.Line)
            .Select(x => ImportFinder.MissingName(x.Message)).FirstOrDefault(n => n is not null);

    /// <summary>For an "unknown identifier" on this line: the modules that define the name, from Loogle.</summary>
    /// <remarks>
    /// Asks Loogle over the network. Only modules the project can import are kept (core Lean's always; Mathlib's and
    /// its dependencies' only in a project that uses Mathlib). A network failure is logged and gives an empty list.
    /// </remarks>
    /// <param name="line">The 0-based line in the active file.</param>
    public async Task<IReadOnlyList<ImportSuggestion>> ImportSuggestionsAsync(int line)
    {
        if (ActiveDocument is not DocumentViewModel d || MissingNameOn(d, line) is not string name)
        {
            return [];
        }
        try
        {
            IReadOnlyList<ImportSuggestion> found = await ImportFinder.SuggestAsync(name);
            bool mathlib = Project?.DependsOnMathlib == true;
            return found.Where(s => ImportFinder.Available(s.Module, mathlib)).ToList();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log("Could not ask Loogle where the name is defined: " + e.Message);
            return [];
        }
    }

    /// <summary>Add an import to the active file, as one undoable edit.</summary>
    /// <remarks>Does nothing if the file already imports it. The file is left unsaved.</remarks>
    /// <param name="module">The module, such as <c>Mathlib.Data.Nat.Prime.Basic</c>.</param>
    public void AddImport(string module)
    {
        if (ActiveDocument is not DocumentViewModel d)
        {
            return;
        }
        string updated = ImportFinder.AddImport(d.Document.Text, module);
        if (updated != d.Document.Text)
        {
            d.ReplaceAll(updated);
            Log($"Added import {module}. Lean rebuilds the file's imports when it rechecks it.");
        }
    }

    // ---- Lean crashed: start it again ----

    private readonly Queue<DateTime> _restarts = new();

    /// <summary>Restart a crashed server, but not in a loop: at most three times in five minutes.</summary>
    private void OnServerCrashed()
    {
        while (_restarts.Count > 0 && DateTime.UtcNow - _restarts.Peek() > TimeSpan.FromMinutes(5))
        {
            _restarts.Dequeue();
        }
        if (_restarts.Count >= 3)
        {
            Log("Lean keeps stopping; not restarting it again automatically. Lean ▸ Restart Server when ready.");
            return;
        }
        _restarts.Enqueue(DateTime.UtcNow);
        Log("Lean stopped unexpectedly; starting it again.");
        DispatcherTimer.RunOnce(() => _ = RestartServerAsync(), TimeSpan.FromSeconds(2));
    }

    // ---- getting Lean installed ----

    /// <summary>elan is not installed, so Lean cannot run: the window offers to install it.</summary>
    [ObservableProperty]
    private bool _leanMissing;

    /// <summary>The Lean installer is running.</summary>
    [ObservableProperty]
    private bool _installingLean;

    /// <summary>Install elan and the latest stable Lean with the official installer, then start Lean.</summary>
    [RelayCommand]
    private async Task InstallLeanAsync()
    {
        if (InstallingLean)
        {
            return;
        }
        InstallingLean = true;
        BottomTab = OutputPanel;
        try
        {
            var r = await ElanInstaller.InstallAsync(Log);
            if (r.Success && Elan.IsInstalled)
            {
                Log("Lean is installed. Starting it.");
                LeanMissing = false;
                await Toolchains.RefreshAsync();
                await StartServerAsync();
            }
            else
            {
                Log($"The installer did not finish (exit {r.ExitCode}). The instructions at {Elan.InstallUrl} do the same by hand.");
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            Log("Could not install Lean: " + e.Message);
        }
        finally
        {
            InstallingLean = false;
        }
    }

    // ---- files ----

    /// <summary>Create an empty file and open it. <c>.lean</c> is added to a name with no extension.</summary>
    /// <param name="folder">The folder to create it in; created if needed.</param>
    /// <param name="name">The file's name, which may include subfolders.</param>
    /// <returns>What went wrong (the file already exists), or null.</returns>
    public async Task<string?> CreateFileAsync(string folder, string name)
    {
        string path = Path.Combine(folder, name.EndsWith(".lean", StringComparison.Ordinal) || name.Contains('.') ? name : name + ".lean");
        if (File.Exists(path) || Directory.Exists(path))
        {
            return $"{Path.GetFileName(path)} already exists.";
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "");
        RefreshFiles();
        await OpenFileAsync(path);
        await AddToLibraryRootAsync(path);
        return null;
    }

    /// <summary>Create a folder and refresh the file tree.</summary>
    /// <param name="parent">The folder to create it in.</param>
    /// <param name="name">The new folder's name.</param>
    /// <returns>What went wrong (it already exists), or null.</returns>
    public string? CreateFolder(string parent, string name)
    {
        string path = Path.Combine(parent, name);
        if (File.Exists(path) || Directory.Exists(path))
        {
            return $"{name} already exists.";
        }
        Directory.CreateDirectory(path);
        RefreshFiles();
        return null;
    }

    /// <summary>
    /// Rename a file or folder. A Lean file inside the project is renamed as a module, so the imports of it are
    /// rewritten too; an open file follows its new name.
    /// </summary>
    /// <remarks>Saves the file first if it has unsaved changes.</remarks>
    /// <param name="path">The file or folder to rename.</param>
    /// <param name="newName">Its new name, in the same folder.</param>
    /// <returns>What went wrong (the new name is taken), or null.</returns>
    public async Task<string?> RenamePathAsync(string path, string newName)
    {
        string target = Path.Combine(Path.GetDirectoryName(path)!, newName);
        if (File.Exists(target) || Directory.Exists(target))
        {
            return $"{newName} already exists.";
        }
        DocumentViewModel? open = Documents.FirstOrDefault(d => d.Path == path);
        if (open is { IsDirty: true })
        {
            await SaveDocumentAsync(open);
        }
        int changedImports = 0;
        if (Project is not null && File.Exists(path) && path.EndsWith(".lean", StringComparison.Ordinal) && target.EndsWith(".lean", StringComparison.Ordinal)
            && path.StartsWith(Project.Root, StringComparison.Ordinal))
        {
            changedImports = Refactor.RenameModule(Project.Root, path, target).Count;
        }
        else if (Directory.Exists(path))
        {
            Directory.Move(path, target);
        }
        else
        {
            File.Move(path, target);
        }
        if (open is not null)
        {
            var (line, col) = (open.CaretLine, open.CaretColumn);
            Documents.Remove(open);
            if (_server is { State: LeanServerState.Running } s && open.IsLean)
            {
                await s.CloseAsync(open.Uri);
            }
            await OpenFileAsync(target, line, col);
        }
        RefreshFiles();
        Log($"Renamed {Path.GetFileName(path)} to {newName}" + (changedImports > 0 ? $"; updated imports in {changedImports} file{(changedImports == 1 ? "" : "s")}." : "."));
        return null;
    }

    /// <summary>Move to the trash, closing it first if it is open. Returns a problem to show, or null.</summary>
    /// <remarks>
    /// Open files there (for a folder, every one inside it) are closed without asking about unsaved changes.
    /// </remarks>
    /// <param name="path">The file or folder.</param>
    public async Task<string?> TrashAsync(string path)
    {
        foreach (DocumentViewModel d in Documents.Where(d => d.Path == path || d.Path.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.Ordinal)).ToList())
        {
            Documents.Remove(d);
            if (_server is { State: LeanServerState.Running } s && d.IsLean)
            {
                await s.CloseAsync(d.Uri);
            }
        }
        if (ActiveDocument is not null && !Documents.Contains(ActiveDocument))
        {
            ActiveDocument = Documents.LastOrDefault();
        }
        await RemoveFromLibraryRootAsync(path);
        bool ok = await FileOps.MoveToTrashAsync(path);
        RefreshFiles();
        if (!ok)
        {
            return $"Could not move {Path.GetFileName(path)} to the trash.";
        }
        Log($"Moved {Path.GetFileName(path)} to the trash.");
        return null;
    }

    /// <summary>Start another Lean Studio window, empty, for a second project.</summary>
    [RelayCommand]
    private void NewWindow()
    {
        string exe = Environment.ProcessPath ?? "LeanStudio";
        string entry = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && entry.Length > 0)
        {
            psi.ArgumentList.Add(entry);
        }
        psi.ArgumentList.Add("--new-window");
        try
        {
            Process.Start(psi)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Log("Could not open a new window: " + e.Message);
        }
    }
}
