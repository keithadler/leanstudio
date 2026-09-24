using System.Text;
using System.Text.Json;

namespace LeanStudio.Lsp;

/// <summary>
/// Text with a tree of tags, flattened: the plain string, plus the extent of every tagged subexpression in it.
/// Lean sends goals as <c>TaggedText</c> (<c>{text}</c>, <c>{append:[..]}</c>, <c>{tag:[info, child]}</c>) so a
/// client can hover and click into any subterm; the spans keep that structure without keeping the JSON.
/// </summary>
/// <param name="Text">The plain text.</param>
/// <param name="Spans">Every tagged subterm, in the order their tags close (a tag comes after the tags inside it), with offsets into <paramref name="Text"/>.</param>
public sealed record TaggedString(string Text, IReadOnlyList<TaggedSpan> Spans)
{
    /// <summary>No text and no spans.</summary>
    public static readonly TaggedString Empty = new("", []);

    /// <summary>The plain <see cref="Text"/>.</summary>
    public override string ToString() => Text;

    /// <summary>
    /// Flatten Lean's <c>TaggedText</c> JSON. Anything that is not an object, or not one of the three forms, contributes
    /// no text. The references in the spans stay held by the server until released (see <see cref="LeanServer.ReleaseAsync"/>).
    /// </summary>
    public static TaggedString Parse(JsonElement e)
    {
        var sb = new StringBuilder();
        var spans = new List<TaggedSpan>();
        Walk(e, sb, spans, 0);
        return new TaggedString(sb.ToString(), spans);
    }

    private static void Walk(JsonElement e, StringBuilder sb, List<TaggedSpan> spans, int depth)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        if (e.TryGetProperty("text", out JsonElement t))
        {
            sb.Append(t.GetString());
        }
        else if (e.TryGetProperty("append", out JsonElement a) && a.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in a.EnumerateArray())
            {
                Walk(child, sb, spans, depth);
            }
        }
        else if (e.TryGetProperty("tag", out JsonElement tag) && tag.ValueKind == JsonValueKind.Array && tag.GetArrayLength() == 2)
        {
            int start = sb.Length;
            JsonElement info = tag[0];
            Walk(tag[1], sb, spans, depth + 1);
            string? reference = info.TryGetProperty("info", out JsonElement i) && i.TryGetProperty("p", out JsonElement p)
                ? p.GetString()
                : null;
            string? diff = info.TryGetProperty("diffStatus", out JsonElement d) ? d.GetString() : null;
            spans.Add(new TaggedSpan(start, sb.Length - start, depth, reference, diff));
        }
    }
}

/// <summary>A tagged subterm: where it is in the flattened text, how deep, its RPC reference, and its diff status.</summary>
/// <param name="Start">The 0-based offset of the subterm in the flattened text, in UTF-16 code units.</param>
/// <param name="Length">Its length, in UTF-16 code units.</param>
/// <param name="Depth">How many tags enclose it: 0 for an outermost tag.</param>
/// <param name="Reference">
/// Lean's RPC reference to the subterm's info (the <c>p</c> of its <c>info</c>), for <see cref="LeanServer.InspectAsync"/>;
/// null when the tag has none.
/// </param>
/// <param name="DiffStatus">
/// How the subterm changed across the tactic, as Lean names it (such as <c>wasChanged</c>, <c>willChange</c>,
/// <c>wasInserted</c> or <c>willDelete</c>); null when unchanged.
/// </param>
public sealed record TaggedSpan(int Start, int Length, int Depth, string? Reference, string? DiffStatus);

/// <summary>A hypothesis in a goal's local context; several names share one entry when they have the same type.</summary>
/// <param name="Names">The names, as Lean shows them (inaccessible ones with a <c>✝</c>).</param>
/// <param name="Type">The type.</param>
/// <param name="Value">The value of a <c>let</c> variable, or null for an ordinary hypothesis.</param>
/// <param name="IsInstance">Whether it is a type class instance.</param>
/// <param name="IsType">Whether it is itself a type (such as <c>α : Type</c>).</param>
/// <param name="IsInserted">Whether the tactic added it (in the goals after the tactic).</param>
/// <param name="IsRemoved">Whether the tactic removes it (in the goals before the tactic).</param>
public sealed record InteractiveHypothesis(
    IReadOnlyList<string> Names,
    TaggedString Type,
    TaggedString? Value,
    bool IsInstance,
    bool IsType,
    bool IsInserted,
    bool IsRemoved);

/// <summary>One goal, with its local context, as Lean's interactive goal view has it.</summary>
/// <param name="UserName">The case name (as in <c>case succ</c>), or null for an anonymous goal.</param>
/// <param name="GoalPrefix">What goes before the target: <c>⊢ </c> unless Lean says otherwise.</param>
/// <param name="Hypotheses">The local context, in order.</param>
/// <param name="Type">The target to prove.</param>
/// <param name="MvarId">Lean's id of the goal's metavariable; empty when not given.</param>
/// <param name="IsInserted">Whether the tactic created this goal (in the goals after the tactic).</param>
/// <param name="IsRemoved">Whether the tactic closes this goal (in the goals before the tactic).</param>
public sealed record InteractiveGoal(
    string? UserName,
    string GoalPrefix,
    IReadOnlyList<InteractiveHypothesis> Hypotheses,
    TaggedString Type,
    string MvarId,
    bool IsInserted,
    bool IsRemoved)
{
    /// <summary>The goal as Lean prints it in plain text.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        if (UserName is not null)
        {
            sb.Append("case ").Append(UserName).Append('\n');
        }
        foreach (InteractiveHypothesis h in Hypotheses)
        {
            sb.Append(string.Join(' ', h.Names)).Append(" : ").Append(h.Type.Text);
            if (h.Value is not null)
            {
                sb.Append(" := ").Append(h.Value.Text);
            }
            sb.Append('\n');
        }
        sb.Append(GoalPrefix).Append(Type.Text);
        return sb.ToString();
    }
}

