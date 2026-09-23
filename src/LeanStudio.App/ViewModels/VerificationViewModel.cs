using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Verification;

namespace LeanStudio.App.ViewModels;

public sealed record VerdictView(DeclarationVerdict Verdict)
{
    public string Name => Verdict.Name;
    public string Module => Verdict.Module;
    public string Icon => Verdict.Status switch
    {
        VerificationStatus.Verified => "✓",
        VerificationStatus.RestsOnAssumption => "◐",
        _ => "✗",
    };
    public string Detail => Verdict.Status switch
    {
        VerificationStatus.Verified => "verified",
        VerificationStatus.RestsOnAssumption when Verdict.Assumptions.Contains(Verdict.Name) => "an axiom this project introduces",
        VerificationStatus.RestsOnAssumption => "rests on " + string.Join(", ", Verdict.Assumptions.Select(a => a == "sorryAx" ? "sorry" : a)),
        _ => "rejected: " + (Verdict.Message ?? "").Split('\n')[0],
    };
    public VerificationStatus Status => Verdict.Status;

    /// <summary>It rests on an assumption without being one: there is a chain to show.</summary>
    public bool CanExplain => Verdict.Status == VerificationStatus.RestsOnAssumption && !Verdict.Assumptions.Contains(Verdict.Name);
}

/// <summary>One step of a "why is this not proved" chain.</summary>
public sealed record TrailLinkView(TrailLink Link, bool IsFirst, bool IsCulprit)
{
    public string Arrow => IsFirst ? "" : "→";
    public string Name => Link.Display;
    public string Where => Link.SourceFile is string f && Link.Line is int l ? $"{Path.GetFileName(f)}:{l}" : Link.IsSorry ? "" : Link.Module;
    public bool CanOpen => Link.SourceFile is not null;
    public string Note => IsCulprit ? (Link.IsSorry ? "" : "← fix this one") : "";
}

/// <summary>One chain, from the declaration down to the sorry or axiom it rests on.</summary>
public sealed record TrailView(AssumptionTrail Trail)
{
    public string Heading => Trail.IsSorry ? "rests on sorry" : $"rests on the axiom {Trail.Assumption}";
    public IReadOnlyList<TrailLinkView> Links { get; } =
        Trail.Path.Select((l, i) => new TrailLinkView(l, i == 0, i == Trail.Path.Count - 2)).ToList();
}

/// <summary>The Tenet panel: the last independent re-check of the project, and its progress while one runs.</summary>
public sealed partial class VerificationViewModel : ObservableObject
{
    public ObservableList<VerdictView> Items { get; } = new();

    private IReadOnlyList<VerdictView> _all = [];

    [ObservableProperty]
    private string _summary = "Tenet re-checks what Lean built with an independent kernel. Build the project, then Verify.";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private int _filter; // 0 all, 1 needs attention, 2 verified

    [ObservableProperty]
    private VerificationReport? _report;

    partial void OnFilterChanged(int value) => Apply();

    // ---- why a declaration is not fully proved ----

    public ObservableList<TrailView> Trails { get; } = new();

    [ObservableProperty]
    private string _trailTitle = "";

    [ObservableProperty]
    private bool _hasTrail;

    /// <summary>Set by the window's view model: find the chains for a declaration.</summary>
    public Func<string, Task>? Explain { get; set; }

    /// <summary>Opens a source file at a 1-based line.</summary>
    public event Action<string, int>? OpenRequested;

    [RelayCommand]
    private Task Why(VerdictView? v) => v is null || Explain is null ? Task.CompletedTask : Explain(v.Name);

    [RelayCommand]
    private void OpenLink(TrailLinkView? l)
    {
        if (l?.Link.SourceFile is string f)
        {
            OpenRequested?.Invoke(f, l.Link.Line ?? 1);
        }
    }

    [RelayCommand]
    private void CloseTrail()
    {
        HasTrail = false;
        Trails.Reset([]);
    }

    public void ShowTrails(string name, IReadOnlyList<AssumptionTrail> trails)
    {
        Trails.Reset(trails.Select(t => new TrailView(t)));
        TrailTitle = trails.Count == 0
            ? $"{name} is fully proved: no sorry, and no axiom beyond propext, Classical.choice and Quot.sound."
            : $"Why {name} is not fully proved" + (trails.Count > 1 ? $" ({trails.Count} reasons)" : "") + ": "
              + string.Join("; ", trails.Select(t => t.Culprit is TrailLink c
                  ? (t.IsSorry ? $"{c.Display} uses sorry" : $"{c.Display} uses the axiom {t.Assumption}")
                  : t.Assumption));
        HasTrail = true;
    }

    public void Show(VerificationReport r)
    {
        Report = r;
        _all = r.Declarations
            .OrderBy(d => d.Status == VerificationStatus.Rejected ? 0 : d.Status == VerificationStatus.RestsOnAssumption ? 1 : 2)
            .ThenBy(d => d.Module, StringComparer.Ordinal)
            .ThenBy(d => d.Line ?? 0)
            .Select(d => new VerdictView(d))
            .ToList();
        Apply();
        Summary = r.Declarations.Count == 0
            ? "Nothing to check: the project has no built modules of its own."
            : $"Tenet checked {r.Declarations.Count:N0} declarations in {r.ModulesChecked} module{(r.ModulesChecked == 1 ? "" : "s")} "
              + $"({r.ModulesLoaded:N0} loaded) in {r.Elapsed.TotalSeconds:F1}s:  "
              + $"✓ {r.Verified} verified   ◐ {r.Conditional} resting on sorry or an axiom   ✗ {r.Rejected} rejected";
    }

    private void Apply() => Items.Reset(Filter switch
    {
        1 => _all.Where(v => v.Status != VerificationStatus.Verified),
        2 => _all.Where(v => v.Status == VerificationStatus.Verified),
        _ => _all,
    });
}
