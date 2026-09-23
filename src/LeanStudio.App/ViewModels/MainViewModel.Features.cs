using Avalonia.Threading;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Editing;
using LeanStudio.Core.Git;
using LeanStudio.Lsp;
using Range = LeanStudio.Lsp.Range;

namespace LeanStudio.App.ViewModels;

public sealed record OutlineItem(string Name, string Kind, int Depth, Range Range, Range SelectionRange)
{
    public string Indent => new(' ', Depth * 3);
    public string Icon => Kind switch
    {
        "namespace" => "{}",
        "structure" or "class" => "◇",
        "inductive" => "◆",
        "theorem" => "⊢",
        _ => "ƒ",
    };
}

public sealed record LocationItem(string Path, int Line, int Column, string Preview, int MatchLength = 0)
{
    public string File => System.IO.Path.GetFileName(Path);
    public string Where => $"{Line + 1}:{Column + 1}";
    public string Text => Preview.Trim();
}

/// <summary>The editor features beyond editing: code actions, rename, references, outline, search, symbols, Git.</summary>
public sealed partial class MainViewModel
{
    public const int FilesTab = 0, OutlineTab = 1, LibraryTab = 2, GitTab = 3, ToolchainsTab = 4;
    public const int ProblemsPanel = 0, OutputPanel = 1, TenetPanel = 2, ReferencesPanel = 3, SearchPanel = 4;

    public ObservableList<OutlineItem> Outline { get; } = new();
    public ObservableList<LocationItem> References { get; } = new();
    public ObservableList<LocationItem> SearchResults { get; } = new();

    [ObservableProperty]
    private string _referencesTitle = "References";

    [ObservableProperty]
    private string _searchQuery = "";

    [ObservableProperty]
    private bool _searchCaseSensitive;

    [ObservableProperty]
    private bool _searchRegex;

    [ObservableProperty]
    private string _searchStatus = "";

    public SourceControlViewModel SourceControl { get; private set; } = null!;

    private CancellationTokenSource? _outlineCts;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _gitCts;