/// <summary>The goals at a position, from <c>Lean.Widget.getInteractiveGoals</c>.</summary>
/// <param name="Goals">The goals, first the main one; empty when there are none (or no tactic proof there).</param>
public sealed record InteractiveGoals(IReadOnlyList<InteractiveGoal> Goals)
{
    /// <summary>No goals.</summary>
    public static readonly InteractiveGoals None = new([]);

    /// <summary>
    /// Read the result of <c>getInteractiveGoals</c> (an object with a <c>goals</c> array). Anything else, including
    /// null, gives <see cref="None"/>.
    /// </summary>
    public static InteractiveGoals Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("goals", out JsonElement goals))
        {
            return None;
        }
        var list = new List<InteractiveGoal>();
        foreach (JsonElement g in goals.EnumerateArray())
        {
            var hyps = new List<InteractiveHypothesis>();
            if (g.TryGetProperty("hyps", out JsonElement hs))
            {
                foreach (JsonElement h in hs.EnumerateArray())
                {
                    hyps.Add(new InteractiveHypothesis(
                        h.TryGetProperty("names", out JsonElement n) ? n.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [],
                        h.TryGetProperty("type", out JsonElement ty) ? TaggedString.Parse(ty) : TaggedString.Empty,
                        h.TryGetProperty("val", out JsonElement v) ? TaggedString.Parse(v) : null,
                        Flag(h, "isInstance"),
                        Flag(h, "isType"),
                        Flag(h, "isInserted"),
                        Flag(h, "isRemoved")));
                }
            }
            list.Add(new InteractiveGoal(
                g.TryGetProperty("userName", out JsonElement u) ? u.GetString() : null,
                g.TryGetProperty("goalPrefix", out JsonElement gp) ? gp.GetString() ?? "⊢ " : "⊢ ",
                hyps,
                g.TryGetProperty("type", out JsonElement t) ? TaggedString.Parse(t) : TaggedString.Empty,
                g.TryGetProperty("mvarId", out JsonElement m) ? m.GetString() ?? "" : "",
                Flag(g, "isInserted"),
                Flag(g, "isRemoved")));
        }
        return new InteractiveGoals(list);
    }

    private static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement f) && f.ValueKind == JsonValueKind.True;

    /// <summary>Every RPC reference the goals hold, so they can be released once rendered.</summary>
    public IEnumerable<string> References() =>
        Goals.SelectMany(g => g.Hypotheses.SelectMany(h => h.Type.Spans.Concat(h.Value?.Spans ?? []))
                                .Concat(g.Type.Spans))
             .Select(s => s.Reference)
             .OfType<string>();
}

/// <summary>A subterm of a goal, as Lean describes it: written out in full, its type, and its documentation.</summary>
/// <param name="Explicit">The term printed explicitly (with its implicit arguments), or null.</param>
/// <param name="Type">Its type, or null.</param>
/// <param name="Doc">The docstring of its head constant as Markdown, or null when it has none.</param>
public sealed record SubtermInfo(string? Explicit, string? Type, string? Doc);

/// <summary>
/// What the Tactic State leaves out of each goal's local context, as VS Code's infoview can: hypotheses that are
/// types (<c>α : Type</c>), instances (<c>inst : Group G</c>), inaccessible names (<c>n✝</c>), and let values.
/// </summary>
/// <param name="HideTypes">Leave out hypotheses that are themselves types.</param>
/// <param name="HideInstances">Leave out type class instances.</param>
/// <param name="HideInaccessible">Leave out inaccessible names (those with a <c>✝</c>), and hypotheses left with none.</param>
/// <param name="HideLetValues">Show a let variable's type but not its value.</param>
public sealed record GoalFilter(bool HideTypes = false, bool HideInstances = false, bool HideInaccessible = false, bool HideLetValues = false)
{
    /// <summary>Leave everything in.</summary>
    public static readonly GoalFilter None = new();

    /// <summary>The goal with what this filter leaves out removed.</summary>
    public InteractiveGoal Apply(InteractiveGoal goal) => this == None ? goal : goal with
    {
        Hypotheses = goal.Hypotheses
            .Where(h => !(HideTypes && h.IsType) && !(HideInstances && h.IsInstance))
            .Select(h => HideInaccessible ? h with { Names = h.Names.Where(n => !n.Contains('✝', StringComparison.Ordinal)).ToList() } : h)
            .Where(h => h.Names.Count > 0)
            .Select(h => HideLetValues ? h with { Value = null } : h)
            .ToList(),
    };
}
