using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.Core.Proofs;
using LeanStudio.Lsp;

namespace LeanStudio.App.ViewModels;

/// <summary>One hypothesis of a goal, as the Tactic State panel shows it.</summary>
/// <param name="Names">
/// The names it binds, separated by spaces (several hypotheses of one type are shown together).
/// </param>
/// <param name="Type">Its type, as text.</param>
/// <param name="Value">Its value, for a local definition (<c>let</c>, <c>set</c>); null otherwise.</param>
/// <param name="IsInserted">Lean marks it as added by the tactic.</param>
/// <param name="IsRemoved">Lean marks it as removed by the tactic.</param>
/// <param name="IsInstance">It is a type class instance.</param>
public sealed record HypothesisView(string Names, string Type, string? Value, bool IsInserted, bool IsRemoved, bool IsInstance)
{
    /// <summary>The type with Lean's subterm structure, for hovering into it.</summary>
    public TaggedString? Tagged { get; init; }

    /// <summary>The hypothesis as Lean writes it: <c>h : type</c>, or <c>x : type := value</c>.</summary>
    public string Text => Value is null ? $"{Names} : {Type}" : $"{Names} : {Type} := {Value}";
    /// <summary><c>+</c> if it was added, <c>−</c> if it was removed, a space otherwise.</summary>
    public string Marker => IsInserted ? "+" : IsRemoved ? "−" : " ";
}

/// <summary>One goal in the Tactic State panel.</summary>
/// <param name="CaseName">The goal's case tag, such as <c>succ</c>, or null.</param>
/// <param name="Hypotheses">Its hypotheses, in order.</param>
/// <param name="Prefix">What Lean puts before the target, usually <c>⊢ </c>.</param>
/// <param name="Target">What is to be proved, as text.</param>
/// <param name="IsInserted">Lean marks the goal as added by the tactic.</param>
/// <param name="IsRemoved">Lean marks the goal as closed by the tactic.</param>
public sealed record GoalView(string? CaseName, IReadOnlyList<HypothesisView> Hypotheses, string Prefix, string Target, bool IsInserted, bool IsRemoved)
{
    /// <summary>The target with Lean's subterm structure, for hovering into it.</summary>
    public TaggedString? TargetTagged { get; init; }

    /// <summary>Show the target before the hypotheses (Preferences, or the Tactic State's ⋯ menu).</summary>
    public bool TargetFirst { get; init; }

    /// <summary>The row the hypotheses go in: after the target when it comes first.</summary>
    public int HypothesesRow => TargetFirst ? 1 : 0;

    /// <summary>The row the target goes in.</summary>
    public int TargetRow => TargetFirst ? 0 : 1;

    /// <summary>The goal has a case tag.</summary>
    public bool HasCase => CaseName is not null;
    /// <summary>The case tag, as <c>case name</c>.</summary>
    public string CaseLabel => "case " + CaseName;
    /// <summary>The target with its prefix, as Lean shows it.</summary>
    public string TargetText => Prefix + Target;

    /// <summary>The goal read aloud, for someone who does not read Lean yet.</summary>
    public string English => "In words: " + Core.Learn.PlainEnglish.Read(Target);

    /// <summary>A view of a goal from Lean's interactive goals, keeping the tagged text for hovering.</summary>
    /// <param name="g">The goal, as Lean's interactive goals give it.</param>
    public static GoalView From(InteractiveGoal g) => new(
        g.UserName,
        g.Hypotheses.Select(h => new HypothesisView(string.Join(' ', h.Names), h.Type.Text, h.Value?.Text, h.IsInserted, h.IsRemoved, h.IsInstance) { Tagged = h.Type }).ToList(),
        g.GoalPrefix,
        g.Type.Text,
        g.IsInserted,
        g.IsRemoved)
    { TargetTagged = g.Type };
}

