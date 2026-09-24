using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Verification;

namespace LeanStudio.App.ViewModels;

/// <summary>
/// Browse every declaration the project can see, read straight from the compiled modules by Tenet: statement,
/// docstring, where it is defined, what it uses, what uses it, and the axioms it rests on.
/// </summary>
/// <remarks>
/// Backs the Library panel. Declarations come from the <see cref="TenetWorkspace"/> that <see cref="MainViewModel"/>
/// opens on the last build (so nothing shows until the project is built), read on background threads; LeanSearch
/// and Loogle are asked over the network. Opening a file or a web page is left to the window, through
/// <see cref="OpenSourceRequested"/> and <see cref="OpenUrlRequested"/>.
/// </remarks>
public sealed partial class NavigatorViewModel : ObservableObject
{
    private readonly Func<TenetWorkspace?> _workspace;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _detailCts;

    /// <summary>Create the panel's state; it reads the workspace afresh each time it needs it.</summary>
    /// <param name="workspace">The workspace Tenet has open on the project's build, or null when there is none.</param>
    public NavigatorViewModel(Func<TenetWorkspace?> workspace) => _workspace = workspace;

    /// <summary>
    /// Declarations whose names contain every word of <see cref="Query"/>, shortest first, at most 400.
    /// </summary>
    public ObservableList<DeclarationSummary> Results { get; } = new();
    /// <summary>The declarations the one shown uses.</summary>
    public ObservableList<string> Uses { get; } = new();
    /// <summary>The declarations that mention the one shown directly, after Find Used By.</summary>
    public ObservableList<string> UsedByList { get; } = new();
    /// <summary>The axioms the declaration shown rests on, transitively (as <c>#print axioms</c> lists them).</summary>
    public ObservableList<string> Axioms { get; } = new();

    /// <summary>
    /// The name search. Changing it searches again, after a short pause; fewer than two characters clears the results.
    /// </summary>
    [ObservableProperty]
    private string _query = "";

    /// <summary>The result selected in the list; selecting one shows its details.</summary>
    [ObservableProperty]
    private DeclarationSummary? _selected;

