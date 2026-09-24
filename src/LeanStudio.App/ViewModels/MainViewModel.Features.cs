using Avalonia.Threading;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Editing;
using LeanStudio.Core.Git;
using LeanStudio.Lsp;
using Range = LeanStudio.Lsp.Range;

namespace LeanStudio.App.ViewModels;

/// <summary>A declaration or namespace in the Outline panel, from Lean's document symbols.</summary>
/// <param name="Name">The name Lean reports.</param>
/// <param name="Kind">
/// The keyword read from the source: <c>theorem</c> (also for <c>lemma</c>), <c>def</c>, <c>structure</c>,
/// <c>namespace</c>…
/// </param>
/// <param name="Depth">How deeply it is nested, 0 at the top level.</param>
/// <param name="Range">The whole declaration, doc comment and attributes included.</param>
/// <param name="SelectionRange">The name, where going to it puts the caret.</param>
/// <param name="Status">✓, ◐, ✗ or empty; see <see cref="IsProved"/>.</param>
public sealed record OutlineItem(string Name, string Kind, int Depth, Range Range, Range SelectionRange, string Status = "")
{
    /// <summary>For theorems: ✓ Lean accepts it, ◐ it uses sorry, ✗ it has an error. Live, without building.</summary>
    public bool IsProved => Status == "✓";
    /// <summary>It uses <c>sorry</c> (◐).</summary>
    public bool IsIncomplete => Status == "◐";
    /// <summary>Lean reports an error in it (✗).</summary>
    public bool IsBroken => Status == "✗";

    /// <summary>Three spaces per level of nesting.</summary>
    public string Indent => new(' ', Depth * 3);
    /// <summary>
    /// A symbol for the kind: <c>{}</c> namespace, ◇ structure or class, ◆ inductive, ⊢ theorem, ƒ anything else.
    /// </summary>
    public string Icon => Kind switch
    {
        "namespace" => "{}",
        "structure" or "class" => "◇",
        "inductive" => "◆",
        "theorem" => "⊢",
        _ => "ƒ",
    };
}

/// <summary>A place in a file, in the References and Search panels.</summary>
/// <param name="Path">The file.</param>
/// <param name="Line">The 0-based line.</param>
/// <param name="Column">The 0-based column.</param>
/// <param name="Preview">The line's text.</param>
/// <param name="MatchLength">How many characters matched, for highlighting a search hit; 0 for a reference.</param>
public sealed record LocationItem(string Path, int Line, int Column, string Preview, int MatchLength = 0)
{
    /// <summary>The file's name, without its folder.</summary>
    public string File => System.IO.Path.GetFileName(Path);
    /// <summary>The 1-based line and column, as <c>line:column</c>.</summary>
    public string Where => $"{Line + 1}:{Column + 1}";
    /// <summary>The line's text, without surrounding whitespace.</summary>
    public string Text => Preview.Trim();
}

/// <summary>The editor features beyond editing: code actions, rename, references, outline, search, symbols, Git.</summary>
public sealed partial class MainViewModel
{
    /// <summary>The side panels' indices in <see cref="SidebarTab"/> (<see cref="LearnTab"/> is the sixth).</summary>
    public const int FilesTab = 0, OutlineTab = 1, LibraryTab = 2, GitTab = 3, ToolchainsTab = 4;
    /// <summary>
    /// The bottom panels' indices in <see cref="BottomTab"/> (<see cref="MarkersPanel"/>, <see cref="TimingPanel"/> and
    /// <see cref="ReplPanel"/> are the others).
    /// </summary>
    public const int ProblemsPanel = 0, OutputPanel = 1, TenetPanel = 2, ReferencesPanel = 3, SearchPanel = 5;

    /// <summary>
    /// The Outline panel: the active Lean file's declarations in order, nested ones after their parent.
    /// </summary>
    public ObservableList<OutlineItem> Outline { get; } = new();
    /// <summary>The References panel: the last Find References' results.</summary>
    public ObservableList<LocationItem> References { get; } = new();
    /// <summary>The Search panel: the last find-in-files' hits, at most 2000.</summary>
    public ObservableList<LocationItem> SearchResults { get; } = new();

    /// <summary>The References panel's title, with the count.</summary>
    [ObservableProperty]
    private string _referencesTitle = "References";

    /// <summary>The text (or regular expression) to find in the project's files.</summary>
    [ObservableProperty]
    private string _searchQuery = "";

    /// <summary>Find-in-files matches case.</summary>
    [ObservableProperty]
    private bool _searchCaseSensitive;

    /// <summary>The query is a regular expression.</summary>
    [ObservableProperty]
    private bool _searchRegex;

    /// <summary>The Search panel's status: the hit count, <c>Searching…</c>, or an invalid expression.</summary>
    [ObservableProperty]
    private string _searchStatus = "";

    /// <summary>The Source Control panel. Pointed at the open project's repository when it is opened.</summary>
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

    /// <summary>
    /// The branch and its sync state, for the status bar (see <see cref="SourceControlViewModel.BranchLabel"/>).
    /// </summary>
    public string BranchLabel => SourceControl.BranchLabel;