/// <summary>A message at the cursor, with Lean's suggestions ("Try this: …") as actions that can be applied.</summary>
public sealed partial class MessageView : ObservableObject
{
    /// <summary>A message with no suggestions yet; <see cref="InfoViewModel"/> fetches them.</summary>
    /// <param name="diagnostic">Lean's message.</param>
    public MessageView(Diagnostic diagnostic) => Diagnostic = diagnostic;

    /// <summary>Lean's message, with its range and severity.</summary>
    public Diagnostic Diagnostic { get; }

    /// <summary>The message, starting with <c>error: </c> or <c>warning: </c> as appropriate.</summary>
    public string Text => (Diagnostic.Severity switch
    {
        DiagnosticSeverity.Error => "error: ",
        DiagnosticSeverity.Warning => "warning: ",
        _ => "",
    }) + (InteractiveText ?? Diagnostic.Message);

    /// <summary>The message's text from Lean's interactive form, which spells out what the plain one abbreviates.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text), nameof(HasText))]
    private string? _interactiveText;

    /// <summary>There is text to show beside the traces (a message that is only a trace has none).</summary>
    public bool HasText => Text.Length > 0;

    /// <summary>The trace trees in the message (from <c>set_option trace.… true</c>), to expand one level at a time.</summary>
    public ObservableList<TraceNodeView> Traces { get; } = new();

    /// <summary><see cref="Traces"/> has any.</summary>
    [ObservableProperty]
    private bool _hasTraces;

    /// <summary>The message's "Try this" code actions, each shown as a button that applies it.</summary>
    public ObservableList<CodeAction> Suggestions { get; } = new();

    /// <summary>What the message means, in plain words, when there is a known explanation.</summary>
    public string? Explanation { get; init; }

    /// <summary>There is a plain-words <see cref="Explanation"/>.</summary>
    public bool HasExplanation => Explanation is not null;

    /// <summary><see cref="Suggestions"/> has any.</summary>
    [ObservableProperty]
    private bool _hasSuggestions;
}

/// <summary>
/// A node of a trace tree in the Tactic State panel. Children Lean sends on request are fetched the first time the
/// node is expanded; until then it shows one placeholder child, so the tree offers to expand it.
/// </summary>
public sealed partial class TraceNodeView : ObservableObject
{
    private readonly TraceNode _node;
    private readonly Func<string, Task<IReadOnlyList<TraceNode>>>? _fetch;
    private bool _loaded;

    /// <summary>A view of a trace node.</summary>
    /// <param name="node">The node.</param>
    /// <param name="fetch">How to fetch children Lean sends on request.</param>
    public TraceNodeView(TraceNode node, Func<string, Task<IReadOnlyList<TraceNode>>>? fetch)
    {
        _node = node;
        _fetch = fetch;
        if (node.Children.Count > 0)
        {
            Children.Reset(node.Children.Select(c => new TraceNodeView(c, fetch)));
            _loaded = true;
        }
        else if (node.LazyChildren is not null)
        {
            Children.Add(Placeholder);
        }
        IsExpanded = !node.Collapsed && node.Children.Count > 0;
    }

    private TraceNodeView(string text)
    {
        _node = new TraceNode("", text, true, [], null);
        _loaded = true;
    }

    private static readonly TraceNodeView Placeholder = new("…");

    /// <summary>The trace class, as <c>[Meta.synthInstance]</c>; empty for the placeholder.</summary>
    public string Class => _node.Class.Length == 0 ? "" : "[" + _node.Class + "]";

    /// <summary>The node's message.</summary>
    public string Text => _node.Text;

    /// <summary>A success (✅) or failure (❌, 💥) mark leads the text, as Lean writes it.</summary>
    public bool Failed => _node.Text.StartsWith('❌') || _node.Text.StartsWith("💥", StringComparison.Ordinal);

    /// <summary>The node's children.</summary>
    public ObservableList<TraceNodeView> Children { get; } = new();

