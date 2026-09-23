using System.Text;
using System.Text.Json;

namespace LeanStudio.Lsp;

/// <summary>
/// Text with a tree of tags, flattened: the plain string, plus the extent of every tagged subexpression in it.
/// Lean sends goals as <c>TaggedText</c> (<c>{text}</c>, <c>{append:[..]}</c>, <c>{tag:[info, child]}</c>) so a
/// client can hover and click into any subterm; the spans keep that structure without keeping the JSON.
/// </summary>
public sealed record TaggedString(string Text, IReadOnlyList<TaggedSpan> Spans)
{
    public static readonly TaggedString Empty = new("", []);

    public override string ToString() => Text;

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
public sealed record TaggedSpan(int Start, int Length, int Depth, string? Reference, string? DiffStatus);

public sealed record InteractiveHypothesis(
    IReadOnlyList<string> Names,
    TaggedString Type,
    TaggedString? Value,
    bool IsInstance,
    bool IsType,
    bool IsInserted,
    bool IsRemoved);

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

public sealed record InteractiveGoals(IReadOnlyList<InteractiveGoal> Goals)
{
    public static readonly InteractiveGoals None = new([]);

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