    // ---- code actions ("Try this", quick fixes) ----

    /// <summary>
    /// The code actions Lean offers at the caret (quick fixes, "Try this" suggestions), for the first message on the
    /// caret's line, or for the caret position when there is none.
    /// </summary>
    /// <returns>
    /// The actions; empty without an active Lean file and a running server, or when Lean fails (logged).
    /// </returns>
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

    /// <summary>
    /// Resolve a code action with Lean and apply its edit through <see cref="ApplyWorkspaceEditAsync"/>, which leaves
    /// the files unsaved. Failures are logged.
    /// </summary>
    /// <param name="action">The action, as Lean offered it.</param>
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
    /// <param name="edit">The edits, by file URI. The file that was active stays active.</param>
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

    /// <summary>
    /// Rename the name at the caret everywhere Lean knows it is used, asking for the new name. The edited files are left
    /// unsaved.
    /// </summary>
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

    /// <summary>Ask Lean for every use of the name at the caret and list them in the References panel.</summary>
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

    /// <summary>Open a reference or search hit in the editor.</summary>
    /// <param name="item">The place; null does nothing.</param>
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
                    string kind = KindOf(sym, lines);
                    items.Add(new OutlineItem(sym.Name, kind, depth, sym.Range, sym.SelectionRange, StatusOf(d, sym, kind)));
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

    private static string StatusOf(DocumentViewModel d, DocumentSymbol s, string kind)
    {
        var inside = d.Diagnostics.Where(x => s.Range.Start.Line <= x.Range.Start.Line && x.Range.Start.Line <= s.Range.End.Line).ToList();
        if (inside.Any(x => x.Severity == DiagnosticSeverity.Error))
        {
            return "✗";
        }
        if (inside.Any(x => x.Message.Contains("sorry", StringComparison.Ordinal)))
        {
            return "◐";
        }
        return kind is "theorem" or "example" && !d.IsProcessing ? "✓" : "";
    }

    /// <summary>Lean reports every declaration with one LSP kind; read the keyword from the source instead.</summary>
    private static string KindOf(DocumentSymbol s, string[] lines)
    {
        if (s.Kind == 3)
        {
            return "namespace";
        }
        // The name's line, not the range's first line: that can be a doc comment or an attribute.
        string text = s.SelectionRange.Start.Line < lines.Length ? lines[s.SelectionRange.Start.Line] : "";
        foreach (string k in new[] { "theorem", "lemma", "def", "instance", "structure", "class", "inductive", "abbrev", "axiom", "example", "namespace", "section" })
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(text, $@"(^|\s){k}\b"))
            {
                return k == "lemma" ? "theorem" : k;
            }
        }
        return "def";
    }

    /// <summary>Move the caret to an outline item's name.</summary>
    /// <param name="item">The item; null does nothing.</param>
    [RelayCommand]
    private void GoToOutline(OutlineItem? item)
    {
        if (item is not null)
        {
            ActiveDocument?.Reveal(item.SelectionRange.Start.Line, item.SelectionRange.Start.Character);
        }
    }

    // ---- find in files ----

    /// <summary>
    /// Search the project's files for <see cref="SearchQuery"/> on a background thread and list the hits (at most 2000).
    /// A newer search cancels an older one.
    /// </summary>
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

    /// <summary>Show the Search panel and run the search.</summary>
    /// <param name="seed">
    /// Text to search for (such as the editor's selection), or null or empty to keep the current query.
    /// </param>
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

    /// <summary>The project's files, relative to its root, for quick open; empty with no project.</summary>
    public IReadOnlyList<string> ProjectFiles() =>
        Project is null ? [] : ProjectSearch.Files(Project.Root).Select(f => Path.GetRelativePath(Project.Root, f)).ToList();

    /// <summary>Ask Lean for declarations whose names match, for the symbol picker.</summary>
    /// <param name="query">Part of a name; fewer than two characters gives nothing.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The matches; empty without a running server, or when the request fails or is cancelled.</returns>
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

    private bool _fullGitRefreshPending;

    /// <summary>
    /// Debounced: a burst of changes becomes one refresh. A full refresh (the file list) that is still pending
    /// is never downgraded to a partial one (just the gutter) by a later request.
    /// </summary>
    private void ScheduleGitRefresh(bool full = true)
    {
        _gitCts?.Cancel();
        var cts = new CancellationTokenSource();
        _gitCts = cts;
        _fullGitRefreshPending |= full;
        _ = RefreshGitAsync(_fullGitRefreshPending, cts.Token);
    }

    private async Task RefreshGitAsync(bool full, CancellationToken ct)
    {
        try
        {
            await Task.Delay(400, ct);
            if (full)
            {
                _fullGitRefreshPending = false;
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

    /// <summary>
    /// Ask for a repository (GitHub <c>owner/repo</c> or any Git URL) and a folder, clone it, and open it as the project.
    /// For a Mathlib project, offers to download Mathlib's prebuilt files.
    /// </summary>
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
