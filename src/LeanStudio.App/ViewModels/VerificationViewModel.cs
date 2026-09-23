using CommunityToolkit.Mvvm.ComponentModel;
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
