using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LeanStudio.Lsp;

namespace LeanStudio.Core.Proofs;

/// <summary>A hypothesis as Lean shows it: the names it binds and their type.</summary>
public sealed record StateHypothesis(IReadOnlyList<string> Names, string Type);

/// <summary>One goal: its hypotheses and what is left to prove.</summary>
public sealed record StateGoal(IReadOnlyList<StateHypothesis> Hypotheses, string Target)
{
    /// <summary>A goal from the language server's interactive goals.</summary>
    public static StateGoal From(InteractiveGoal g) =>
        new(g.Hypotheses.Select(h => new StateHypothesis(h.Names, h.Type.Text)).ToList(), g.Type.Text);

    /// <summary>The goal the way the infoview shows it.</summary>
    public string Render() =>
        string.Concat(Hypotheses.Select(h => string.Join(' ', h.Names) + " : " + h.Type + "\n")) + "⊢ " + Target;
}

/// <summary>
/// One tactic step of one proof: where it is, the goals before it, and the goals after it (null when the tactic's
/// block carries on in the lines below, where the state at the end of its line means nothing).
/// </summary>
public sealed record StateStep(string File, string Declaration, int Line, int Column, string Tactic,
                               IReadOnlyList<StateGoal> Before, IReadOnlyList<StateGoal>? After);

/// <summary>How strictly two proof states have to agree to count as the same one.</summary>
public enum StateMatch
{
    /// <summary>Same hypotheses and same goals, up to renaming the local names.</summary>
    Exact,

    /// <summary>Same goals, whatever the hypotheses.</summary>
    Goal,

    /// <summary>Same goals with local names and numbers blanked out.</summary>
    Shape,
}

/// <summary>A place a proof passes through a state: the tactic about to run there.</summary>
public sealed record StateVisit(string File, string Declaration, int Line, int Column, string Tactic);

/// <summary>A proof state, merged across every proof that reaches it.</summary>
/// <param name="Id">Its index in <see cref="ProofStateMap.Nodes"/>.</param>
/// <param name="Key">What was compared, after renaming and blanking.</param>
/// <param name="Example">The goals as Lean printed them the first time the state was reached.</param>
/// <param name="Declarations">Every proof that reaches it, as <c>file · declaration</c>, in order of first visit.</param>
/// <param name="Visits">Where a tactic runs from this state, for jumping to it and extracting it.</param>
/// <param name="Done">The state with no goals left: where every finished proof ends.</param>
/// <param name="Trivial">A goal nobody would make a lemma of: <c>False</c>, <c>True</c>, <c>a = a</c>, or numbers only.</param>
public sealed record StateNode(int Id, string Key, string Example, IReadOnlyList<string> Declarations,
                               IReadOnlyList<StateVisit> Visits, bool Done, bool Trivial);

/// <summary>A tactic step between two states, for one proof.</summary>
public sealed record StateEdge(int From, int To, string Declaration, string Tactic);

/// <summary>
/// The proof-state map of a set of tactic proofs: one node per distinct state, one edge per tactic step. Where the
/// proofs of different declarations reach the same state they share a node, and a state that several proofs pass
/// through is a lemma nobody has written yet. How strictly "the same" is read is <see cref="StateMatch"/>.
///
/// Built from what Lean reports at each tactic (see <see cref="ProofStates.CollectAsync"/>); nothing here needs Lean.
/// </summary>
public sealed class ProofStateMap
{
    private ProofStateMap(StateMatch match, IReadOnlyList<StateNode> nodes, IReadOnlyList<StateEdge> edges)
    {
        Match = match;
        Nodes = nodes;
        Edges = edges;
        Shared = nodes.Where(n => !n.Done && !n.Trivial && n.Declarations.Count > 1)
                      .OrderByDescending(n => n.Declarations.Count).ThenBy(n => n.Id).ToList();
    }

    /// <summary>How strictly states were compared.</summary>
    public StateMatch Match { get; }

    /// <summary>Every distinct state.</summary>
    public IReadOnlyList<StateNode> Nodes { get; }

    /// <summary>Every step between two states; the same step taken by two proofs is two edges.</summary>
    public IReadOnlyList<StateEdge> Edges { get; }

    /// <summary>The states worth a look: reached by two or more proofs, not trivial, most shared first.</summary>
    public IReadOnlyList<StateNode> Shared { get; }

