using System.Text;
using System.Text.RegularExpressions;
using LeanStudio.Core.Proofs;
using LeanStudio.Lsp;

namespace LeanStudio.Core.Ai;

/// <summary>What the AI suggested for one sorry, after Lean checked every suggestion.</summary>
/// <param name="Result">The trials, one per suggestion, in the form Prove It shows.</param>
/// <param name="Model">The model's name.</param>
/// <param name="Rounds">How many times the model was asked (a second time when nothing in the first round worked).</param>
public sealed record AiProofResult(SearchResult Result, string Model, int Rounds)
{
    /// <summary>How many proofs the model suggested, in all rounds.</summary>
    public int Suggested => Result.Trials.Count;

    /// <summary>A sentence for people: how many were suggested, and how many Lean accepted.</summary>
    public string Summary
    {
        get
        {
            int ok = Result.Successes.Count();
            return Suggested == 0 ? $"{Model} did not suggest a proof Lean could try."
                : ok == 0 ? $"{Model} suggested {Suggested} proof{(Suggested == 1 ? "" : "s")}; Lean accepted none."
                : $"{Model} suggested {Suggested}; Lean checked them and {ok} {(ok == 1 ? "closes" : "close")} the goal.";
        }
    }
}

/// <summary>
/// Proof search with a language model, where the model only suggests: every proof it proposes is run by Lean, from
/// the sorry's own state, exactly as Prove It runs its portfolio, and only proofs Lean accepts with no sorry left
/// are offered. A model that invents a lemma, or a tactic that does not exist, costs a failed trial, not a wrong
/// proof in the file.
/// </summary>
public static partial class AiProver
{
    /// <summary>How many proofs to ask for in each round.</summary>
    public const int PerRound = 5;

    /// <summary>
    /// The goal at a sorry, as Lean prints it (hypotheses, then <c>⊢</c> and the target); null when Lean has none there,
    /// such as when an earlier error stops it before the sorry.
    /// </summary>
    public static async Task<string?> GoalAtAsync(LeanServer server, string uri, SorrySite site, CancellationToken ct = default)
    {
        var pos = new Position(site.Line, site.Column);
        PlainGoal? g = await server.PlainGoalAsync(uri, pos, ct).ConfigureAwait(false);
        if (g is { Goals.Count: > 0 })
        {
            return string.Join("\n\n", g.Goals);
        }
        PlainTermGoal? t = await server.PlainTermGoalAsync(uri, pos, ct).ConfigureAwait(false);
        return t is { Goal.Length: > 0 } ? t.Goal : null;
    }

