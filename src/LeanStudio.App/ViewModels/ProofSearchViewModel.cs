using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Proofs;

namespace LeanStudio.App.ViewModels;

/// <summary>One tactic's trial against one sorry, as the Prove It card lists it.</summary>
public sealed record TrialView(TacticTrial Trial, SearchResultView Owner)
{
    public string Label => Trial.Label;
    public string Time => Trial.Time;
    public bool Closes => Trial.Closes;
    public string Tip => Trial.Closes ? "Replace the sorry with: " + Owner.Result.Fill(Trial) : Trial.Tactic + " does not close this goal";
}

/// <summary>What proof search found for one sorry, and whether its answer has been used.</summary>
public sealed partial class SearchResultView : ObservableObject
{
    public SearchResultView(SearchResult result)
    {
        Result = result;
        Offset = result.Site.Offset;
    }

    public SearchResult Result { get; }

    /// <summary>Where the sorry is now: fills applied above it move it.</summary>
    public int Offset { get; set; }

    public ObservableList<TrialView> Shown { get; } = new();

    [ObservableProperty]
    private bool _isApplied;

    public string Title => $"{Result.Site.Where}" + (Result.Site.Declaration is string d ? $" · {d}" : "");

    public string Verdict =>
        IsApplied ? "✓ filled in"
        : !Result.Reached ? "not reached: an error earlier in the file stops Lean before it"
        : Result.Best is TacticTrial b ? $"{Result.Successes.Count()} of {Result.Trials.Count(t => t.Outcome != TrialOutcome.Unavailable)} tactics close it; the simplest is {b.Replacement}"
        : "none of the tactics closes this goal: it needs a real idea (or a lemma)";

    public bool CanApply => !IsApplied && Result.Best is not null;

    partial void OnIsAppliedChanged(bool value)
    {
        OnPropertyChanged(nameof(Verdict));
        OnPropertyChanged(nameof(CanApply));
    }

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
public sealed partial class ProofSearchViewModel : ObservableObject
{
    public ObservableList<SearchResultView> Results { get; } = new();

    /// <summary>Set by the window's view model: run a search (true: every sorry in the file).</summary>
    public Func<bool, Task>? Run { get; set; }

    /// <summary>Set by the window's view model: put a tactic in place of a sorry.</summary>
    public Action<SearchResultView, TacticTrial>? ApplyTrial { get; set; }

    public Action? Cancel { get; set; }

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _showFailures;

    [ObservableProperty]
    private bool _canFillAll;

    partial void OnShowFailuresChanged(bool value)
    {
        foreach (SearchResultView r in Results)
        {
            r.Show(value);
        }
    }

    public void Show(IReadOnlyList<SearchResult> results)
    {
        var views = results.Select(r => new SearchResultView(r)).ToList();
        foreach (SearchResultView v in views)
        {
            v.Show(ShowFailures);
        }
        Results.Reset(views);
        int proved = results.Count(r => r.Best is not null);
        Status = results.Count == 1
            ? proved == 1 ? "Found a proof. Click a tactic to use it." : "No tactic in the portfolio closes this goal."
            : $"Found proofs for {proved} of {results.Count} sorries." + (proved > 0 ? " Click a tactic, or fill them all in." : "");
        UpdateCanFillAll();
    }

    public void UpdateCanFillAll() => CanFillAll = Results.Count(r => r.CanApply) > 1;

    [RelayCommand]
    private Task ProveAsync() => Run?.Invoke(false) ?? Task.CompletedTask;

    [RelayCommand]
    private Task ProveAllAsync() => Run?.Invoke(true) ?? Task.CompletedTask;

    [RelayCommand]
    private void Use(TrialView? t)
    {
        if (t is { Closes: true } && !t.Owner.IsApplied)
        {
            ApplyTrial?.Invoke(t.Owner, t.Trial);
        }
    }

    [RelayCommand]
    private void FillAll()
    {
        // Bottom-up, so each fill leaves the offsets above it alone.
        foreach (SearchResultView r in Results.Where(r => r.CanApply).OrderByDescending(r => r.Offset).ToList())
        {
            ApplyTrial?.Invoke(r, r.Result.Best!);
        }
    }

    [RelayCommand]
    private void Close()
    {
        Cancel?.Invoke();
        IsVisible = false;
        Results.Reset([]);
    }
}
