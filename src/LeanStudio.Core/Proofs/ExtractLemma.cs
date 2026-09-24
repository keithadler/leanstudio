using System.Text.RegularExpressions;
using LeanStudio.Lsp;

namespace LeanStudio.Core.Proofs;

/// <summary>A goal turned into a lemma of its own: its text, where it goes, and what replaces the sorry.</summary>
/// <param name="Name">The lemma's name, as the user gave it.</param>
/// <param name="Text">The <c>theorem</c> with Lean's signature and a <c>sorry</c> proof, ending in a blank line.</param>
/// <param name="InsertLine">The 0-based line to insert <c>Text</c> before (see <see cref="ExtractLemma.InsertionLine"/>).</param>
/// <param name="Call">
/// What replaces the sorry: <c>exact name (by assumption)…</c> in tactic mode, or the application alone
/// (parenthesized when it has arguments) where a term was expected.
/// </param>
public sealed record ExtractedLemma(string Name, string Text, int InsertLine, string Call);

/// <summary>
/// Extract a goal as a lemma: the goal at a <c>sorry</c>, with the hypotheses it needs, becomes a standalone
/// <c>theorem</c> above the declaration, and the sorry becomes a use of it. The way to break a long proof into
/// pieces, or to set a stuck step aside and prove it on its own (with Prove It, say).
///
/// Lean does the work, in a scratch copy of the file where the sorry is replaced by a small tactic: it keeps the
/// hypotheses the goal depends on (every proof and instance, and the data they and the goal mention), makes data
/// implicit, proofs explicit and instances instance arguments, and prints the signature itself. At the use, Lean
/// infers the data and the proofs are filled in <c>by assumption</c>.
/// </summary>
public static partial class ExtractLemma
{
    /// <summary>The prefix of the info message the instrumented file logs, which <see cref="Parse"/> looks for.</summary>
    public const string Marker = "⟪extract⟫";

    [GeneratedRegex(@"^(@\[[^\]]*\]\s*)*((private|protected|noncomputable|partial|unsafe|nonrec|scoped|local)\s+)*(theorem|lemma|example|def|instance|abbrev|opaque)\b")]
    private static partial Regex DeclarationStart();

    /// <summary>Whether a name can be written as is: letters, digits, _, ' and dots between parts.</summary>
    public static bool IsValidName(string name) => Regex.IsMatch(name, @"^[\p{L}_][\p{L}\p{N}_'!?]*(\.[\p{L}_][\p{L}\p{N}_'!?]*)*$");

    /// <summary>
    /// The line to insert a lemma before: the declaration containing <paramref name="line"/>, above its doc comment and
    /// attributes. Both lines are 0-based.
    /// </summary>
    public static int InsertionLine(IReadOnlyList<string> lines, int line)
    {
        int i = Math.Min(line, lines.Count - 1);
        while (i > 0 && !DeclarationStart().IsMatch(lines[i]))
        {
            i--;
        }
        while (i > 0)
        {
            string prev = lines[i - 1].TrimEnd();
            if (prev.StartsWith("@[", StringComparison.Ordinal))
            {
                i--;
            }
            else if (prev.EndsWith("-/", StringComparison.Ordinal))
            {
                int j = i - 1;
                while (j > 0 && !lines[j].Contains("/-", StringComparison.Ordinal))
                {
                    j--;
                }
                i = j;
            }
            else
            {
                break;
            }
        }
        return i;
    }

    /// <summary>A copy of the file in which the sorry at <paramref name="site"/> asks Lean for its lemma.</summary>
    public static string Instrument(string text, SorrySite site, string name)
    {
        string body = text[..site.Offset] + "leanstudio_extract \"" + name + "\"" + text[(site.Offset + site.Length)..];
        int headerEnd = ProofSearch.HeaderEnd(body);
        string prefix = body[..headerEnd];
        bool hasLean = Regex.IsMatch(prefix, @"(?m)^\s*(public\s+)?import\s+(Lean|Mathlib)\s*$");
        string addition = (prefix.Length > 0 && !prefix.EndsWith('\n') ? "\n" : "") + (hasLean ? "" : "import Lean\n") + Definitions;
        return body.Insert(headerEnd, addition);
    }

