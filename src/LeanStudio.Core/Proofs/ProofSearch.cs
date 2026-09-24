using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LeanStudio.Core.Editing;
using LeanStudio.Lsp;

namespace LeanStudio.Core.Proofs;

/// <summary>A <c>sorry</c> (or <c>admit</c>) in a file, 0-based.</summary>
/// <param name="Line">The 0-based line the keyword is on.</param>
/// <param name="Column">The 0-based column of its first character, in UTF-16 code units.</param>
/// <param name="Offset">The 0-based character offset of the keyword in the file text.</param>
/// <param name="Length">The keyword's length in characters (5 for both <c>sorry</c> and <c>admit</c>).</param>
/// <param name="Declaration">
/// The name of the nearest declaration at or above it (<c>example</c> for an anonymous one), or null if there is none.
/// </param>
public sealed record SorrySite(int Line, int Column, int Offset, int Length, string? Declaration)
{
    /// <summary>The location for people, such as <c>line 12</c> (1-based).</summary>
    public string Where => $"line {Line + 1}";
}

/// <summary>How one tactic fared against one goal.</summary>
/// <param name="Tactic">The tactic as tried, from <see cref="ProofSearch.Portfolio"/>.</param>
/// <param name="Outcome">Whether it closed the goal, failed, or was not available.</param>
/// <param name="Milliseconds">How long it ran, in milliseconds (0 when unavailable).</param>
/// <param name="Term">For a search tactic such as <c>exact?</c> that succeeded, the proof term it found; otherwise null.</param>
public sealed record TacticTrial(string Tactic, TrialOutcome Outcome, int Milliseconds, string? Term)
{
    /// <summary>Whether the tactic closed the goal with no <c>sorry</c> left in the proof.</summary>
    public bool Closes => Outcome == TrialOutcome.Closes;

    /// <summary>
    /// What to write in place of the <c>sorry</c>. A search tactic (<c>exact?</c>) is replaced by what it found,
    /// so the proof does not search again every time the file is checked.
    /// </summary>
    public string Replacement => Tactic.EndsWith('?') && Term is { Length: > 0 } t ? "exact " + t : Tactic;

    /// <summary>A one-line label for a list: a tick and the replacement, or a cross or dash and the tactic.</summary>
    public string Label => Outcome switch
    {
        TrialOutcome.Closes => $"✓ {Replacement}",
        TrialOutcome.Fails => $"✗ {Tactic}",
        _ => $"– {Tactic}",
    };

    /// <summary>The time taken for display (<c>840 ms</c>, <c>2.3 s</c>), or a note that the tactic was not available.</summary>
    public string Time => Outcome == TrialOutcome.Unavailable ? "not available here"
        : Outcome == TrialOutcome.Skipped ? "not needed"
        : Milliseconds < 1000 ? $"{Milliseconds} ms" : $"{Milliseconds / 1000.0:F1} s";
}

/// <summary>The result of one <see cref="TacticTrial"/>.</summary>
public enum TrialOutcome
{
    /// <summary>The tactic closed the goal completely.</summary>
    Closes,
    /// <summary>The tactic raised an error, ran out of heartbeats, or left goals or a <c>sorry</c> behind.</summary>
    Fails,
    /// <summary>The tactic does not exist with this file's imports (Mathlib's, in a file without Mathlib).</summary>
    Unavailable,
    /// <summary>Not tried: a library search, and another tactic already closes the goal.</summary>
    Skipped,
}

/// <summary>What proof search found for one <c>sorry</c>.</summary>
/// <param name="Site">The sorry searched.</param>
/// <param name="TermMode">
/// True when the sorry stood where a term was expected, so a tactic must be wrapped in <c>by</c> (see <see cref="SearchResult.Fill"/>).
/// </param>
/// <param name="Trials">One trial per portfolio tactic, in portfolio order; empty if Lean never reached the sorry.</param>
public sealed record SearchResult(SorrySite Site, bool TermMode, IReadOnlyList<TacticTrial> Trials)
{
    /// <summary>
    /// Values that satisfy the hypotheses and make the goal false, when no tactic closed it and some were found:
    /// the goal cannot be proved as stated.
    /// </summary>
    public string? Counterexample { get; init; }

