using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Verification;

namespace LeanStudio.App.ViewModels;

/// <summary>
/// Browse every declaration the project can see, read straight from the compiled modules by Tenet: statement,
/// docstring, where it is defined, what it uses, what uses it, and the axioms it rests on.
/// </summary>
public sealed partial class NavigatorViewModel : ObservableObject
{
    private readonly Func<TenetWorkspace?> _workspace;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _detailCts;

    public NavigatorViewModel(Func<TenetWorkspace?> workspace) => _workspace = workspace;

    public ObservableList<DeclarationSummary> Results { get; } = new();
    public ObservableList<string> Uses { get; } = new();
    public ObservableList<string> UsedByList { get; } = new();
    public ObservableList<string> Axioms { get; } = new();

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private DeclarationSummary? _selected;

    [ObservableProperty]
    private DeclarationDetails? _details;

    [ObservableProperty]
    private string _status = "Build the project, then search its declarations and everything it imports.";

    [ObservableProperty]
    private string _axiomSummary = "";

    [ObservableProperty]
    private bool _hasDetails;

    [ObservableProperty]
    private bool _searchEverywhere;

    // ---- Loogle: search all of Mathlib online, by name or by the shape of a type ----

    public ObservableList<Core.Workflow.LoogleHit> LoogleResults { get; } = new();

    [ObservableProperty]
    private string _loogleQuery = "";

    [ObservableProperty]
    private string _loogleStatus = "Search all of Mathlib by name, constant or type shape, e.g. Real.sqrt, \"prime\", or _ * (_ ^ _). Online, at loogle.lean-lang.org.";

    [ObservableProperty]
    private Core.Workflow.LoogleHit? _selectedLoogle;

    /// <summary>Opens a web page (documentation, Loogle).</summary>
    public event Action<Uri>? OpenUrlRequested;

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
            _ = ShowAsync(value.Name);
        }
    }

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

    public async Task ShowAsync(string name)
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
            DeclarationDetails? d = await Task.Run(() => ws.Details(name), cts.Token);
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
            IReadOnlyList<string> axioms = await Task.Run(() => ws.AxiomsOf(name), cts.Token);
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

    [RelayCommand]
    private void OpenSource()
    {
        if (Details?.SourceFile is string f)
        {
            OpenSourceRequested?.Invoke(f, Details.Line ?? 1, Details.Column ?? 0);
        }
    }

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
