using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Proofs;
using LeanStudio.Lsp;

namespace LeanStudio.App.ViewModels;

public sealed record HypothesisView(string Names, string Type, string? Value, bool IsInserted, bool IsRemoved, bool IsInstance)
{
    public string Text => Value is null ? $"{Names} : {Type}" : $"{Names} : {Type} := {Value}";
    public string Marker => IsInserted ? "+" : IsRemoved ? "−" : " ";
}

public sealed record GoalView(string? CaseName, IReadOnlyList<HypothesisView> Hypotheses, string Prefix, string Target, bool IsInserted, bool IsRemoved)
{
    public bool HasCase => CaseName is not null;
    public string CaseLabel => "case " + CaseName;
    public string TargetText => Prefix + Target;

    public static GoalView From(InteractiveGoal g) => new(
        g.UserName,
        g.Hypotheses.Select(h => new HypothesisView(string.Join(' ', h.Names), h.Type.Text, h.Value?.Text, h.IsInserted, h.IsRemoved, h.IsInstance)).ToList(),
        g.GoalPrefix,
        g.Type.Text,
        g.IsInserted,
        g.IsRemoved);
}

/// <summary>A message at the cursor, with Lean's suggestions ("Try this: …") as actions that can be applied.</summary>
public sealed partial class MessageView : ObservableObject
{
    public MessageView(Diagnostic diagnostic) => Diagnostic = diagnostic;

    public Diagnostic Diagnostic { get; }

    public string Text => (Diagnostic.Severity switch
    {
        DiagnosticSeverity.Error => "error: ",
        DiagnosticSeverity.Warning => "warning: ",
        _ => "",
    }) + Diagnostic.Message;

    public ObservableList<CodeAction> Suggestions { get; } = new();

    [ObservableProperty]
    private bool _hasSuggestions;
}

public sealed partial class ProofStepView : ObservableObject
{
    public ProofStepView(ProofStep step, int index)
    {
        Step = step;
        Index = index;
    }

    public ProofStep Step { get; }
    public int Index { get; }
    public string Number => (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string Text => Step.Text;
    public int Line => Step.Line;

    [ObservableProperty]
    private string _summary = "…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GoalsLabel))]
    private int _goalsAfter = -1;

    public string GoalsLabel => GoalsAfter < 0 ? "" : GoalsAfter == 1 ? "1 goal" : $"{GoalsAfter} goals";

    [ObservableProperty]
    private bool _isCurrent;

    [ObservableProperty]
    private bool _closesAll;

    [ObservableProperty]
    private bool _hasError;

    public InteractiveGoals? After { get; set; }
}

/// <summary>
/// The tactic state panel: goals and hypotheses at the cursor (with what the last tactic added or removed), the
/// expected type of the term under the cursor, the messages there, and the whole proof as a list of steps each
/// with the change it made.
/// </summary>
public sealed partial class InfoViewModel : ObservableObject
{
    private CancellationTokenSource? _goalsCts;
    private CancellationTokenSource? _stepsCts;
    private string? _stepsKey;

    public ObservableList<GoalView> Goals { get; } = new();
    public ObservableList<MessageView> Messages { get; } = new();

    /// <summary>Raised when the person clicks a suggestion, for the window to apply its edit.</summary>
    public event Action<CodeAction>? ApplyRequested;

    [RelayCommand]
    private void ApplySuggestion(CodeAction? action)
    {
        if (action is not null)
        {
            ApplyRequested?.Invoke(action);
        }
    }
    public ObservableList<ProofStepView> Steps { get; } = new();

    [ObservableProperty]
    private string _position = "";

    [ObservableProperty]
    private string _status = "No file open";

    [ObservableProperty]
    private string? _expectedType;

    [ObservableProperty]
    private string _proofName = "";

    [ObservableProperty]
    private bool _hasGoals;

    [ObservableProperty]
    private bool _inProof;

    [ObservableProperty]
    private bool _hasSteps;

    [ObservableProperty]
    private bool _hasMessages;

    [ObservableProperty]
    private bool _hasExpectedType;

    /// <summary>Show the plain-text rendering Lean would put in a hover, instead of the structured view.</summary>
    [ObservableProperty]
    private bool _plainText;

    [ObservableProperty]
    private string _plainGoals = "";

    public event Action<int, int>? NavigateRequested;

    [RelayCommand]
    private void GoToStep(ProofStepView? step)
    {
        if (step is not null)
        {
            NavigateRequested?.Invoke(step.Step.After.Line, step.Step.After.Character);
        }
    }

    public void Clear(string status)
    {
        _goalsCts?.Cancel();
        _stepsCts?.Cancel();
        _stepsKey = null;
        Goals.Reset([]);
        Messages.Reset([]);
        Steps.Reset([]);
        HasGoals = InProof = HasSteps = HasMessages = HasExpectedType = false;
        ExpectedType = null;
        PlainGoals = "";
        Position = "";
        ProofName = "";
        Status = status;
    }