    /// <summary>
    /// The goal at a sorry in <paramref name="text"/>: from the open document when the server has
    /// <paramref name="path"/> open, otherwise from a scratch copy that is checked and closed again.
    /// </summary>
    public static async Task<string?> GoalAsync(LeanServer server, string path, string text, SorrySite site, CancellationToken ct = default)
    {
        string uri = LeanServer.UriOf(path);
        if (server.IsOpen(uri))
        {
            return await GoalAtAsync(server, uri, site, ct).ConfigureAwait(false);
        }
        string scratch = Scratch.UriFor(path, "Goal");
        if (server.IsOpen(scratch))
        {
            await server.CloseAsync(scratch).ConfigureAwait(false);
        }
        await server.OpenAsync(scratch, text).ConfigureAwait(false);
        try
        {
            await server.WaitForElaborationAsync(scratch, ct).ConfigureAwait(false);
            return await GoalAtAsync(server, scratch, site, ct).ConfigureAwait(false);
        }
        finally
        {
            if (server.State == LeanServerState.Running)
            {
                await server.CloseAsync(scratch).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// The code the model sees around a sorry: the file's imports and <c>open</c>s, then the declaration the sorry is in,
    /// cut to <paramref name="tokens"/>.
    /// </summary>
    public static string Context(string text, SorrySite site, int tokens)
    {
        string[] lines = text.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        var header = lines.Take(ProofSearchHeaderLines(text))
            .Concat(lines.Where(l => OpenLine().IsMatch(l)))
            .Where(l => l.Trim().Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        int start = site.Line;
        while (start > 0 && !DeclarationStart().IsMatch(lines[start]))
        {
            start--;
        }
        // Keep a docstring or attribute just above the declaration: it often says what is meant.
        while (start > 0)
        {
            string above = lines[start - 1].Trim();
            if (above.StartsWith("@[", StringComparison.Ordinal))
            {
                start--;
            }
            else if (above.EndsWith("-/", StringComparison.Ordinal))
            {
                int top = start - 1;
                while (top > 0 && !lines[top].Contains("/-", StringComparison.Ordinal))
                {
                    top--;
                }
                start = top;
            }
            else
            {
                break;
            }
        }
        int end = site.Line;
        while (end + 1 < lines.Length && !DeclarationStart().IsMatch(lines[end + 1]) && !(lines[end + 1].Trim().Length == 0 && end + 2 < lines.Length && DeclarationStart().IsMatch(lines[end + 2])))
        {
            end++;
        }
        var decl = new StringBuilder();
        for (int i = start; i <= end; i++)
        {
            string l = lines[i];
            if (i == site.Line && site.Column + site.Length <= l.Length)
            {
                // Mark the sorry being asked about, where a declaration has several.
                l = l[..site.Column] + "sorry /- ← this one -/" + l[(site.Column + site.Length)..];
            }
            decl.Append(l).Append('\n');
        }
        string head = AiText.Fit(string.Join("\n", header), Math.Max(40, tokens / 5));
        string body = AiText.Fit(decl.ToString().TrimEnd(), tokens - AiText.EstimateTokens(head), FitKeep.End);
        return head.Length > 0 ? head + "\n\n" + body : body;
    }

    private static int ProofSearchHeaderLines(string text)
    {
        int end = ProofSearch.HeaderEnd(text);
        return text[..end].Count(c => c == '\n');
    }

    [GeneratedRegex(@"^\s*(open|namespace|section|variable|universe|set_option)\b")]
    private static partial Regex OpenLine();

    [GeneratedRegex(@"^(@\[[^\]]*\]\s*)*((private|protected|noncomputable|partial|unsafe|nonrec|scoped|local)\s+)*(theorem|lemma|example|def|instance|abbrev|structure|inductive|class)\b")]
    private static partial Regex DeclarationStart();

    /// <summary>The instructions: what the model is, and exactly how to answer so the answer can be checked.</summary>
    public static string SystemPrompt(bool mathlib, int count) =>
        $"""
        You are an expert in Lean 4{(mathlib ? " and Mathlib" : "")}. You write tactic proofs that Lean accepts.
        Reply with {count} different candidate proofs for the goal. Put each in its own ```lean code block, containing only the tactics that replace `sorry`: no `by`, no statement, no explanation.
        Prefer short, robust proofs: simp, omega, decide, induction, cases, rcases, obtain, intro, constructor, exact, apply, refine, rw, calc{(mathlib ? ", linarith, nlinarith, positivity, norm_num, ring, field_simp, aesop, gcongr" : "")}.
        Only use lemmas that exist{(mathlib ? " in Lean 4 core or Mathlib" : " in Lean 4 core; Mathlib is not available")}. Never write sorry.
        """;

    /// <summary>
    /// The conversation asking for proofs of <paramref name="goal"/>, sized for <paramref name="model"/>: a small
    /// on-device model gets the goal and the declaration; a large one gets more of the surrounding code.
    /// </summary>
    /// <param name="model">The model that will answer (its context size decides how much code fits).</param>
    /// <param name="text">The file's text.</param>
    /// <param name="site">The sorry.</param>
    /// <param name="goal">The goal at the sorry, from <see cref="GoalAtAsync"/>.</param>
    /// <param name="failed">Proofs already tried that Lean rejected, so the model tries something else.</param>
    public static IReadOnlyList<ChatMessage> Prompt(IChatModel model, string text, SorrySite site, string goal, IReadOnlyList<string> failed)
    {
        bool mathlib = Regex.IsMatch(text, @"(?m)^\s*(public\s+)?import\s+Mathlib");
        string system = SystemPrompt(mathlib, PerRound);
        int answer = AnswerTokens(model);
        int room = model.ContextTokens - answer - AiText.EstimateTokens(system) - 120;
        string goalText = AiText.Fit(goal, Math.Max(120, room * 2 / 5));
        string failedText = failed.Count == 0 ? ""
            : "\n\nLean rejected these, so try different ideas:\n" + AiText.Fit(string.Join("\n", failed.Select(f => "- " + f.Replace("\n", "; ", StringComparison.Ordinal))), Math.Max(60, room / 6));
        string code = Context(text, site, Math.Max(80, room - AiText.EstimateTokens(goalText) - AiText.EstimateTokens(failedText)));
        string user = $"""
            The code:
            ```lean
            {code}
            ```

            Lean's goal at the marked sorry:
            ```lean
            {goalText}
            ```{failedText}

            Give {PerRound} candidate proofs, each in its own ```lean block.
            """;
        return [ChatMessage.System(system), ChatMessage.User(user)];
    }

    /// <summary>How long an answer to allow: most of what is left of a small model's context, 1500 tokens otherwise.</summary>
    public static int AnswerTokens(IChatModel model) => Math.Clamp(model.ContextTokens / 5, 400, 1500);

    /// <summary>A proof to try, as the model wrote it and in the form the trial parses.</summary>
    /// <param name="Tactic">What the trial runs: one line, or several lines wrapped in parentheses.</param>
    /// <param name="Display">What goes in the file: the lines as written (dedented).</param>
    public sealed record Candidate(string Tactic, string Display);

    /// <summary>
    /// The proofs in a model's answer: each fenced code block (or, from a model that ignored the format, each line
    /// of a plain answer), cleaned up into something a trial can run. Proofs using <c>sorry</c>, <c>admit</c> or
    /// <c>native_decide</c> (which trusts the compiler rather than the kernel) are dropped, as are repeats and
    /// anything in <paramref name="exclude"/>.
    /// </summary>
    public static IReadOnlyList<Candidate> Candidates(string answer, int max = 8, IEnumerable<string>? exclude = null)
    {
        string a = AiText.WithoutThinking(answer);
        IReadOnlyList<string> blocks = AiText.CodeBlocks(a);
        if (blocks.Count == 0)
        {
            blocks = a.Split('\n')
                .Select(l => ListMarker().Replace(l.Trim(), "").Trim('`', ' '))
                .Where(l => l.Length > 0 && !l.EndsWith(':') && TacticLike().IsMatch(l))
                .ToList();
        }
        var seen = new HashSet<string>(exclude ?? [], StringComparer.Ordinal);
        var result = new List<Candidate>();
        foreach (string b in blocks)
        {
            if (Clean(b) is Candidate c && !Forbidden().IsMatch(c.Display) && seen.Add(c.Display))
            {
                result.Add(c);
                if (result.Count == max)
                {
                    break;
                }
            }
        }
        return result;
    }

    [GeneratedRegex(@"^(\d+[.)]|[-*•])\s*")]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"^(simp|simp_all|omega|decide|rfl|exact|apply|refine|intro|intros|induction|cases|rcases|obtain|constructor|rw|rewrite|linarith|nlinarith|norm_num|ring|ring_nf|field_simp|positivity|aesop|grind|tauto|trivial|assumption|use|exists|unfold|calc|have|show|subst|contradiction|exfalso|left|right|ext|funext|congr|gcongr|split|by_cases|by_contra|push_neg|specialize|interval_cases|fin_cases|nlinarith|polyrith|norm_cast|push_cast|exact_mod_cast|zify|qify|·)\b")]
    private static partial Regex TacticLike();

    [GeneratedRegex(@"(?<![\w.'])(sorry|admit|native_decide)(?![\w'])")]
    private static partial Regex Forbidden();

    [GeneratedRegex(@"^(theorem|lemma|example|def)\b[^\n]*:=\s*(by\b)?\s*", RegexOptions.Singleline)]
    private static partial Regex RestatedHeader();

    /// <summary>One block cleaned into a <see cref="Candidate"/>; null if nothing usable is left.</summary>
    public static Candidate? Clean(string block)
    {
        string b = block.Replace("\r", "", StringComparison.Ordinal).Replace("\t", "  ", StringComparison.Ordinal);
        // A model that restated the theorem: keep only the proof after `:=`.
        Match header = RestatedHeader().Match(b.TrimStart());
        if (header.Success)
        {
            b = b.TrimStart()[header.Length..];
        }
        var lines = b.Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[0].Trim().Length == 0)
        {
            lines.RemoveAt(0);
        }
        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }
        // Comment lines say nothing Lean needs.
        lines = lines.Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)).ToList();
        if (lines.Count == 0)
        {
            return null;
        }
        // A leading `by` (the model wrote the term, not the tactics): the tactics follow it.
        string first = lines[0].TrimStart();
        if (first == "by")
        {
            lines.RemoveAt(0);
        }
        else if (first.StartsWith("by ", StringComparison.Ordinal))
        {
            lines[0] = lines[0][..(lines[0].Length - first.Length)] + first[3..].TrimStart();
        }
        if (lines.Count == 0)
        {
            return null;
        }
        int indent = lines.Where(l => l.Trim().Length > 0).Min(l => l.Length - l.TrimStart().Length);
        lines = lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()).ToList();
        if (lines.Count == 1)
        {
            string one = lines[0].Trim();
            // A bare term where tactics were asked for: prove with it.
            if (!TacticLike().IsMatch(one) && TermLike().IsMatch(one))
            {
                one = "exact " + one;
            }
            return one.Length == 0 ? null : new Candidate(one, one);
        }
        string display = string.Join("\n", lines);
        // Several lines: run as `( … )`, which Lean parses as one tactic, with the lines indented alike inside.
        string tactic = "(\n" + string.Join("\n", lines.Select(l => l.Length == 0 ? l : "  " + l)) + ")";
        return new Candidate(tactic, display);
    }

    [GeneratedRegex(@"^([\w.']+\.[\w.']+|⟨|fun\b|\(|Or\.|And\.|absurd\b|le_of|lt_of)")]
    private static partial Regex TermLike();

    /// <summary>
    /// Ask <paramref name="model"/> for proofs of the goal at <paramref name="site"/>, run each in Lean from the
    /// sorry's state, and, if none works, ask once more with the rejected ones listed. The file is not changed.
    /// </summary>
    /// <param name="server">A running Lean server for the file's project, with the file open.</param>
    /// <param name="model">The model.</param>
    /// <param name="path">The file.</param>
    /// <param name="text">The file's text, as the server has it.</param>
    /// <param name="site">The sorry.</param>
    /// <param name="progress">Told what is happening, for a status line.</param>
    /// <param name="rounds">At most this many requests to the model.</param>
    /// <param name="ct">Stops everything.</param>
    /// <exception cref="AiException">The model could not be reached.</exception>
    public static async Task<AiProofResult> RunAsync(LeanServer server, IChatModel model, string path, string text, SorrySite site,
        Action<string>? progress = null, int rounds = 2, CancellationToken ct = default)
    {
        progress?.Invoke("Reading the goal…");
        string? goal = await GoalAsync(server, path, text, site, ct).ConfigureAwait(false);
        if (goal is null)
        {
            return new AiProofResult(new SearchResult(site, false, []), model.DisplayName, 0);
        }
        var trials = new List<TacticTrial>();
        var failed = new List<string>();
        bool termMode = false;
        int round = 0;
        while (round < rounds)
        {
            round++;
            progress?.Invoke(round == 1 ? $"Asking {model.DisplayName} for proofs…" : $"None worked; asking {model.DisplayName} for different ones…");
            string answer = await model.CompleteAsync(Prompt(model, text, site, goal, failed), new ChatOptions(AnswerTokens(model), round == 1 ? 0.3 : 0.7), ct).ConfigureAwait(false);
            IReadOnlyList<Candidate> candidates = Candidates(answer, PerRound + 2, trials.Select(t => t.Display ?? t.Tactic));
            if (candidates.Count == 0)
            {
                continue;
            }
            progress?.Invoke($"Lean is checking {candidates.Count} suggested proof{(candidates.Count == 1 ? "" : "s")}…");
            IReadOnlyList<SearchResult> checkedRound = await ProofSearch.RunAsync(server, path, text, [site], ct, candidates.Select(c => c.Tactic).ToList()).ConfigureAwait(false);
            SearchResult r = checkedRound[0];
            termMode = r.TermMode;
            var byTactic = candidates.GroupBy(c => c.Tactic, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Display, StringComparer.Ordinal);
            foreach (TacticTrial t in r.Trials)
            {
                string display = byTactic.TryGetValue(t.Tactic, out string? d) ? d : t.Tactic;
                // For a suggestion, "unavailable" means Lean could not parse it: it failed.
                TrialOutcome outcome = t.Outcome == TrialOutcome.Unavailable ? TrialOutcome.Fails : t.Outcome;
                trials.Add(t with { Outcome = outcome, Display = display == t.Tactic ? null : display, SuggestedBy = model.DisplayName });
                if (!t.Closes)
                {
                    failed.Add(display);
                }
            }
            if (trials.Any(t => t.Closes) || !r.Reached)
            {
                break;
            }
        }
        // Proofs that work first, shortest first; then the rest as suggested.
        var ordered = trials.OrderBy(t => t.Closes ? 0 : 1).ThenBy(t => t.Closes ? t.Replacement.Length : 0).ToList();
        return new AiProofResult(new SearchResult(site, termMode, ordered), model.DisplayName, round);
    }
}