    /// <summary>Whether the tree shows the node open; opening it the first time fetches its children.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded && _node.LazyChildren is string lazy && _fetch is not null)
        {
            _loaded = true;
            _ = LoadAsync(lazy);
        }
    }

    private async Task LoadAsync(string lazy)
    {
        try
        {
            IReadOnlyList<TraceNode> children = await _fetch!(lazy);
            Children.Reset(children.Select(c => new TraceNodeView(c, _fetch)));
        }
        catch (Exception e) when (e is JsonRpcException or IOException or InvalidOperationException)
        {
            Children.Reset([new TraceNodeView("Lean could not send these: " + e.Message)]);
        }
    }
}

/// <summary>A goal state kept on screen while you work elsewhere, to compare against.</summary>
/// <param name="Where">Where the goals were, as <c>File.lean:line:column</c>.</param>
/// <param name="Text">The goals as plain text.</param>
public sealed record PinnedGoal(string Where, string Text);

/// <summary>
/// One tactic step of the proof at the cursor, in the Tactic State panel's step list, with the change it made to the
/// goals. Created with <see cref="Summary"/> <c>…</c>; <see cref="InfoViewModel"/> fills in the rest when Lean answers.
/// </summary>
public sealed partial class ProofStepView : ObservableObject
{
    /// <summary>A view of a step, before its effect is known.</summary>
    /// <param name="step">The step.</param>
    /// <param name="index">Its 0-based position in the proof.</param>
    public ProofStepView(ProofStep step, int index)
    {
        Step = step;
        Index = index;
    }

    /// <summary>The step: its text, its line, and the positions before and after it.</summary>
    public ProofStep Step { get; }
    /// <summary>The step's 0-based position in the proof.</summary>
    public int Index { get; }
    /// <summary>The step's 1-based number, for display.</summary>
    public string Number => (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>The step's source text.</summary>
    public string Text => Step.Text;
    /// <summary>The step's 0-based line.</summary>
    public int Line => Step.Line;

    /// <summary>What the step's tactic does, for the tooltip.</summary>
    public string? Explanation => Core.Learn.TacticGuide.TacticOf(Step.Text) is string t && Core.Learn.TacticGuide.Explain(t) is { } e
        ? $"{e.Name}: {e.Explanation}"
        : null;

    /// <summary>
    /// What the step changed, in words (or <c>continues below</c> for a tactic its next lines finish); <c>…</c> until
    /// Lean answers.
    /// </summary>
    [ObservableProperty]
    private string _summary = "…";

    /// <summary>How many goals are left after the step; -1 until Lean answers.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GoalsLabel))]
    private int _goalsAfter = -1;

    /// <summary>The goals left after the step, as <c>1 goal</c> or <c>N goals</c>; empty until known.</summary>
    public string GoalsLabel => GoalsAfter < 0 ? "" : GoalsAfter == 1 ? "1 goal" : $"{GoalsAfter} goals";

    /// <summary>The caret is on this step's line.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>The step closed every goal: the proof is done there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsGoalCount))]
    private bool _closesAll;

    /// <summary>The step "closes" the goal with <c>sorry</c> or <c>admit</c>: nothing is proved, only put off.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsGoalCount))]
    private bool _closedBySorry;

    /// <summary>Show how many goals are left (not when the step finishes the proof, or puts it off with sorry).</summary>
    public bool ShowsGoalCount => !ClosesAll && !ClosedBySorry;

    /// <summary>Lean reports an error on the step's line.</summary>
    [ObservableProperty]
    private bool _hasError;

    /// <summary>The goals after the step, or null until Lean answers.</summary>
    public InteractiveGoals? After { get; set; }
}

/// <summary>
/// The tactic state panel: goals and hypotheses at the cursor (with what the last tactic added or removed), the
/// expected type of the term under the cursor, the messages there, and the whole proof as a list of steps each
/// with the change it made.
/// </summary>
/// <remarks>
/// <see cref="MainViewModel"/> calls <see cref="RefreshAsync"/> each time the caret settles in a Lean file, with the
/// running <see cref="LeanServer"/>; everything shown comes from Lean's interactive goals, term goal and code actions
/// at that position, and from the document's diagnostics. Used on the UI thread.
/// </remarks>
public sealed partial class InfoViewModel : ObservableObject
{
    private CancellationTokenSource? _goalsCts;
    private (LeanServer Server, string Uri, Position Pos, List<string> Refs)? _held;

