using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Verification;

namespace LeanStudio.App.ViewModels;

/// <summary>One declaration's verdict in the Tenet panel's list.</summary>
/// <param name="Verdict">What Tenet concluded about it.</param>
public sealed record VerdictView(DeclarationVerdict Verdict)
{
    /// <summary>The declaration's full name.</summary>
    public string Name => Verdict.Name;
    /// <summary>The module that defines it.</summary>
    public string Module => Verdict.Module;
    /// <summary>✓ verified, ◐ rests on sorry or an axiom, ✗ rejected.</summary>
    public string Icon => Verdict.Status switch
    {
        VerificationStatus.Verified => "✓",
        VerificationStatus.RestsOnAssumption => "◐",
        _ => "✗",
    };
    /// <summary>The verdict in words: what it rests on, or the first line of why it was rejected.</summary>
    public string Detail => Verdict.Status switch
    {
        VerificationStatus.Verified => "verified",
        VerificationStatus.RestsOnAssumption when Verdict.Assumptions.Contains(Verdict.Name) => "an axiom this project introduces",
        VerificationStatus.RestsOnAssumption => "rests on " + string.Join(", ", Verdict.Assumptions.Select(a => a == "sorryAx" ? "sorry" : a)),
        _ => "rejected: " + (Verdict.Message ?? "").Split('\n')[0],
    };
    /// <summary>The verdict.</summary>
    public VerificationStatus Status => Verdict.Status;

    /// <summary>It rests on an assumption without being one: there is a chain to show.</summary>
    public bool CanExplain => Verdict.Status == VerificationStatus.RestsOnAssumption && !Verdict.Assumptions.Contains(Verdict.Name);
}

/// <summary>One step of a "why is this not proved" chain.</summary>
/// <param name="Link">The declaration at this step.</param>
/// <param name="IsFirst">It is the declaration asked about, at the start of the chain.</param>
/// <param name="IsCulprit">It is the one that uses the sorry or axiom directly.</param>
public sealed record TrailLinkView(TrailLink Link, bool IsFirst, bool IsCulprit)
{
    /// <summary>→ before every step but the first.</summary>
    public string Arrow => IsFirst ? "" : "→";
    /// <summary>The declaration's name, as shown.</summary>
    public string Name => Link.Display;
    /// <summary>
    /// The source file's name and line, the module when the source is not known, or empty for <c>sorry</c> itself.
    /// </summary>
    public string Where => Link.SourceFile is string f && Link.Line is int l ? $"{Path.GetFileName(f)}:{l}" : Link.IsSorry ? "" : Link.Module;
    /// <summary>Its source file is known, so it can be opened.</summary>
    public bool CanOpen => Link.SourceFile is not null;
    /// <summary><c>← fix this one</c> on the culprit, unless it is itself the sorry.</summary>
    public string Note => IsCulprit ? (Link.IsSorry ? "" : "← fix this one") : "";
}

/// <summary>One chain, from the declaration down to the sorry or axiom it rests on.</summary>
/// <param name="Trail">The chain, as Tenet found it.</param>
public sealed record TrailView(AssumptionTrail Trail)
{
    /// <summary>What the chain ends in: sorry, or which axiom.</summary>
    public string Heading => Trail.IsSorry ? "rests on sorry" : $"rests on the axiom {Trail.Assumption}";
    /// <summary>The chain's steps, from the declaration down.</summary>
    public IReadOnlyList<TrailLinkView> Links { get; } =
        Trail.Path.Select((l, i) => new TrailLinkView(l, i == 0, i == Trail.Path.Count - 2)).ToList();
}

/// <summary>The Tenet panel: the last independent re-check of the project, and its progress while one runs.</summary>
/// <remarks>
/// <see cref="MainViewModel"/> runs Tenet and hands the report to <see cref="Show"/>, and sets progress while it
/// runs; it also traces why a declaration is not fully proved (through <see cref="Explain"/>) and hands the chains to
/// <see cref="ShowTrails"/>. Used on the UI thread.
/// </remarks>
public sealed partial class VerificationViewModel : ObservableObject
{
    /// <summary>
    /// The verdicts that pass <see cref="Filter"/>: rejected first, then those resting on an assumption, then verified.
    /// </summary>
    public ObservableList<VerdictView> Items { get; } = new();

    private IReadOnlyList<VerdictView> _all = [];

    /// <summary>The panel's summary line: what Tenet does, progress, the counts, or what went wrong.</summary>
    [ObservableProperty]
    private string _summary = "Tenet re-checks what Lean built with an independent kernel. Build the project, then Verify.";

    /// <summary>A verification is running.</summary>
    [ObservableProperty]
    private bool _isRunning;

    /// <summary>How far the running verification is, from 0 to 100.</summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>Which module the running verification is on, and the counts.</summary>
    [ObservableProperty]
    private string _progressText = "";

    /// <summary>
    /// Which verdicts <see cref="Items"/> lists: 0 all, 1 those that need attention (not verified), 2 the verified.
    /// </summary>
    [ObservableProperty]
    private int _filter; // 0 all, 1 needs attention, 2 verified

    /// <summary>The last verification's report, or null before one has run.</summary>
    [ObservableProperty]
    private VerificationReport? _report;

    partial void OnFilterChanged(int value) => Apply();

    // ---- why a declaration is not fully proved ----

    /// <summary>The chains explaining why the last declaration asked about is not fully proved.</summary>
    public ObservableList<TrailView> Trails { get; } = new();

    /// <summary>
    /// The explanation's heading: why it is not fully proved, that it is, progress, or what to do first.
    /// </summary>
    [ObservableProperty]
    private string _trailTitle = "";

    /// <summary>The explanation is shown.</summary>
    [ObservableProperty]
    private bool _hasTrail;

    /// <summary>Set by the window's view model: find the chains for a declaration.</summary>
    public Func<string, Task>? Explain { get; set; }

    /// <summary>Opens a source file at a 1-based line.</summary>
    public event Action<string, int>? OpenRequested;

    /// <summary>Explain why a declaration rests on an assumption.</summary>
    /// <param name="v">The verdict; null does nothing.</param>
    [RelayCommand]
    private Task Why(VerdictView? v) => v is null || Explain is null ? Task.CompletedTask : Explain(v.Name);

    /// <summary>Open a step of a chain in the editor, when its source file is known.</summary>
    /// <param name="l">The step; null does nothing.</param>
    [RelayCommand]
    private void OpenLink(TrailLinkView? l)
    {
        if (l?.Link.SourceFile is string f)
        {
            OpenRequested?.Invoke(f, l.Link.Line ?? 1);
        }
    }

    /// <summary>Hide the explanation.</summary>
    [RelayCommand]
    private void CloseTrail()
    {
        HasTrail = false;
        Trails.Reset([]);
    }

    /// <summary>
    /// Show the chains explaining why a declaration is not fully proved, or that it is when there are none.
    /// </summary>
    /// <param name="name">The declaration's full name.</param>
    /// <param name="trails">One chain per sorry or axiom it rests on.</param>
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

    /// <summary>Show a verification's report: its verdicts, sorted and filtered, and a summary of the counts.</summary>
    /// <param name="r">The report.</param>
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