    /// <summary>Merge the states of <paramref name="steps"/>, compared as <paramref name="match"/> says.</summary>
    public static ProofStateMap Build(IEnumerable<StateStep> steps, StateMatch match)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var keys = new List<string>();
        var examples = new List<string>();
        var decls = new List<List<string>>();
        var visits = new List<List<StateVisit>>();
        var trivial = new List<bool>();
        var edges = new List<StateEdge>();

        int NodeOf(IReadOnlyList<StateGoal> goals, string decl)
        {
            string key = ProofStates.Key(goals, match);
            if (!index.TryGetValue(key, out int id))
            {
                id = keys.Count;
                index[key] = id;
                keys.Add(key);
                examples.Add(goals.Count == 0 ? "no goals" : string.Join("\n\n", goals.Select(g => g.Render())));
                decls.Add([]);
                visits.Add([]);
                trivial.Add(goals.Count > 0 && goals.All(ProofStates.IsTrivial));
            }
            if (!decls[id].Contains(decl))
            {
                decls[id].Add(decl);
            }
            return id;
        }

        foreach (StateStep s in steps)
        {
            string decl = s.File + " · " + s.Declaration;
            int from = NodeOf(s.Before, decl);
            visits[from].Add(new StateVisit(s.File, s.Declaration, s.Line, s.Column, s.Tactic));
            if (s.After is not null)
            {
                int to = NodeOf(s.After, decl);
                if (to != from)
                {
                    edges.Add(new StateEdge(from, to, decl, s.Tactic));
                }
            }
        }
        var nodes = keys.Select((k, i) => new StateNode(i, k, examples[i], decls[i], visits[i], k == ProofStates.NoGoals, trivial[i])).ToList();
        return new ProofStateMap(match, nodes, edges);
    }

    /// <summary>
    /// The map as the 3D view reads it: states with their text and proofs, and the steps between them. The state
    /// with no goals is left out (every finished proof ends there, so it would tie the whole picture into one knot).
    /// </summary>
    public object ToView() => new
    {
        match = Match.ToString().ToLowerInvariant(),
        nodes = Nodes.Where(n => !n.Done).Select(n => new
        {
            id = n.Id,
            text = n.Example.Length > 900 ? n.Example[..900] + "…" : n.Example,
            decls = n.Declarations,
            trivial = n.Trivial,
        }),
        links = Edges.Where(e => !Nodes[e.From].Done && !Nodes[e.To].Done)
                     .Select(e => new { source = e.From, target = e.To, decl = e.Declaration }),
        shared = Shared.Select(n => n.Id),
    };
}

/// <summary>Collecting proof states from Lean, and the comparison that decides when two are the same.</summary>
public static partial class ProofStates
{
    /// <summary>The key of the state with no goals left.</summary>
    public const string NoGoals = "∎";

    /// <summary>
    /// Every tactic step of every tactic proof in an elaborated document, with the goals before and after it, as
    /// Lean reports them (two goal requests per step). <paramref name="ct"/> is checked between proofs.
    /// </summary>
    /// <param name="server">The Lean server that has <paramref name="uri"/> open and elaborated.</param>
    /// <param name="uri">The document.</param>
    /// <param name="file">How to name the file to people (a project-relative path).</param>
    /// <param name="lines">The document's text, split into lines, as the server has it.</param>
    /// <param name="ct">Cancels the collection.</param>
    public static async Task<IReadOnlyList<StateStep>> CollectAsync(LeanServer server, string uri, string file, IReadOnlyList<string> lines, CancellationToken ct = default)
    {
        var steps = new List<StateStep>();
        foreach (TacticProof p in Walkthrough.Proofs(lines))
        {
            ct.ThrowIfCancellationRequested();
            for (int i = 0; i < p.Steps.Count; i++)
            {
                ProofStep s = p.Steps[i];
                InteractiveGoals before = await server.InteractiveGoalsAsync(uri, s.Before, ct).ConfigureAwait(false);
                if (before.Goals.Count == 0)
                {
                    continue; // not inside the tactic block after all (a comment, a stray line)
                }
                IReadOnlyList<StateGoal>? after = null;
                if (!ProofSteps.ContinuesBelow(p.Steps, i))
                {
                    InteractiveGoals a = await server.InteractiveGoalsAsync(uri, s.After, ct).ConfigureAwait(false);
                    after = a.Goals.Select(StateGoal.From).ToList();
                }
                steps.Add(new StateStep(file, p.Declaration, s.Line, s.Indent, s.Text, before.Goals.Select(StateGoal.From).ToList(), after));
            }
        }
        return steps;
    }