    /// <summary>The declaration shown, or null.</summary>
    /// <summary>The selected declaration's docstring, with its math ($…$) shown as text.</summary>
    public string DocStringText => Details?.DocString is string d ? Core.Editing.LatexText.ToUnicode(d) : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DocStringText))]
    private DeclarationDetails? _details;

    /// <summary>
    /// The panel's status line: how many modules are open, the number of matches, or what to do first.
    /// </summary>
    [ObservableProperty]
    private string _status = "Build the project, then search its declarations and everything it imports.";

    /// <summary>
    /// What the axioms of the declaration shown mean: none, only Lean's standard ones, sorry, or others.
    /// </summary>
    [ObservableProperty]
    private string _axiomSummary = "";

    /// <summary>A declaration is shown.</summary>
    [ObservableProperty]
    private bool _hasDetails;

    /// <summary>Find Used By looks through every open module (dependencies too), not only the project's own.</summary>
    [ObservableProperty]
    private bool _searchEverywhere;

    // ---- LeanSearch: find a Mathlib result from a description in plain English ----

    /// <summary>LeanSearch's results for <see cref="MeaningQuery"/>, closest first.</summary>
    public ObservableList<Core.Workflow.MeaningHit> MeaningResults { get; } = new();

    /// <summary>A result described in plain English, for LeanSearch.</summary>
    [ObservableProperty]
    private string _meaningQuery = "";

    /// <summary>What the LeanSearch part says: how to use it, progress, the result count, or the error.</summary>
    [ObservableProperty]
    private string _meaningStatus = "Describe a result in words, e.g. \"the sum of the first n odd numbers is n squared\" or \"a continuous function on a closed interval attains its maximum\". Online, at leansearch.net.";

    /// <summary>
    /// The LeanSearch result selected. Selecting one shows it here when the project's build has it, and otherwise opens
    /// its documentation page.
    /// </summary>
    [ObservableProperty]
    private Core.Workflow.MeaningHit? _selectedMeaning;

    /// <summary>A LeanSearch request is running; another is not sent until it answers.</summary>
    [ObservableProperty]
    private bool _meaningBusy;

    /// <summary>
    /// Ask LeanSearch (online) for up to 25 results matching <see cref="MeaningQuery"/>. Errors are shown in
    /// <see cref="MeaningStatus"/>.
    /// </summary>
    [RelayCommand]
    public async Task SearchMeaningAsync()
    {
        string q = MeaningQuery.Trim();
        if (q.Length == 0 || MeaningBusy)
        {
            return;
        }
        MeaningBusy = true;
        MeaningStatus = "Asking LeanSearch…";
        try
        {
            IReadOnlyList<Core.Workflow.MeaningHit> hits = await new Core.Workflow.LeanSearch().SearchAsync(q, 25);
            MeaningResults.Reset(hits);
            MeaningStatus = hits.Count == 0 ? "No results." : $"{hits.Count} results, closest first. Click one for its statement and documentation.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            MeaningStatus = "Could not reach LeanSearch: " + e.Message;
        }
        finally
        {
            MeaningBusy = false;
        }
    }

    partial void OnSelectedMeaningChanged(Core.Workflow.MeaningHit? value)
    {
        if (value is null)
        {
            return;
        }
        if (_workspace() is TenetWorkspace ws && ws.Details(value.Name) is not null)
        {
            Query = value.Name;
            _ = ShowAsync(value.Name);
        }
        else
        {
            OpenUrlRequested?.Invoke(new Uri(Core.Workflow.DocLinks.For(value.Module, value.Name)));
        }
    }

    // ---- Loogle: search all of Mathlib online, by name or by the shape of a type ----

    /// <summary>Loogle's results for <see cref="LoogleQuery"/>.</summary>
    public ObservableList<Core.Workflow.LoogleHit> LoogleResults { get; } = new();

    /// <summary>A Loogle query: a name, a constant, a string, or a type pattern.</summary>
    [ObservableProperty]
    private string _loogleQuery = "";

    /// <summary>What the Loogle part says: how to use it, progress, the result count, or Loogle's error.</summary>
    [ObservableProperty]
    private string _loogleStatus = "Search all of Mathlib by name, constant or type shape, e.g. Real.sqrt, \"prime\", or _ * (_ ^ _). Online, at loogle.lean-lang.org.";

    /// <summary>
    /// The Loogle result selected. Selecting one shows it here when the project's build has it, and otherwise opens its
    /// documentation page.
    /// </summary>
    [ObservableProperty]
    private Core.Workflow.LoogleHit? _selectedLoogle;

    /// <summary>Opens a web page (documentation, Loogle).</summary>
    public event Action<Uri>? OpenUrlRequested;

    /// <summary>
    /// Ask Loogle (online) for <see cref="LoogleQuery"/>. Errors are shown in <see cref="LoogleStatus"/>.
    /// </summary>
    [RelayCommand]
    private async Task SearchLoogleAsync()
    {
        string q = LoogleQuery.Trim();
        if (q.Length == 0)
        {
            return;
        }
        LoogleStatus = "Searching Loogle…";
        try
        {
            var (hits, error, count) = await new Core.Workflow.Loogle().SearchAsync(q);
            LoogleResults.Reset(hits);
            LoogleStatus = error is not null ? "Loogle: " + error.Trim()
                : hits.Count == 0 ? "No results."
                : count > hits.Count ? $"Showing {hits.Count} of {count} results." : $"{count} result{(count == 1 ? "" : "s")}.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            LoogleStatus = "Could not reach Loogle: " + e.Message;
        }
    }

    partial void OnSelectedLoogleChanged(Core.Workflow.LoogleHit? value)
    {
        if (value is null)
        {
            return;
        }
        // Show it here when the project can see it; otherwise its documentation page has everything.
        if (_workspace() is TenetWorkspace ws && ws.Details(value.Name) is not null)
        {
            Query = value.Name;
            _ = ShowAsync(value.Name);
        }
        else
        {
            OpenUrlRequested?.Invoke(new Uri(Core.Workflow.DocLinks.For(value.Module, value.Name)));
        }
    }

    /// <summary>Open the declaration shown on the documentation site for Mathlib and its dependencies.</summary>
    [RelayCommand]
    private void OpenDocumentation()
    {
        if (Details is { Module.Length: > 0 } d)
        {
            OpenUrlRequested?.Invoke(new Uri(Core.Workflow.DocLinks.For(d.Module, d.Name)));
        }
    }

    /// <summary>Opens a source file at a 1-based line and 0-based column.</summary>
    public event Action<string, int, int>? OpenSourceRequested;

    partial void OnQueryChanged(string value) => _ = SearchAsync(value);

    partial void OnSelectedChanged(DeclarationSummary? value)
    {
        if (value is not null)
        {
            _ = ShowAsync(value.Name, value.Module);
        }
    }

    /// <summary>Called when Tenet opens a new build (or closes it): update the status and search again.</summary>
    public void WorkspaceChanged()
    {
        TenetWorkspace? ws = _workspace();
        Status = ws is null
            ? "Build the project, then search its declarations and everything it imports."
            : $"{ws.ModuleCount:N0} modules open (Lean {ws.LeanVersion}). Search by any part of a name.";
        _ = SearchAsync(Query);
    }

    private async Task SearchAsync(string query)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        TenetWorkspace? ws = _workspace();
        if (ws is null || query.Trim().Length < 2)
        {
            Results.Reset([]);
            return;
        }
        try
        {
            await Task.Delay(150, cts.Token);
            IReadOnlyList<DeclarationSummary> hits = await Task.Run(() => ws.Search(query, 400, cts.Token), cts.Token);
            if (!cts.IsCancellationRequested)
            {
                Results.Reset(hits);
                Status = hits.Count == 400 ? "Showing the first 400 matches" : hits.Count == 1 ? "1 match" : $"{hits.Count} matches";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Show a declaration's details and what it uses, then the axioms it rests on, read by Tenet on a background thread.
    /// A newer call cancels an older one. Does nothing without a build; a name the build does not have clears the details.
    /// </summary>
    /// <param name="name">The declaration's full name.</param>
    /// <param name="module">The module it is in, when several of the project's modules declare the name.</param>
    public async Task ShowAsync(string name, string? module = null)
    {
        _detailCts?.Cancel();
        var cts = new CancellationTokenSource();
        _detailCts = cts;
        TenetWorkspace? ws = _workspace();
        if (ws is null)
        {
            return;
        }
        try
        {
            module ??= ws.ModulesDeclaring(name).FirstOrDefault(); // a name the project declares twice: the first, unless one is given
            DeclarationDetails? d = await Task.Run(() => ws.Details(name, module), cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }
            Details = d;
            HasDetails = d is not null;
            Uses.Reset(d?.Uses ?? []);
            UsedByList.Reset([]);
            Axioms.Reset([]);
            AxiomSummary = "";
            if (d is null)
            {
                return;
            }
            IReadOnlyList<string> axioms = await Task.Run(() => ws.AxiomsOf(name, module), cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }
            Axioms.Reset(axioms);
            string[] standard = ["propext", "Classical.choice", "Quot.sound"];
            var extra = axioms.Where(a => !standard.Contains(a)).ToList();
            AxiomSummary = axioms.Count == 0 ? "Depends on no axioms."
                : extra.Count == 0 ? "Depends only on Lean's standard axioms."
                : extra.Contains("sorryAx") ? "Rests on sorry: this is not proved."
                : $"Rests on {extra.Count} non-standard axiom{(extra.Count == 1 ? "" : "s")}: {string.Join(", ", extra)}";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is Tenet.Kernel.KernelException or InvalidOperationException)
        {
            AxiomSummary = "Tenet could not read this declaration: " + e.Message;
        }
    }

    /// <summary>
    /// List the declarations that use the one shown, scanning the project's modules (or every open module, with
    /// <see cref="SearchEverywhere"/>) on a background thread.
    /// </summary>
    [RelayCommand]
    private async Task FindUsedByAsync()
    {
        TenetWorkspace? ws = _workspace();
        if (ws is null || Details is null)
        {
            return;
        }
        string name = Details.Name;
        _detailCts?.Cancel();
        var cts = new CancellationTokenSource();
        _detailCts = cts;
        Status = SearchEverywhere ? "Scanning every open module…" : "Scanning the project's modules…";
        try
        {
            IReadOnlyList<string> hits = await Task.Run(() => ws.UsedBy(name, SearchEverywhere, cts.Token), cts.Token);
            UsedByList.Reset(hits);
            Status = $"{hits.Count} declaration{(hits.Count == 1 ? "" : "s")} use {name}";
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Open the declaration shown in the editor, when its source file is known.</summary>
    [RelayCommand]
    private void OpenSource()
    {
        if (Details?.SourceFile is string f)
        {
            OpenSourceRequested?.Invoke(f, Details.Line ?? 1, Details.Column ?? 0);
        }
    }

    /// <summary>Show another declaration, such as one the current one uses, and search for it.</summary>
    /// <param name="name">Its full name; null does nothing.</param>
    [RelayCommand]
    private void Navigate(string? name)
    {
        if (name is not null)
        {
            Query = name;
            _ = ShowAsync(name);
        }
    }
}