    private const string Definitions = $$"""
        open Lean Elab Tactic Meta in
        private def leanstudioExtract (name : String) (mode : String) : TacticM Unit := do
          let g ← getMainGoal
          let (ty, explicitProps) ← g.withContext do
            let target ← instantiateMVars (← g.getType)
            -- What the goal needs: every proof and instance, and the data they and the goal mention.
            let decls := (← getLCtx).foldl (init := #[]) fun acc d => if d.isImplementationDetail then acc else acc.push d
            let mut keep : FVarIdSet := {}
            for f in (collectFVars {} target).fvarIds do keep := keep.insert f
            for d in decls.reverse do
              if (← isProp d.type) || (← isClass? d.type).isSome || keep.contains d.fvarId then
                keep := keep.insert d.fvarId
                for f in (collectFVars {} (← instantiateMVars d.type)).fvarIds do keep := keep.insert f
            let fvars := decls.filter (keep.contains ·.fvarId) |>.map (·.toExpr)
            let mut infos := #[]
            let mut explicitProps := 0
            for f in fvars do
              let d ← f.fvarId!.getDecl
              if ← isProp d.type then
                infos := infos.push BinderInfo.default
                explicitProps := explicitProps + 1
              else if (← isClass? d.type).isSome then infos := infos.push BinderInfo.instImplicit
              else infos := infos.push BinderInfo.implicit
            let rec setInfos (e : Expr) (k : Nat) : Expr :=
              match e with
              | .forallE n t b _ =>
                if h : k < infos.size then
                  let n := if infos[k] != .instImplicit && n.hasMacroScopes then Name.mkSimple s!"{n.eraseMacroScopes}_{k+1}" else n
                  .forallE n t (setInfos b (k+1)) infos[k]
                else e
              | e => e
            return (← instantiateMVars (setInfos (← mkForallFVars fvars target) 0), explicitProps)
          -- Print it the way Lean prints a signature: add it for a moment (under the declaration's own name, as
          -- declarations made inside a proof must be), print it outside the proof's context, take it away.
          let n := ((← Term.getDeclName?).getD .anonymous).str name
          let env ← getEnv
          addDecl (.axiomDecl { name := n, levelParams := (collectLevelParams {} ty).params.toList, type := ty, isUnsafe := false })
          let sig := toString (← withLCtx {} {} <| PrettyPrinter.ppSignature n).fmt
          setEnv env
          let sig := name ++ (sig.drop (toString n).length).toString
          let args := String.join (List.replicate explicitProps " (by assumption)")
          admitGoal g
          logInfo m!"{{Marker}}\t{mode}\t{name}{args}\n{sig}"

        syntax "leanstudio_extract " str : tactic
        syntax "leanstudio_extract_term_ " str : tactic
        syntax "leanstudio_extract " str : term
        macro_rules | `(term| leanstudio_extract $s:str) => `(by leanstudio_extract_term_ $s)
        open Lean Elab Tactic in
        elab_rules : tactic | `(tactic| leanstudio_extract $s:str) => leanstudioExtract s.getString "tactic"
        open Lean Elab Tactic in
        elab_rules : tactic | `(tactic| leanstudio_extract_term_ $s:str) => leanstudioExtract s.getString "term"

        """;

    /// <summary>
    /// Read Lean's answer (the use and the signature) from the messages of the instrumented file; null when none of
    /// them carries <see cref="Marker"/>, as when Lean never reached the sorry.
    /// </summary>
    public static ExtractedLemma? Parse(IEnumerable<string> messages, int insertLine)
    {
        foreach (string m in messages)
        {
            string[] lines = m.Replace("\r", "", StringComparison.Ordinal).Split('\n');
            string[] head = lines[0].Split('\t');
            if (head.Length < 3 || head[0] != Marker || lines.Length < 2)
            {
                continue;
            }
            bool term = head[1] == "term";
            string use = head[2];
            string name = use.Split(' ')[0];
            string signature = string.Join('\n', lines.Skip(1)).TrimEnd();
            string text = "theorem " + signature + " := by\n  sorry\n\n";
            string call = term ? (use.Contains(' ', StringComparison.Ordinal) ? "(" + use + ")" : use) : "exact " + use;
            return new ExtractedLemma(name, text, insertLine, call);
        }
        return null;
    }

    /// <summary>The file with the lemma inserted and the sorry at <paramref name="site"/> replaced by its use.</summary>
    public static string Apply(string text, SorrySite site, ExtractedLemma lemma)
    {
        string replaced = text[..site.Offset] + lemma.Call + text[(site.Offset + site.Length)..];
        var lines = replaced.Split('\n').ToList();
        int at = Math.Clamp(lemma.InsertLine, 0, lines.Count);
        lines.InsertRange(at, lemma.Text.TrimEnd('\n').Split('\n').Append(""));
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Ask Lean for the lemma, through a scratch document beside the file (see <see cref="Scratch.CheckAsync"/>); null
    /// if Lean did not produce one. Nothing is written to disk: pass the result to <see cref="Apply"/>.
    /// </summary>
    public static async Task<ExtractedLemma?> RunAsync(LeanServer server, string sourcePath, string text, SorrySite site, string name, CancellationToken ct = default)
    {
        IReadOnlyList<Diagnostic> diags = await Scratch.CheckAsync(server, sourcePath, "Extract", Instrument(text, site, name), ct).ConfigureAwait(false);
        return Parse(diags.Where(d => d.Severity == DiagnosticSeverity.Information).Select(d => d.Message), InsertionLine(text.Split('\n'), site.Line));
    }
}