    /// <summary>What Lean says about a subterm of the goals on screen (its type, written out, and its docs).</summary>
    /// <param name="reference">The subterm's reference, from the tagged text of a goal on screen.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>What Lean says, or null when no goals are shown or Lean does not answer.</returns>
    public async Task<SubtermInfo?> InspectAsync(string reference, CancellationToken ct = default)
    {
        if (_held is not { } h)
        {
            return null;
        }
        try
        {
            return await h.Server.InspectAsync(h.Uri, h.Pos, reference, ct);
        }
        catch (Exception e) when (e is JsonRpcException or IOException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Hand the previous goals' subterm references back to Lean, as they are no longer shown.</summary>
    private void ReleaseHeld()
    {
        if (_held is { } h && h.Refs.Count > 0)
        {
            _ = h.Server.ReleaseAsync(h.Uri, h.Refs);
        }
        _held = null;
    }
    private CancellationTokenSource? _stepsCts;
    private string? _stepsKey;

    /// <summary>The Prove It card: tactics tried against a sorry.</summary>
    public ProofSearchViewModel Search { get; } = new();

    /// <summary>The goals at the cursor, from the last refresh.</summary>
    public ObservableList<GoalView> Goals { get; } = new();
    /// <summary>Lean's messages on the cursor's line.</summary>
    public ObservableList<MessageView> Messages { get; } = new();

    /// <summary>Goal states pinned with the Pin command, oldest first.</summary>
    public ObservableList<PinnedGoal> Pinned { get; } = new();

    /// <summary><see cref="Pinned"/> has any.</summary>
    [ObservableProperty]
    private bool _hasPinned;

    /// <summary>Keep the current goals on screen, to compare with the goals after a change.</summary>
    [RelayCommand]
    private void Pin()
    {
        if (PlainGoals.Length > 0)
        {
            Pinned.Add(new PinnedGoal(Position, PlainGoals));
            HasPinned = true;
        }
    }

    /// <summary>Remove a pinned goal state.</summary>
    /// <param name="g">The pinned state; null does nothing.</param>
    [RelayCommand]
    private void Unpin(PinnedGoal? g)
    {
        if (g is not null)
        {
            Pinned.Remove(g);
            HasPinned = Pinned.Count > 0;
        }
    }

    /// <summary>Raised when the person clicks a suggestion, for the window to apply its edit.</summary>
    public event Action<CodeAction>? ApplyRequested;

    /// <summary>Apply a suggestion from a message, by raising <see cref="ApplyRequested"/>.</summary>
    /// <param name="action">The suggestion; null does nothing.</param>
    [RelayCommand]
    private void ApplySuggestion(CodeAction? action)
    {
        if (action is not null)
        {
            ApplyRequested?.Invoke(action);
        }
    }
    /// <summary>The steps of the tactic proof at the cursor; empty outside a proof.</summary>
    public ObservableList<ProofStepView> Steps { get; } = new();

    /// <summary>Where the state shown is, as <c>File.lean:line:column</c> (1-based).</summary>
    [ObservableProperty]
    private string _position = "";

    /// <summary>
    /// A line about the state: the goal count, <c>No goals</c>, <c>Lean is elaborating…</c>, or why there is nothing.
    /// </summary>
    [ObservableProperty]
    private string _status = "No file open";

    /// <summary>The type expected of the term at the cursor, or null.</summary>
    [ObservableProperty]
    private string? _expectedType;

    /// <summary>The declaration whose tactic proof the cursor is in; empty outside a proof.</summary>
    [ObservableProperty]
    private string _proofName = "";

    /// <summary>There are goals at the cursor.</summary>
    [ObservableProperty]
    private bool _hasGoals;

    /// <summary>The cursor is inside a tactic proof.</summary>
    [ObservableProperty]
    private bool _inProof;

    /// <summary><see cref="Steps"/> has any.</summary>
    [ObservableProperty]
    private bool _hasSteps;

    /// <summary><see cref="Messages"/> has any.</summary>
    [ObservableProperty]
    private bool _hasMessages;

    /// <summary>There is an expected type at the cursor.</summary>
    [ObservableProperty]
    private bool _hasExpectedType;

    /// <summary>
    /// How many user widgets Lean shows at the cursor (from <c>#widget</c>, ProofWidgets and the like). This panel
    /// can't draw them; <see cref="OpenWidgetsCommand"/> opens Lean's own infoview, which can.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWidgets), nameof(WidgetsLabel))]
    private int _widgetCount;

    /// <summary>There is at least one user widget at the cursor.</summary>
    public bool HasWidgets => WidgetCount > 0;

    /// <summary>The widget chip's text.</summary>
    public string WidgetsLabel => WidgetCount == 1 ? "Widget" : $"{WidgetCount} widgets";

    /// <summary>What <see cref="OpenWidgetsCommand"/> does: set by the main window's view model.</summary>
    public Action? ShowWidgets { get; set; }

    /// <summary>Show the widgets at the cursor, in Lean's own infoview.</summary>
    [RelayCommand]
    private void OpenWidgets() => ShowWidgets?.Invoke();

    /// <summary>Show the plain-text rendering Lean would put in a hover, instead of the structured view.</summary>
    [ObservableProperty]
    private bool _plainText;

    /// <summary>All the goals as plain text, as Lean renders them, separated by blank lines.</summary>
    [ObservableProperty]
    private string _plainGoals = "";

    /// <summary>Show each goal read aloud in English under it.</summary>
    [ObservableProperty]
    private bool _showEnglish = true;

    /// <summary>Explain messages in plain words.</summary>
    public bool ExplainErrors { get; set; } = true;

    /// <summary>Raised with a 0-based line and column to move the editor's caret to (after a proof step).</summary>
    public event Action<int, int>? NavigateRequested;

    /// <summary>Move the caret to the end of a proof step, so the panel shows the state after it.</summary>
    /// <param name="step">The step; null does nothing.</param>
    [RelayCommand]
    private void GoToStep(ProofStepView? step)
    {
        if (step is not null)
        {
            NavigateRequested?.Invoke(step.Step.After.Line, step.Step.After.Character);
        }
    }

    // ---- what the goals show, as VS Code's infoview lets you choose ----

    private InteractiveGoals? _shown;

    /// <summary>Leave hypotheses that are types (<c>α : Type</c>) out of the goals.</summary>
    [ObservableProperty]
    private bool _hideTypes;

    /// <summary>Leave type class instances out of the goals.</summary>
    [ObservableProperty]
    private bool _hideInstances;

    /// <summary>Leave inaccessible names (<c>n✝</c>) out of the goals.</summary>
    [ObservableProperty]
    private bool _hideInaccessible;

    /// <summary>Show let variables without their values.</summary>
    [ObservableProperty]
    private bool _hideLetValues;

    /// <summary>Show each goal's target before its hypotheses.</summary>
    [ObservableProperty]
    private bool _targetFirst;

    /// <summary>
    /// Keep showing the state where it was when paused, whatever the cursor does, to compare it with the code
    /// elsewhere.
    /// </summary>
    [ObservableProperty]
    private bool _paused;

    /// <summary>What <see cref="CopyGoalsCommand"/> does: set by the main window's view model.</summary>
    public Action? CopyGoalsRequested { get; set; }

    /// <summary>What <see cref="GoalsToCommentCommand"/> does: set by the main window's view model.</summary>
    public Action? GoalsToCommentRequested { get; set; }

    /// <summary>Copy the goals shown, as Lean prints them.</summary>
    [RelayCommand]
    private void CopyGoals() => CopyGoalsRequested?.Invoke();

    /// <summary>Put the goals shown into the file, as a comment above the cursor (VS Code's Copy Contents to Comment).</summary>
    [RelayCommand]
    private void GoalsToComment() => GoalsToCommentRequested?.Invoke();

    /// <summary>Stop pausing: the Tactic State follows the cursor again.</summary>
    [RelayCommand]
    private void Resume() => Paused = false;

    /// <summary>What the filters leave out.</summary>
    public GoalFilter Filter => new(HideTypes, HideInstances, HideInaccessible, HideLetValues);

    partial void OnHideTypesChanged(bool value) => ShowGoals();
    partial void OnHideInstancesChanged(bool value) => ShowGoals();
    partial void OnHideInaccessibleChanged(bool value) => ShowGoals();
    partial void OnHideLetValuesChanged(bool value) => ShowGoals();
    partial void OnTargetFirstChanged(bool value) => ShowGoals();

    /// <summary>Show the goals last fetched, with the filters and the order chosen now.</summary>
    private void ShowGoals()
    {
        if (_shown is not InteractiveGoals goals)
        {
            return;
        }
        GoalFilter filter = Filter;
        var shown = goals.Goals.Select(filter.Apply).ToList();
        Goals.Reset(shown.Select(g => GoalView.From(g) with { TargetFirst = TargetFirst }));
        PlainGoals = string.Join("\n\n", shown.Select(g => g.Render()));
    }

    /// <summary>Empty the panel and cancel any request still waiting, showing why there is nothing.</summary>
    /// <param name="status">What to show instead, such as <c>No file open</c>.</param>
    public void Clear(string status)
    {
        _goalsCts?.Cancel();
        _stepsCts?.Cancel();
        _stepsKey = null;
        _shown = null;
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
    /// <remarks>
    /// Messages on the line are shown at once; goals and the expected type follow when Lean answers, and the proof's
    /// steps after that (only when the proof has changed). Lean's errors are shown in <see cref="Status"/>, not thrown.
    /// The subterm references of the goals shown are kept for <see cref="InspectAsync"/> and released when replaced.
    /// </remarks>
    /// <param name="server">The running Lean server.</param>
    /// <param name="doc">The document the cursor is in.</param>
    /// <param name="pos">The cursor, 0-based.</param>
    public async Task RefreshAsync(LeanServer server, DocumentViewModel doc, Position pos)
    {
        if (Paused)
        {
            return; // keep showing the state where it was paused
        }
        _goalsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _goalsCts = cts;
        Position = $"{System.IO.Path.GetFileName(doc.Path)}:{pos.Line + 1}:{pos.Character + 1}";

        // Messages on this line need no round trip.
        var msgs = doc.Diagnostics
            .Where(d => d.Extent.Start.Line <= pos.Line && pos.Line <= d.Extent.End.Line)
            .Select(d => new MessageView(d) { Explanation = ExplainErrors ? Core.Learn.ErrorGuide.Explain(d.Message) : null })
            .ToList();
        Messages.Reset(msgs);
        HasMessages = msgs.Count > 0;
        _ = LoadSuggestionsAsync(server, doc, msgs, cts.Token);
        _ = LoadTracesAsync(server, doc, msgs, pos, cts.Token);
        _ = LoadWidgetsAsync(server, doc, pos, cts.Token);

        TacticProof? proof = ProofSteps.Find(doc.Lines(), pos.Line);
        InProof = proof is not null;
        ProofName = proof?.Declaration ?? "";
        UpdateCurrentStep(proof, pos);

        Status = doc.IsProcessing ? "Lean is elaborating…" : "";
        try
        {
            Task<InteractiveGoals> goalsTask = server.InteractiveGoalsAsync(doc.Uri, pos, cts.Token, keepReferences: true);
            Task<InteractiveGoals> termTask = server.InteractiveTermGoalAsync(doc.Uri, pos, cts.Token);
            InteractiveGoals goals = await goalsTask;
            if (cts.IsCancellationRequested)
            {
                _ = server.ReleaseAsync(doc.Uri, goals.References().ToList());
                return;
            }
            ReleaseHeld();
            _held = (server, doc.Uri, pos, goals.References().ToList());
            _shown = goals;
            ShowGoals();
            HasGoals = goals.Goals.Count > 0;
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
        else if (proof is null)
        {
            // Out of any proof: last proof's steps would only mislead.
            _stepsCts?.Cancel();
            _stepsKey = null;
            Steps.Reset([]);
            HasSteps = false;
        }
    }

    /// <summary>Ask Lean which user widgets it shows at the cursor, for the widget chip.</summary>
    private async Task LoadWidgetsAsync(LeanServer server, DocumentViewModel doc, Position pos, CancellationToken ct)
    {
        try
        {
            int n = (await server.WidgetsAtAsync(doc.Uri, pos, ct)).Count;
            if (!ct.IsCancellationRequested)
            {
                WidgetCount = n;
            }
        }
        catch (Exception e) when (e is JsonRpcException or OperationCanceledException or IOException or InvalidOperationException)
        {
            if (!ct.IsCancellationRequested)
            {
                WidgetCount = 0;
            }
        }
    }

    /// <summary>For messages that offer "Try this", fetch the matching code actions so each can be applied with a click.</summary>
    /// <summary>
    /// For messages that carry traces (their plain text is just "(trace)"), ask Lean for the interactive form: the
    /// text in full and each trace as a tree.
    /// </summary>
    private static async Task LoadTracesAsync(LeanServer server, DocumentViewModel doc, List<MessageView> msgs, Position pos, CancellationToken ct)
    {
        var traced = msgs.Where(m => m.Diagnostic.Message.Contains("(trace)", StringComparison.Ordinal)).ToList();
        if (traced.Count == 0)
        {
            return;
        }
        try
        {
            int start = traced.Min(m => m.Diagnostic.Range.Start.Line), end = traced.Max(m => m.Diagnostic.Range.End.Line) + 1;
            IReadOnlyList<InteractiveMessage> interactive = await server.InteractiveMessagesAsync(doc.Uri, start, end, ct);
            Task<IReadOnlyList<TraceNode>> Fetch(string lazy) => server.TraceChildrenAsync(doc.Uri, pos, lazy, CancellationToken.None);
            foreach (MessageView m in traced)
            {
                InteractiveMessage? match = interactive.FirstOrDefault(i => i.Range.Start == m.Diagnostic.Range.Start && i.Traces.Count > 0);
                if (match is null)
                {
                    continue;
                }
                m.InteractiveText = match.Text;
                m.Traces.Reset(match.Traces.Select(t => new TraceNodeView(t, Fetch)));
                m.HasTraces = true;
            }
        }
        catch (Exception e) when (e is JsonRpcException or IOException or OperationCanceledException)
        {
        }
    }

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
                // sorry and admit "close" the goal only by putting it off: say so, rather than "goals accomplished".
                bool bySorry = change.GoalsBefore > change.GoalsAfter
                    && System.Text.RegularExpressions.Regex.IsMatch(v.Step.Text, @"(?<![\w.])(sorry|admit)(?![\w'])");
                v.Summary = bySorry ? "put off with sorry" : continues && change.Summary == "no change" ? "continues below" : change.Summary;
                v.ClosesAll = change.ClosedAll && !bySorry;
                v.ClosedBySorry = bySorry;
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