    private void InitFeatures()
    {
        SourceControl = new SourceControlViewModel(
            Log,
            async (path, line) => await OpenFileAsync(path, line),
            OpenDiffAsync,
            msg => _dialogs.ConfirmAsync("Source Control", msg),
            _dialogs.PromptAsync,
            _dialogs.LaunchAsync,
            () => (ActiveDocument?.Path, (ActiveDocument?.CaretLine ?? 0) + 1));
        SourceControl.RepositoryChanged += () => ScheduleGitRefresh(full: false);
        SourceControl.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SourceControlViewModel.BranchLabel))
            {
                OnPropertyChanged(nameof(BranchLabel));
            }
        };
        Info.ApplyRequested += a => _ = ApplyCodeActionAsync(a);
    }

    public string BranchLabel => SourceControl.BranchLabel;

    // ---- code actions ("Try this", quick fixes) ----

    public async Task<IReadOnlyList<CodeAction>> CodeActionsAtCaretAsync()
    {
        if (ActiveDocument is not { IsLean: true } d || _server is not { State: LeanServerState.Running } s)
        {
            return [];
        }
        var pos = new Position(d.CaretLine, d.CaretColumn);
        var here = d.Diagnostics.Where(x => x.Extent.Start.Line <= pos.Line && pos.Line <= x.Extent.End.Line).ToList();
        Range range = here.Count > 0 ? here[0].Range : new Range(pos, pos);
        try
        {
            return await s.CodeActionsAsync(d.Uri, range, here);
        }
        catch (Exception e) when (e is JsonRpcException or IOException)
        {
            Log("Code actions: " + e.Message);
            return [];
        }
    }

    public async Task ApplyCodeActionAsync(CodeAction action)
    {
        if (_server is not { State: LeanServerState.Running } s)
        {
            return;
        }
        try
        {
            CodeAction resolved = await s.ResolveAsync(action);
            if (resolved.Edit is WorkspaceEdit edit && !edit.IsEmpty)
            {
                await ApplyWorkspaceEditAsync(edit);
                Log("Applied: " + action.Title);
            }
        }
        catch (Exception e) when (e is JsonRpcException or IOException)
        {
            Log("Could not apply " + action.Title + ": " + e.Message);
        }
    }

    /// <summary>
    /// Apply edits across files. Each file is opened (if it is not already) and edited in the editor, as one undo
    /// step per file, and left unsaved so the person can review the change before saving.
    /// </summary>
    public async Task ApplyWorkspaceEditAsync(WorkspaceEdit edit)
    {
        DocumentViewModel? keep = ActiveDocument;
        foreach ((string uri, IReadOnlyList<TextEdit> edits) in edit.Changes)
        {
            if (edits.Count == 0)
            {
                continue;
            }
            string path = LeanServer.PathOf(uri);
            DocumentViewModel? d = Documents.FirstOrDefault(x => x.Uri == uri || string.Equals(x.Path, path, StringComparison.Ordinal))
                                   ?? await OpenFileAsync(path);
            if (d is null)
            {
                continue;
            }
            TextDocument doc = d.Document;
            using (doc.RunUpdate())
            {
                foreach (TextEdit e in edits.OrderByDescending(e => e.Range.Start).ThenByDescending(e => e.Range.End))
                {
                    int s = Editor.DiagnosticRenderer.Offset(doc, e.Range.Start);
                    int en = Editor.DiagnosticRenderer.Offset(doc, e.Range.End);
                    doc.Replace(s, Math.Max(0, en - s), e.NewText);
                }
            }
        }
        if (keep is not null)
        {
            ActiveDocument = keep;
        }
    }

    // ---- rename and references ----

    [RelayCommand]
    private async Task RenameSymbolAsync()
    {
        if (ActiveDocument is not { IsLean: true } d || _server is not { State: LeanServerState.Running } s)
        {
            return;
        }
        var pos = new Position(d.CaretLine, d.CaretColumn);
        try
        {
            Range? range = await s.PrepareRenameAsync(d.Uri, pos);
            if (range is not Range r)
            {
                Log("Nothing to rename here.");
                return;
            }
            DocumentLine line = d.Document.GetLineByNumber(r.Start.Line + 1);
            string current = d.Document.GetText(line.Offset + r.Start.Character, Math.Max(0, r.End.Character - r.Start.Character));
            string? name = await _dialogs.PromptAsync("Rename", $"Rename {current} to:", current);
            if (string.IsNullOrWhiteSpace(name) || name == current)
            {
                return;
            }
            WorkspaceEdit edit = await s.RenameAsync(d.Uri, pos, name.Trim());
            int count = edit.Changes.Values.Sum(e => e.Count);
            await ApplyWorkspaceEditAsync(edit);
            Log($"Renamed {current} to {name.Trim()}: {count} place{(count == 1 ? "" : "s")} in {edit.Changes.Count} file{(edit.Changes.Count == 1 ? "" : "s")}. Save to keep it.");
        }
        catch (Exception e) when (e is JsonRpcException or IOException)
        {
            Log("Rename: " + e.Message);
        }
    }

    [RelayCommand]
    private async Task FindReferencesAsync()
    {
        if (ActiveDocument is not { IsLean: true } d || _server is not { State: LeanServerState.Running } s)
        {
            return;
        }
        try
        {
            IReadOnlyList<Location> locs = await s.ReferencesAsync(d.Uri, new Position(d.CaretLine, d.CaretColumn));
            var items = new List<LocationItem>();
            foreach (Location l in locs)
            {
                string path = LeanServer.PathOf(l.Uri);
                items.Add(new LocationItem(path, l.Range.Start.Line, l.Range.Start.Character, LineOf(path, l.Range.Start.Line)));
            }
            References.Reset(items);
            ReferencesTitle = $"References ({items.Count})";
            BottomTab = ReferencesPanel;
        }
        catch (Exception e) when (e is JsonRpcException or IOException)
        {
            Log("Find references: " + e.Message);
        }
    }

    private string LineOf(string path, int line)
    {
        DocumentViewModel? open = Documents.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.Ordinal));
        if (open is not null && line < open.Document.LineCount)
        {
            return open.Document.GetText(open.Document.GetLineByNumber(line + 1));
        }
        try
        {
            return File.ReadLines(path).Skip(line).FirstOrDefault() ?? "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    [RelayCommand]
    private async Task OpenLocationAsync(LocationItem? item)
    {
        if (item is not null)
        {
            await OpenFileAsync(item.Path, item.Line, item.Column);
        }
    }

    // ---- outline ----

    private void ScheduleOutline()
    {
        _outlineCts?.Cancel();
        var cts = new CancellationTokenSource();
        _outlineCts = cts;
        _ = RefreshOutlineAsync(cts.Token);
    }

    private async Task RefreshOutlineAsync(CancellationToken ct)
    {
        if (ActiveDocument is not { IsLean: true } d || _server is not { State: LeanServerState.Running } s)
        {
            Outline.Reset([]);
            return;
        }
        try
        {
            await Task.Delay(300, ct);
            IReadOnlyList<DocumentSymbol> symbols = await s.DocumentSymbolsAsync(d.Uri, ct);
            if (ct.IsCancellationRequested || d != ActiveDocument)
            {
                return;
            }
            var items = new List<OutlineItem>();
            string[] lines = d.Lines();
            void Walk(IEnumerable<DocumentSymbol> list, int depth)
            {
                foreach (DocumentSymbol sym in list.OrderBy(x => x.Range.Start))
                {
                    items.Add(new OutlineItem(sym.Name, KindOf(sym, lines), depth, sym.Range, sym.SelectionRange));
                    Walk(sym.Children, depth + 1);
                }
            }
            Walk(symbols, 0);
            Outline.Reset(items);
        }
        catch (Exception e) when (e is OperationCanceledException or JsonRpcException or IOException)
        {
        }
    }

    /// <summary>Lean reports every declaration with one LSP kind; read the keyword from the source instead.</summary>
    private static string KindOf(DocumentSymbol s, string[] lines)
    {
        if (s.Kind == 3)
        {
            return "namespace";
        }
        string text = s.Range.Start.Line < lines.Length ? lines[s.Range.Start.Line] : "";
        foreach (string k in new[] { "theorem", "lemma", "def", "instance", "structure", "class", "inductive", "abbrev", "axiom", "example", "namespace", "section" })
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(text, $@"(^|\s){k}\b"))
            {
                return k == "lemma" ? "theorem" : k;
            }
        }
        return "def";
    }

    [RelayCommand]
    private void GoToOutline(OutlineItem? item)
    {
        if (item is not null)
        {
            ActiveDocument?.Reveal(item.SelectionRange.Start.Line, item.SelectionRange.Start.Character);
        }
    }

    // ---- find in files ----

    [RelayCommand]
    private async Task SearchInFilesAsync()
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        if (Project is null || SearchQuery.Length == 0)
        {
            SearchResults.Reset([]);
            SearchStatus = "";
            return;
        }
        string root = Project.Root, query = SearchQuery;
        bool cs = SearchCaseSensitive, rx = SearchRegex;
        SearchStatus = "Searching…";
        try
        {
            IReadOnlyList<SearchHit> hits = await Task.Run(() => ProjectSearch.Search(root, query, cs, rx, 2000, cts.Token), cts.Token);
            SearchResults.Reset(hits.Select(h => new LocationItem(h.Path, h.Line, h.Column, h.LineText, h.Length)));
            int files = hits.Select(h => h.Path).Distinct().Count();
            SearchStatus = hits.Count == 0 ? "No results." : $"{hits.Count}{(hits.Count >= 2000 ? "+" : "")} results in {files} file{(files == 1 ? "" : "s")}";
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void ShowSearch(string? seed)
    {
        if (!string.IsNullOrEmpty(seed))
        {
            SearchQuery = seed;
        }
        BottomTab = SearchPanel;
        _ = SearchInFilesAsync();
    }

    // ---- quick open and symbols ----

    public IReadOnlyList<string> ProjectFiles() =>
        Project is null ? [] : ProjectSearch.Files(Project.Root).Select(f => Path.GetRelativePath(Project.Root, f)).ToList();

    public async Task<IReadOnlyList<SymbolLocation>> WorkspaceSymbolsAsync(string query, CancellationToken ct)
    {
        if (_server is not { State: LeanServerState.Running } s || query.Trim().Length < 2)
        {
            return [];
        }
        try
        {
            return await s.WorkspaceSymbolsAsync(query.Trim(), ct);
        }
        catch (Exception e) when (e is JsonRpcException or IOException or OperationCanceledException)
        {
            return [];
        }
    }

    // ---- Git ----

    /// <summary>Show a file's changes (a unified diff) in a read-only tab.</summary>
    private Task OpenDiffAsync(string path, string diff)
    {
        string virtualPath = path + ".diff";
        DocumentViewModel? existing = Documents.FirstOrDefault(d => d.Path == virtualPath);
        if (existing is not null)
        {
            existing.ReloadFrom(diff);
            ActiveDocument = existing;
            return Task.CompletedTask;
        }
        var doc = new DocumentViewModel(virtualPath, diff.Length == 0 ? "(no changes)" : diff) { IsVirtual = true };
        Documents.Add(doc);
        ActiveDocument = doc;
        return Task.CompletedTask;
    }

    private void ScheduleGitRefresh(bool full = true)
    {
        _gitCts?.Cancel();
        var cts = new CancellationTokenSource();
        _gitCts = cts;
        _ = RefreshGitAsync(full, cts.Token);
    }

    private async Task RefreshGitAsync(bool full, CancellationToken ct)
    {
        try
        {
            await Task.Delay(400, ct);
            if (full)
            {
                await SourceControl.RefreshAsync();
            }
            if (SourceControl.Repository is GitRepository repo)
            {
                foreach (DocumentViewModel d in Documents.Where(d => !d.IsVirtual).ToList())
                {
                    d.LineChanges = await repo.LineChangesAsync(d.Path, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private async Task CloneRepositoryAsync()
    {
        if (!GitRepository.IsGitInstalled)
        {
            Log("Cloning needs git, which was not found.");
            return;
        }
        string? source = await _dialogs.PromptAsync("Clone a repository", "GitHub owner/repo, or any Git URL:", "");
        if (string.IsNullOrWhiteSpace(source))
        {
            return;
        }
        string? parent = await _dialogs.PickFolderAsync("Clone into which folder?");
        if (parent is null)
        {
            return;
        }
        await RunBusyAsync($"Cloning {source.Trim()}…", async ct =>
        {
            BottomTab = 1;
            var (result, path) = await GitRepository.CloneAsync(source.Trim(), parent, Log, ct);
            if (path is null)
            {
                Log($"Clone failed (exit {result.ExitCode}).");
                return;
            }
            Log("Cloned into " + path);
            await OpenProjectAsync(path);
            if (Project is { DependsOnMathlib: true } p
                && await _dialogs.ConfirmAsync("Mathlib", "This project uses Mathlib. Download Mathlib's prebuilt files now (a few GB) instead of compiling it for hours?"))
            {
                await Core.Projects.Lake.GetCacheAsync(p, Log, ct);
                await RestartServerAsync();
            }
        });
    }
}