    /// <summary>
    /// What two states are compared by. Local names are renamed in order of appearance (<c>v0</c>, <c>v1</c>, …),
    /// one renaming across all the goals of a state, so <c>n + 0 = n</c> and <c>m + 0 = m</c> agree when <c>n</c>
    /// and <c>m</c> are both the first local. Hypotheses count only for <see cref="StateMatch.Exact"/>;
    /// <see cref="StateMatch.Shape"/> also blanks the renamed locals and the numerals.
    /// </summary>
    public static string Key(IReadOnlyList<StateGoal> goals, StateMatch match)
    {
        if (goals.Count == 0)
        {
            return NoGoals;
        }
        var rename = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (StateGoal g in goals)
        {
            foreach (StateHypothesis h in g.Hypotheses)
            {
                foreach (string n in h.Names)
                {
                    rename.TryAdd(n, "v" + rename.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }
        Regex? names = rename.Count == 0 ? null
            : new Regex(@"(?<![\p{L}\p{N}_'!?✝.])(" + string.Join('|', rename.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)) + @")(?![\p{L}\p{N}_'!?✝])");
        string Sub(string s) => names is null ? s : names.Replace(s, m => rename[m.Value]);

        var sb = new StringBuilder();
        foreach (StateGoal g in goals)
        {
            if (sb.Length > 0)
            {
                sb.Append("\n\n");
            }
            if (match == StateMatch.Exact)
            {
                foreach (StateHypothesis h in g.Hypotheses)
                {
                    sb.Append(string.Join(' ', h.Names.Select(n => rename[n]))).Append(" : ").Append(Sub(h.Type)).Append('\n');
                }
            }
            string target = Sub(Collapse(g.Target));
            sb.Append("⊢ ").Append(match == StateMatch.Shape ? Blank(target) : target);
        }
        return sb.ToString();
    }

    /// <summary>
    /// A goal nobody would extract: <c>False</c>, <c>True</c>, the same term on both sides of an equation, or a
    /// relation between numerals. Every proof by contradiction reaches <c>False</c>; that is not a shared idea.
    /// </summary>
    public static bool IsTrivial(StateGoal g)
    {
        string t = Collapse(g.Target);
        if (t is "False" or "True")
        {
            return true;
        }
        Match m = Relation().Match(t);
        if (!m.Success)
        {
            return false;
        }
        string l = m.Groups[1].Value.Trim(), r = m.Groups[3].Value.Trim();
        return (m.Groups[2].Value == "=" && l == r) || (Numeral().IsMatch(l) && Numeral().IsMatch(r));
    }

    private static string Collapse(string s) => Whitespace().Replace(s, " ").Trim();

    private static string Blank(string s) => Digits().Replace(Renamed().Replace(s, "_"), "#");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\bv\d+\b")]
    private static partial Regex Renamed();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_.])\d+(?![\p{L}\p{N}_])")]
    private static partial Regex Digits();

    [GeneratedRegex(@"^([^=<≤≠]+?)\s(=|<|≤|≠)\s([^=<≤≠]+)$")]
    private static partial Regex Relation();

    [GeneratedRegex(@"^\(?\d+\)?$")]
    private static partial Regex Numeral();

    /// <summary>
    /// A copy of <paramref name="text"/> that asks for the goal where a proof reaches a state: a <c>sorry</c> on a line
    /// of its own in front of the tactic that runs there, and where it is. <see cref="ExtractLemma.RunAsync"/> turns the
    /// goal at that sorry into a lemma, as it does for any sorry. Lines are split on <c>\n</c>, as Lean counts them.
    /// </summary>
    public static (string Text, SorrySite Site) ProbeAt(string text, StateVisit visit)
    {
        int offset = 0;
        for (int line = 0; line < visit.Line; line++)
        {
            int nl = text.IndexOf('\n', offset);
            if (nl < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(visit), $"the text has no line {visit.Line + 1}");
            }
            offset = nl + 1;
        }
        offset += visit.Column;
        string probe = text.Insert(offset, "sorry\n" + new string(' ', visit.Column));
        return (probe, new SorrySite(visit.Line, visit.Column, offset, "sorry".Length, visit.Declaration));
    }

    /// <summary>The three maps of a set of steps, one per <see cref="StateMatch"/>, as the 3D view reads them.</summary>
    public static string ViewJson(IReadOnlyList<StateStep> steps) => JsonSerializer.Serialize(
        Enum.GetValues<StateMatch>().ToDictionary(m => m.ToString().ToLowerInvariant(), m => ProofStateMap.Build(steps, m).ToView()));
}