    /// <summary>Lean reached this sorry (an earlier error can stop it from getting there).</summary>
    public bool Reached => Trials.Count > 0;

    /// <summary>The first trial that closed the goal (the cheapest, by portfolio order), or null if none did.</summary>
    public TacticTrial? Best => Trials.FirstOrDefault(t => t.Closes);

    /// <summary>Every trial that closed the goal, in portfolio order.</summary>
    public IEnumerable<TacticTrial> Successes => Trials.Where(t => t.Closes);

    /// <summary>The text that replaces the sorry: a tactic, or <c>by</c> and the tactic where a term was expected.</summary>
    public string Fill(TacticTrial t) => TermMode ? "by " + t.Replacement : t.Replacement;
}

/// <summary>
/// "Prove it": try a portfolio of tactics on the goal at every <c>sorry</c> in a file, in one pass of Lean, and
/// report which ones close it and how long each took. Each tactic runs from the same state, so they are
/// independent trials rather than a <c>first | … </c> chain that only tells you about the first success.
///
/// The file is instrumented rather than edited: every sorry becomes <c>leanstudio_try N</c>, a small tactic defined
/// at the top of a copy of the file that parses each candidate at run time (so tactics a file's imports do not
/// provide are reported as unavailable rather than breaking the parse), runs it with a heartbeat budget, and
/// restores the state before the next.
/// </summary>
public static partial class ProofSearch
{
    /// <summary>The tactics tried, cheapest and most specific first; the first that closes a goal is the suggestion.</summary>
    public static IReadOnlyList<string> Portfolio { get; } =
    [
        "rfl", "trivial", "assumption", "decide", "simp", "simp_all", "omega",
        "norm_num", "ring", "linarith", "positivity", "nlinarith", "field_simp", "tauto", "aesop", "grind", "exact?",
    ];

    /// <summary>The heartbeat budget for each tactic, in Lean's thousands (the default for a whole declaration is 200000).</summary>
    public const int HeartbeatsPerTactic = 50000;

    /// <summary>The prefix of the info messages the instrumented file logs, which <see cref="Parse"/> looks for.</summary>
    public const string Marker = "⟪leanstudio⟫";

    [GeneratedRegex(@"(?<![\w.'])(sorry|admit)(?![\w'!?])")]
    private static partial Regex SorryWord();

    [GeneratedRegex(@"^(@\[[^\]]*\]\s*)*((private|protected|noncomputable|partial|unsafe|nonrec|scoped|local)\s+)*(theorem|lemma|example|def|instance|abbrev)\b\s*(?<name>[^\s:({\[]*)")]
    private static partial Regex DeclarationLine();

