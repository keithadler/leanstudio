using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Proofs;

namespace LeanStudio.App.ViewModels;

/// <summary>One tactic's trial against one sorry, as the Prove It card lists it.</summary>
/// <param name="Trial">The tactic and how it fared.</param>
/// <param name="Owner">The sorry it was tried on.</param>
public sealed record TrialView(TacticTrial Trial, SearchResultView Owner)
{
    /// <summary>The tactic with ✓ (as what would replace the sorry), ✗ or –.</summary>
    public string Label => Trial.Label;
    /// <summary>How long the tactic took, or that it is not available with this file's imports.</summary>
    public string Time => Trial.Time;
    /// <summary>The tactic closes the goal.</summary>
    public bool Closes => Trial.Closes;
    /// <summary>The tooltip: what the sorry would become, or that the tactic does not close the goal.</summary>
    public string Tip => Trial.Closes ? "Replace the sorry with: " + Owner.Result.Fill(Trial) : Trial.Tactic + " does not close this goal";
}

/// <summary>What proof search found for one sorry, and whether its answer has been used.</summary>
public sealed partial class SearchResultView : ObservableObject
{
    /// <summary>A view of one sorry's result, with nothing applied yet.</summary>
    /// <param name="result">What proof search found.</param>
    public SearchResultView(SearchResult result)
    {
        Result = result;
        Offset = result.Site.Offset;
    }

    /// <summary>What proof search found for the sorry.</summary>
    public SearchResult Result { get; }

    /// <summary>Where the sorry is now: fills applied above it move it.</summary>
    public int Offset { get; set; }

    /// <summary>
    /// The trials listed: those that close the goal, first, then (when failures are shown) those that fail.
    /// </summary>
    public ObservableList<TrialView> Shown { get; } = new();

    /// <summary>A tactic has replaced the sorry.</summary>
    [ObservableProperty]
    private bool _isApplied;

    /// <summary>Where the sorry is, and the declaration it is in.</summary>
    public string Title => $"{Result.Site.Where}" + (Result.Site.Declaration is string d ? $" · {d}" : "");

    /// <summary>
    /// What the search concluded, in a sentence: filled in, not reached, how many tactics close it, false, or none.
    /// </summary>
    public string Verdict =>
        IsApplied ? "✓ filled in"
        : !Result.Reached ? "not reached: an error earlier in the file stops Lean before it"
        : Result.Best is TacticTrial b ? $"{Result.Successes.Count()} of {Result.Trials.Count(t => t.Outcome is TrialOutcome.Closes or TrialOutcome.Fails)} tactics close it; the simplest is {b.Replacement}"
        : Result.Counterexample is not null ? "no tactic closes it, and it cannot be proved as stated:"
        : "none of the tactics closes this goal: it needs a real idea (or a lemma)";

    /// <summary>Values that make the goal false: the statement needs fixing, not the proof.</summary>
    public string? Counterexample => IsApplied ? null : Result.Counterexample is string c ? "✗ False when " + c : null;

    /// <summary>There is a <see cref="Counterexample"/> to show.</summary>
    public bool HasCounterexample => Counterexample is not null;

    /// <summary>A tactic closes the goal and none has been applied yet.</summary>
    public bool CanApply => !IsApplied && Result.Best is not null;

    /// <summary>Nothing closes it and it is not false: set it aside as a lemma of its own.</summary>
    public bool CanExtract => !IsApplied && Result.Reached && Result.Best is null && Result.Counterexample is null;

    partial void OnIsAppliedChanged(bool value)
    {
        OnPropertyChanged(nameof(Verdict));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(Counterexample));
        OnPropertyChanged(nameof(HasCounterexample));
        OnPropertyChanged(nameof(CanExtract));
    }

    /// <summary>Refill <see cref="Shown"/>. Tactics not available with the file's imports are never listed.</summary>
    /// <param name="failures">Also list the tactics that fail.</param>
    public void Show(bool failures) =>
        Shown.Reset(Result.Trials
            .Where(t => t.Closes || (failures && t.Outcome == TrialOutcome.Fails))
            .OrderBy(t => t.Closes ? 0 : 1)
            .Select(t => new TrialView(t, this)));
}

/// <summary>
/// The Prove It card in the Tactic State panel: tactics tried against the sorry at the cursor (or every sorry in the
/// file), which of them work, and a click to use one.
/// </summary>
/// <remarks>
/// Owned by <see cref="InfoViewModel.Search"/>. The search itself, and editing the file, are done by
/// <see cref="MainViewModel"/>, which sets <see cref="Run"/>, <see cref="ApplyTrial"/>, <see cref="Cancel"/> and
/// <see cref="Extract"/> and hands the results to <see cref="Show"/>.
/// </remarks>
public sealed partial class ProofSearchViewModel : ObservableObject
{
    /// <summary>One result per sorry searched.</summary>
    public ObservableList<SearchResultView> Results { get; } = new();