    /// <summary>Ask Lean for everything at the cursor. A newer call cancels an older one still waiting.</summary>
    public async Task RefreshAsync(LeanServer server, DocumentViewModel doc, Position pos)
    {
        _goalsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _goalsCts = cts;
        Position = $"{System.IO.Path.GetFileName(doc.Path)}:{pos.Line + 1}:{pos.Character + 1}";

        // Messages on this line need no round trip.
        var msgs = doc.Diagnostics
            .Where(d => d.Extent.Start.Line <= pos.Line && pos.Line <= d.Extent.End.Line)
            .Select(d => new MessageView(d))
            .ToList();
        Messages.Reset(msgs);
        HasMessages = msgs.Count > 0;
        _ = LoadSuggestionsAsync(server, doc, msgs, cts.Token);

        TacticProof? proof = ProofSteps.Find(doc.Lines(), pos.Line);
        InProof = proof is not null;
        ProofName = proof?.Declaration ?? "";
        UpdateCurrentStep(proof, pos);

        Status = doc.IsProcessing ? "Lean is elaborating…" : "";
        try
        {
            Task<InteractiveGoals> goalsTask = server.InteractiveGoalsAsync(doc.Uri, pos, cts.Token);
            Task<InteractiveGoals> termTask = server.InteractiveTermGoalAsync(doc.Uri, pos, cts.Token);
            InteractiveGoals goals = await goalsTask;
            if (cts.IsCancellationRequested)
            {
                return;
            }
            Goals.Reset(goals.Goals.Select(GoalView.From));
            HasGoals = goals.Goals.Count > 0;
            PlainGoals = string.Join("\n\n", goals.Goals.Select(g => g.Render()));
            Status = HasGoals ? $"{goals.Goals.Count} goal{(goals.Goals.Count == 1 ? "" : "s")}"
                   : InProof ? "No goals" : doc.IsProcessing ? "Lean is elaborating…" : "";
            try
            {
                InteractiveGoals term = await termTask;
                if (!cts.IsCancellationRequested)
                {
                    InteractiveGoal? t = term.Goals.FirstOrDefault();
                    ExpectedType = t?.Type.Text;
                    HasExpectedType = t is not null;
                }
            }
            catch (JsonRpcException)
            {
                HasExpectedType = false;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (JsonRpcException e) when (e.Code == LeanServer.ContentModified)
        {
        }
        catch (JsonRpcException e)
        {
            if (!cts.IsCancellationRequested)
            {
                Status = e.Message;
            }
        }
        catch (IOException)
        {
            Status = "The Lean server is not running";
        }

        if (proof is not null && !cts.IsCancellationRequested)
        {
            await RefreshStepsAsync(server, doc, proof);
        }
    }

    /// <summary>For messages that offer "Try this", fetch the matching code actions so each can be applied with a click.</summary>
    private static async Task LoadSuggestionsAsync(LeanServer server, DocumentViewModel doc, List<MessageView> msgs, CancellationToken ct)
    {
        foreach (MessageView m in msgs.Where(m => m.Diagnostic.Message.Contains("Try this", StringComparison.Ordinal)))
        {
            try
            {
                IReadOnlyList<CodeAction> actions = await server.CodeActionsAsync(doc.Uri, m.Diagnostic.Range, [m.Diagnostic], ct);
                m.Suggestions.Reset(actions.Where(a => a.Title.StartsWith("Try this", StringComparison.Ordinal)));
                m.HasSuggestions = m.Suggestions.Count > 0;
            }
            catch (Exception e) when (e is JsonRpcException or IOException or OperationCanceledException)
            {
            }
        }
    }

    private void UpdateCurrentStep(TacticProof? proof, Position pos)
    {
        foreach (ProofStepView s in Steps)
        {
            s.IsCurrent = proof is not null && s.Step.Line == pos.Line;
        }
    }

    /// <summary>
    /// Compute the state after every step of the proof, and what each step changed. Done once per version of the
    /// proof: moving the cursor within an unchanged proof only moves the highlight.
    /// </summary>
    private async Task RefreshStepsAsync(LeanServer server, DocumentViewModel doc, TacticProof proof)
    {
        string key = doc.Uri + "|" + proof.DeclarationLine + "|" + string.Join("\n", proof.Steps.Select(s => s.Line + ":" + s.Text)) + "|" + doc.IsProcessing;
        if (key == _stepsKey)
        {
            return;
        }
        _stepsKey = key;
        _stepsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _stepsCts = cts;

        var views = proof.Steps.Select((s, i) => new ProofStepView(s, i) { IsCurrent = s.Line == doc.CaretLine }).ToList();
        Steps.Reset(views);
        HasSteps = views.Count > 0;
        try
        {
            // Each step is compared with the state at its own start, not with the previous line's end: in a case
            // split the previous line closed a different branch, and its "after" says nothing about this one.
            foreach (ProofStepView v in views)
            {
                if (cts.IsCancellationRequested)
                {
                    return;
                }
                InteractiveGoals before = await server.InteractiveGoalsAsync(doc.Uri, v.Step.Before, cts.Token);
                InteractiveGoals after = await server.InteractiveGoalsAsync(doc.Uri, v.Step.After, cts.Token);
                StepChange change = StepChange.Between(before, after);
                v.After = after;
                v.GoalsAfter = after.Goals.Count;
                // A line like `cases h with` or `calc` opens a tactic that the following, deeper lines finish;
                // the end of its first line is not the end of the tactic, so there is no "after" to compare yet.
                bool continues = ProofSteps.ContinuesBelow(proof.Steps, v.Index);
                v.Summary = continues && change.Summary == "no change" ? "continues below" : change.Summary;
                v.ClosesAll = change.ClosedAll;
                v.HasError = doc.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error && d.Extent.Start.Line <= v.Line && v.Line <= d.Extent.End.Line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (JsonRpcException)
        {
            _stepsKey = null;
        }
        catch (IOException)
        {
            _stepsKey = null;
        }
    }

    /// <summary>Forget the cached proof steps, e.g. after the file finishes elaborating.</summary>
    public void InvalidateSteps() => _stepsKey = null;
}