    /// <summary>Every sorry and admit in code (not in comments or strings).</summary>
    public static IReadOnlyList<SorrySite> Sites(string text)
    {
        bool[] code = LeanText.CodeMask(text);
        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lineStarts.Add(i + 1);
            }
        }
        string[] lines = text.Split('\n');
        var sites = new List<SorrySite>();
        foreach (Match m in SorryWord().Matches(text))
        {
            if (!code[m.Index])
            {
                continue;
            }
            int line = lineStarts.BinarySearch(m.Index);
            line = line >= 0 ? line : ~line - 1;
            sites.Add(new SorrySite(line, m.Index - lineStarts[line], m.Index, m.Length, DeclarationAbove(lines, line)));
        }
        return sites;
    }

    private static string? DeclarationAbove(string[] lines, int line)
    {
        for (int i = line; i >= 0; i--)
        {
            Match d = DeclarationLine().Match(lines[i]);
            if (d.Success)
            {
                string n = d.Groups["name"].Value;
                return n.Length > 0 && n != ":=" ? n : "example";
            }
        }
        return null;
    }

    /// <summary>The sorry on the caret's line, else the nearest one to it.</summary>
    public static SorrySite? At(IReadOnlyList<SorrySite> sites, int line, int column)
    {
        SorrySite? onLine = sites.Where(s => s.Line == line).MinBy(s => Math.Abs(s.Column - column));
        if (onLine is not null)
        {
            return onLine;
        }
        // Otherwise the next one below in the same declaration, then the nearest above it.
        SorrySite? below = sites.Where(s => s.Line > line).MinBy(s => s.Line);
        SorrySite? above = sites.Where(s => s.Line < line).MaxBy(s => s.Line);
        return below is not null && (above is null || below.Line - line <= line - above.Line) ? below : above;
    }

    /// <summary>
    /// A copy of <paramref name="text"/> in which each of <paramref name="sites"/> is replaced by
    /// <c>leanstudio_try i</c> (i its index in the list), with the definitions that make that work after the imports.
    /// </summary>
    public static string Instrument(string text, IReadOnlyList<SorrySite> sites, IReadOnlyList<string>? portfolio = null)
    {
        var sb = new StringBuilder(text);
        for (int i = sites.Count - 1; i >= 0; i--)
        {
            SorrySite s = sites[i];
            sb.Remove(s.Offset, s.Length).Insert(s.Offset, "leanstudio_try " + i.ToString(CultureInfo.InvariantCulture));
        }
        string body = sb.ToString();
        int headerEnd = HeaderEnd(body);
        string prefix = body[..headerEnd];
        bool hasLean = Regex.IsMatch(prefix, @"(?m)^\s*(public\s+)?import\s+(Lean|Mathlib)\s*$");
        string addition = (prefix.Length > 0 && !prefix.EndsWith('\n') ? "\n" : "")
            + (hasLean ? "" : "import Lean\n")
            + Definitions(portfolio ?? Portfolio);
        return body.Insert(headerEnd, addition);
    }

    /// <summary>The offset just after a file's header: the <c>module</c>, <c>prelude</c> and <c>import</c> lines at its top.</summary>
    public static int HeaderEnd(string text)
    {
        bool[] code = LeanText.CodeMask(text);
        int end = 0, i = 0;
        while (i < text.Length)
        {
            int nl = text.IndexOf('\n', i);
            int lineEnd = nl < 0 ? text.Length : nl;
            var sb = new StringBuilder();
            for (int k = i; k < lineEnd; k++)
            {
                sb.Append(code[k] ? text[k] : ' ');
            }
            string c = sb.ToString().Trim();
            if (c.Length > 0)
            {
                if (!Regex.IsMatch(c, @"^((public|private|meta)\s+)*(import|module|prelude)\b"))
                {
                    break;
                }
                end = nl < 0 ? text.Length : nl + 1;
            }
            i = lineEnd + 1;
        }
        return end;
    }

    private static string Definitions(IReadOnlyList<string> portfolio)
    {
        string tactics = string.Join(", ", portfolio.Select(t => "\"" + t.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""));
        return $$"""
            open Lean Meta in
            /-- Small values to try for a variable whose type can be enumerated. -/
            private def leanstudioSamples (ty : Expr) : MetaM (Option (Array Expr)) := do
              let ty ← whnfR ty
              if ty.isConstOf ``Nat then return some ((List.range 11).toArray.map mkNatLit)
              if ty.isConstOf ``Int then return some (#[0, 1, -1, 2, -2, 3, -3, 4, -4, 5, -5].map fun (i : Int) => toExpr i)
              if ty.isConstOf ``Bool then return some #[toExpr false, toExpr true]
              return none

            open Lean Meta in
            /-- Whether a closed proposition is true, by evaluating its `Decidable` instance; none if it cannot say. -/
            private def leanstudioEval (p : Expr) : MetaM (Option Bool) := do
              try
                let r ← withAtLeastTransparency .default <| whnf (← mkDecide p)
                if r.isConstOf ``true then return some true
                if r.isConstOf ``false then return some false
                return none
              catch _ => return none

            open Lean Meta in
            private partial def leanstudioHunt (vars : Array Expr) (samples : Array (Array Expr)) (props : Array Expr) (goal : Expr)
                (i : Nat) (acc : Array Expr) (budget : IO.Ref Nat) : MetaM (Option (Array Expr)) := do
              if (← budget.get) == 0 then return none
              if i == vars.size then
                budget.modify (· - 1)
                for h in props do
                  if (← leanstudioEval (h.replaceFVars vars acc)) != some true then return none
                if (← leanstudioEval (goal.replaceFVars vars acc)) == some false then return some acc
                return none
              for v in samples[i]! do
                if let some r ← leanstudioHunt vars samples props goal (i+1) (acc.push v) budget then return some r
              return none

            open Lean Elab Tactic Meta in
            /-- Plausible (in Mathlib projects) tests a goal with random values of many more types. -/
            private def leanstudioPlausible : TacticM (Option String) := do
              match Parser.runParserCategory (← getEnv) `tactic "plausible" with
              | .error _ => return none
              | .ok stx =>
                try
                  withoutRecover <| evalTactic stx
                  return none
                catch e =>
                  let msg := (← e.toMessageData.toString)
                  if msg.startsWith "Found a counter-example" || msg.startsWith "Found problems" then
                    return some (msg.replace "\n" " ")
                  return none

            open Lean Elab Tactic Meta in
            /-- A counterexample to the main goal: small values of its Nat, Int and Bool variables that satisfy every
            hypothesis and make the goal false. Falls back to Plausible where the project has it. -/
            private def leanstudioCounterexample : TacticM (Option String) := do
              let s ← saveState
              try
                let (_, g) ← (← getMainGoal).intros
                let found ← g.withContext do
                  let mut vars := #[]; let mut samples := #[]; let mut props := #[]
                  for d in (← getLCtx) do
                    if d.isImplementationDetail then continue
                    if ← isProp d.type then props := props.push d.type
                    else if let some xs ← leanstudioSamples d.type then
                      vars := vars.push d.toExpr; samples := samples.push xs
                    else return none
                  if vars.isEmpty then return none
                  let budget ← IO.mkRef 3000
                  let some vals ← leanstudioHunt vars samples props (← g.getType) 0 #[] budget | return none
                  let parts ← (vars.zip vals).mapM fun (v, x) => do
                    return s!"{(← v.fvarId!.getDecl).userName.eraseMacroScopes} = {← ppExpr x}"
                  return some (", ".intercalate parts.toList)
                if found.isSome then return found
                leanstudioPlausible
              catch _ => return none
              finally s.restore

            open Lean Elab Tactic Meta in
            private def leanstudioTry (n : Nat) (mode : String) : TacticM Unit := do
              let tacs : Array String := #[{{tactics}}]
              let g ← getMainGoal
              let s ← saveState
              let mut out := s!"{{Marker}}\t{n}\t{mode}"
              let mut closed := false
              for i in [0:tacs.size] do
                s.restore
                -- A library search (exact?) can take a minute in Mathlib: only worth it when nothing else worked.
                if closed && tacs[i]!.endsWith "?" then
                  out := out ++ s!"\n{i}\tskipped\t0\t"
                  continue
                match Parser.runParserCategory (← getEnv) `tactic tacs[i]! with
                | .error _ => out := out ++ s!"\n{i}\tunavailable\t0\t"
                | .ok stx =>
                  let t0 ← IO.monoMsNow
                  let ok ← tryCatchRuntimeEx
                    (withCurrHeartbeats <| withTheReader Core.Context (fun c => { c with maxHeartbeats := {{HeartbeatsPerTactic}} * 1000 }) do
                      withoutRecover <| evalTactic stx
                      let pf ← instantiateMVars (.mvar g)
                      return (← getUnsolvedGoals).isEmpty && !pf.hasSorry && !pf.hasSyntheticSorry)
                    (fun _ => pure false)
                  let ms := (← IO.monoMsNow) - t0
                  closed := closed || ok
                  let term ← if ok && tacs[i]!.endsWith "?" then
                      (do pure ((toString (← ppExpr (← instantiateMVars (.mvar g)))).replace "\n" " "))
                    else pure ""
                  out := out ++ s!"\n{i}\t{if ok then "ok" else "fail"}\t{ms}\t{term}"
              s.restore
              if !closed then
                if let some cex ← withCurrHeartbeats <| withTheReader Core.Context (fun c => { c with maxHeartbeats := {{HeartbeatsPerTactic}} * 1000 }) leanstudioCounterexample then
                  out := out ++ s!"\ncex\t{cex}"
              s.restore
              admitGoal g
              logInfo out

            syntax "leanstudio_try " num : tactic
            syntax "leanstudio_try_term_ " num : tactic
            syntax "leanstudio_try " num : term
            macro_rules | `(term| leanstudio_try $n:num) => `(by leanstudio_try_term_ $n)
            open Lean Elab Tactic in
            elab_rules : tactic | `(tactic| leanstudio_try $n:num) => leanstudioTry n.getNat "tactic"
            open Lean Elab Tactic in
            elab_rules : tactic | `(tactic| leanstudio_try_term_ $n:num) => leanstudioTry n.getNat "term"

            """;
    }

    /// <summary>Read the trials back out of the messages Lean produced for the instrumented file.</summary>
    public static IReadOnlyList<SearchResult> Parse(IEnumerable<string> messages, IReadOnlyList<SorrySite> sites, IReadOnlyList<string>? portfolio = null)
    {
        portfolio ??= Portfolio;
        var found = new Dictionary<int, SearchResult>();
        foreach (string m in messages)
        {
            string[] lines = m.Replace("\r", "", StringComparison.Ordinal).Split('\n');
            string[] head = lines[0].Split('\t');
            if (head.Length < 3 || head[0] != Marker || !int.TryParse(head[1], CultureInfo.InvariantCulture, out int n) || n < 0 || n >= sites.Count)
            {
                continue;
            }
            var trials = new List<TacticTrial>();
            string? counterexample = null;
            foreach (string l in lines.Skip(1))
            {
                if (l.StartsWith("cex\t", StringComparison.Ordinal))
                {
                    counterexample = l[4..].Trim();
                    continue;
                }
                string[] f = l.Split('\t');
                if (f.Length < 3 || !int.TryParse(f[0], CultureInfo.InvariantCulture, out int i) || i < 0 || i >= portfolio.Count)
                {
                    continue;
                }
                TrialOutcome o = f[1] switch
                {
                    "ok" => TrialOutcome.Closes,
                    "fail" => TrialOutcome.Fails,
                    "skipped" => TrialOutcome.Skipped,
                    _ => TrialOutcome.Unavailable,
                };
                int ms = int.TryParse(f[2], CultureInfo.InvariantCulture, out int x) ? x : 0;
                string? term = f.Length > 3 && f[3].Trim().Length > 0 ? Regex.Replace(f[3].Trim(), @"\s+", " ") : null;
                trials.Add(new TacticTrial(portfolio[i], o, ms, term));
            }
            found[n] = new SearchResult(sites[n], head[2] == "term", trials) { Counterexample = counterexample };
        }
        return sites.Select((s, i) => found.TryGetValue(i, out SearchResult? r) ? r : new SearchResult(s, false, [])).ToList();
    }

    /// <summary>
    /// Replace sorries with what was found, bottom-up so earlier offsets stay right. Each fill must still see a
    /// sorry at its site, so a file that changed since the search is left alone where it no longer matches.
    /// </summary>
    public static string Apply(string text, IEnumerable<(SearchResult Result, TacticTrial Trial)> fills)
    {
        var sb = new StringBuilder(text);
        foreach (var (r, t) in fills.OrderByDescending(f => f.Result.Site.Offset))
        {
            SorrySite s = r.Site;
            if (s.Offset + s.Length <= sb.Length && sb.ToString(s.Offset, s.Length) is "sorry" or "admit")
            {
                sb.Remove(s.Offset, s.Length).Insert(s.Offset, r.Fill(t));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Run the search with a Lean server: the instrumented copy is opened as a document that exists only in the
    /// server (beside the real file, so it sees the same project and imports), checked, and closed again.
    /// </summary>
    public static async Task<IReadOnlyList<SearchResult>> RunAsync(LeanServer server, string sourcePath, string text, IReadOnlyList<SorrySite> sites, CancellationToken ct = default)
    {
        if (sites.Count == 0)
        {
            return [];
        }
        IReadOnlyList<Diagnostic> diags = await Scratch.CheckAsync(server, sourcePath, "Prove", Instrument(text, sites), ct).ConfigureAwait(false);
        return Parse(diags.Where(d => d.Severity == DiagnosticSeverity.Information).Select(d => d.Message), sites);
    }
}

/// <summary>
/// Check text Lean should see but no one should: a document opened in the server under a name beside the real
/// file (so it belongs to the same project) that is never written to disk, and closed when done.
/// </summary>
public static class Scratch
{
    /// <summary>
    /// The URI of the scratch document for <paramref name="sourcePath"/>: <c>LeanStudio{purpose}_{name}</c> in the same
    /// directory. The file is never created.
    /// </summary>
    public static string UriFor(string sourcePath, string purpose)
    {
        string full = Path.GetFullPath(sourcePath);
        return LeanServer.UriOf(Path.Combine(Path.GetDirectoryName(full)!, $"LeanStudio{purpose}_{Path.GetFileName(full)}"));
    }

    /// <summary>
    /// Open <paramref name="text"/> as the scratch document for <paramref name="sourcePath"/> (closing any stale one
    /// first), wait until Lean has elaborated it and its diagnostics have settled, and return them. The document is
    /// closed afterwards if the server is still running.
    /// </summary>
    /// <param name="server">A running Lean server for the project.</param>
    /// <param name="sourcePath">The real file the text stands in for.</param>
    /// <param name="purpose">A short word naming the scratch document, such as <c>Prove</c>.</param>
    /// <param name="text">The text to check.</param>
    /// <param name="ct">Cancels the wait; the document is still closed.</param>
    public static async Task<IReadOnlyList<Diagnostic>> CheckAsync(LeanServer server, string sourcePath, string purpose, string text, CancellationToken ct = default)
    {
        string uri = UriFor(sourcePath, purpose);
        if (server.IsOpen(uri))
        {
            await server.CloseAsync(uri).ConfigureAwait(false);
        }
        await server.OpenAsync(uri, text).ConfigureAwait(false);
        try
        {
            await server.WaitForElaborationAsync(uri, ct).ConfigureAwait(false);
            // Lean publishes diagnostics as it goes, and the last batch can trail the "finished" report: wait
            // until they have been still for a moment.
            IReadOnlyList<Diagnostic> last = server.DiagnosticsOf(uri);
            for (int quiet = 0, i = 0; quiet < 3 && i < 40; i++)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                IReadOnlyList<Diagnostic> now = server.DiagnosticsOf(uri);
                quiet = ReferenceEquals(now, last) ? quiet + 1 : 0;
                last = now;
            }
            return last;
        }
        finally
        {
            if (server.State == LeanServerState.Running)
            {
                await server.CloseAsync(uri).ConfigureAwait(false);
            }
        }
    }
}