    /// <summary>Set by the window's view model: run a search (true: every sorry in the file).</summary>
    public Func<bool, Task>? Run { get; set; }

    /// <summary>Set by the window's view model: put a tactic in place of a sorry.</summary>
    public Action<SearchResultView, TacticTrial>? ApplyTrial { get; set; }

    /// <summary>Set by the window's view model: stop a search that is running.</summary>
    public Action? Cancel { get; set; }

    /// <summary>Set by the window's view model: extract a result's goal as a lemma.</summary>
    public Action<SearchResultView>? Extract { get; set; }

    /// <summary>Extract a result's goal as a lemma, when nothing closes it and it is not false.</summary>
    /// <param name="r">The result; null does nothing.</param>
    [RelayCommand]
    private void ExtractAsLemma(SearchResultView? r)
    {
        if (r is { CanExtract: true })
        {
            Extract?.Invoke(r);
        }
    }

    /// <summary>The Prove It card is shown.</summary>
    [ObservableProperty]
    private bool _isVisible;

    /// <summary>A search is running.</summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>The card's summary line: what is being tried, what was found, or why nothing was.</summary>
    [ObservableProperty]
    private string _status = "";

    /// <summary>List the tactics that fail too, not only those that work.</summary>
    [ObservableProperty]
    private bool _showFailures;

    /// <summary>More than one sorry can still be filled in, so Fill All is offered.</summary>
    [ObservableProperty]
    private bool _canFillAll;

    partial void OnShowFailuresChanged(bool value)
    {
        foreach (SearchResultView r in Results)
        {
            r.Show(value);
        }
    }

    /// <summary>Show a search's results, replacing the last, with a summary in <see cref="Status"/>.</summary>
    /// <param name="results">One result per sorry searched.</param>
    public void Show(IReadOnlyList<SearchResult> results)
    {
        var views = results.Select(r => new SearchResultView(r)).ToList();
        foreach (SearchResultView v in views)
        {
            v.Show(ShowFailures);
        }
        Results.Reset(views);
        int proved = results.Count(r => r.Best is not null);
        int refuted = results.Count(r => r.Counterexample is not null);
        Status = results.Count == 1
            ? proved == 1 ? "Found a proof. Click a tactic to use it."
            : refuted == 1 ? "This goal is false: check the statement or the hypotheses."
            : "No tactic in the portfolio closes this goal."
            : $"Found proofs for {proved} of {results.Count} sorries." + (proved > 0 ? " Click a tactic, or fill them all in." : "")
              + (refuted > 0 ? $" {refuted} {(refuted == 1 ? "is" : "are")} false as stated." : "");
        UpdateCanFillAll();
    }

    /// <summary>Recompute <see cref="CanFillAll"/>, after a fill.</summary>
    public void UpdateCanFillAll() => CanFillAll = Results.Count(r => r.CanApply) > 1;

    /// <summary>Search again, on the sorry at the caret.</summary>
    [RelayCommand]
    private Task ProveAsync() => Run?.Invoke(false) ?? Task.CompletedTask;

    /// <summary>Search again, on every sorry in the file.</summary>
    [RelayCommand]
    private Task ProveAllAsync() => Run?.Invoke(true) ?? Task.CompletedTask;

    /// <summary>Put a tactic that closes the goal in place of its sorry.</summary>
    /// <param name="t">The trial; null, a failing one, or one whose sorry is already filled does nothing.</param>
    [RelayCommand]
    private void Use(TrialView? t)
    {
        if (t is { Closes: true } && !t.Owner.IsApplied)
        {
            ApplyTrial?.Invoke(t.Owner, t.Trial);
        }
    }

    /// <summary>
    /// Fill every sorry that can be with its simplest closing tactic, from the bottom of the file up.
    /// </summary>
    [RelayCommand]
    private void FillAll()
    {
        // Bottom-up, so each fill leaves the offsets above it alone.
        foreach (SearchResultView r in Results.Where(r => r.CanApply).OrderByDescending(r => r.Offset).ToList())
        {
            ApplyTrial?.Invoke(r, r.Result.Best!);
        }
    }

    /// <summary>Stop any search, hide the card and forget its results.</summary>
    [RelayCommand]
    private void Close()
    {
        Cancel?.Invoke();
        IsVisible = false;
        Results.Reset([]);
    }
}
