using System.Net;
using System.Text;
using LeanStudio.Core.Learn;
using LeanStudio.Lsp;

namespace LeanStudio.Core.Proofs;

/// <summary>One tactic of a walkthrough: what it says, what it does in words, and the goals it leaves.</summary>
public sealed record WalkStep(int Line, string Tactic, string? Explanation, string Change, IReadOnlyList<string> GoalsAfter);

/// <summary>One tactic proof, from the statement and its opening goal to the last step.</summary>
public sealed record WalkProof(string Declaration, int Line, string Statement, string? InWords, IReadOnlyList<string> GoalsBefore, IReadOnlyList<WalkStep> Steps);

/// <summary>
/// A proof walkthrough: every tactic proof in a file, step by step, with the goal before and after each tactic
/// and what the tactic does in plain words, written out as one self-contained web page anyone can open. The
/// infoview, frozen, for people who do not have Lean: a teacher's handout, a blog post, a pull request review.
///
/// Also a share link that opens the code in the Lean 4 web editor (live.lean-lang.org), where it runs as is.
/// </summary>
public static class Walkthrough
{
    public const string WebEditor = "https://live.lean-lang.org/";

    /// <summary>A link that opens <paramref name="code"/> in the Lean 4 web editor (which has Mathlib).</summary>
    public static string ShareUrl(string code) => WebEditor + "#code=" + Uri.EscapeDataString(code);

    /// <summary>The libraries the web editor has; a file importing anything else will not run there.</summary>
    public static IReadOnlyList<string> WebEditorLibraries { get; } =
        ["Init", "Std", "Lean", "Lake", "Mathlib", "Batteries", "Aesop", "Qq", "ProofWidgets", "Plausible", "ImportGraph", "LeanSearchClient"];

    /// <summary>The modules a file imports that the web editor does not have (typically the project's own).</summary>
    public static IReadOnlyList<string> MissingOnWeb(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text[..ProofSearch.HeaderEnd(text)], @"(?m)^\s*(?:(?:public|private|meta)\s+)*import\s+([^\s]+)")
            .Select(m => m.Groups[1].Value)
            .Where(m => !WebEditorLibraries.Contains(m.Split('.')[0]))
            .ToList();

    /// <summary>Every tactic proof in the file, in order.</summary>
    public static IReadOnlyList<TacticProof> Proofs(IReadOnlyList<string> lines)
    {
        var list = new List<TacticProof>();
        int next = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (i < next || lines[i].Length == 0 || char.IsWhiteSpace(lines[i][0]))
            {
                continue;
            }
            if (ProofSteps.Find(lines, i) is TacticProof p && p.DeclarationLine == i && p.Steps.Count > 0)
            {
                list.Add(p);
                next = p.Steps[^1].Line + 1;
            }
        }
        return list;
    }

    /// <summary>Ask Lean for the state around every step of every tactic proof in an elaborated document.</summary>
    public static async Task<IReadOnlyList<WalkProof>> BuildAsync(LeanServer server, string uri, IReadOnlyList<string> lines, CancellationToken ct = default)
    {
        var result = new List<WalkProof>();
        foreach (TacticProof p in Proofs(lines))
        {
            ct.ThrowIfCancellationRequested();
            InteractiveGoals start = await server.InteractiveGoalsAsync(uri, p.Steps[0].Before, ct).ConfigureAwait(false);
            var steps = new List<WalkStep>();
            for (int i = 0; i < p.Steps.Count; i++)
            {
                ProofStep s = p.Steps[i];
                InteractiveGoals before = await server.InteractiveGoalsAsync(uri, s.Before, ct).ConfigureAwait(false);
                InteractiveGoals after = await server.InteractiveGoalsAsync(uri, s.After, ct).ConfigureAwait(false);
                bool continues = ProofSteps.ContinuesBelow(p.Steps, i);
                string change = StepChange.Between(before, after).Summary;
                string? explain = TacticGuide.TacticOf(s.Text) is string t && TacticGuide.Explain(t) is { } e ? e.Explanation : null;
                steps.Add(new WalkStep(s.Line, s.Text, explain, continues && change == "no change" ? "continues below" : change,
                    continues ? [] : after.Goals.Select(g => g.Render()).ToList()));
            }
            string statement = string.Join('\n', lines.Skip(p.DeclarationLine).Take(p.Start.Line - p.DeclarationLine + 1)).TrimEnd();
            string? words = start.Goals.FirstOrDefault() is InteractiveGoal g0 ? PlainEnglish.Read(g0.Type.Text) : null;
            result.Add(new WalkProof(p.Declaration, p.DeclarationLine, statement, words, start.Goals.Select(g => g.Render()).ToList(), steps));
        }
        return result;
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);

    /// <summary>The walkthrough as one web page, with no outside resources, readable in light and dark.</summary>
    public static string Html(string title, IReadOnlyList<WalkProof> proofs, string source)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">\n")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
          .Append("<title>").Append(E(title)).Append(" · proof walkthrough</title>\n<style>\n").Append(Css).Append("</style></head><body>\n<main>\n")
          .Append("<header><h1>").Append(E(title)).Append("</h1><p class=\"sub\">A step-by-step walkthrough of ")
          .Append(proofs.Count).Append(proofs.Count == 1 ? " proof" : " proofs")
          .Append(". Each step shows the tactic, what it does, and the goals Lean is left with. Use ← and → to step through.</p>")
          .Append("<p><a class=\"button\" href=\"").Append(E(ShareUrl(source))).Append("\">Run this file in the Lean 4 web editor ↗</a></p></header>\n");
        if (proofs.Count > 0)
        {
            sb.Append("<nav><strong>Proofs</strong>");
            foreach (WalkProof p in proofs)
            {
                sb.Append(" <a href=\"#p").Append(p.Line).Append("\">").Append(E(p.Declaration)).Append("</a>");
            }
            sb.Append("</nav>\n");
        }
        foreach (WalkProof p in proofs)
        {
            sb.Append("<section class=\"proof\" id=\"p").Append(p.Line).Append("\">\n<h2>").Append(E(p.Declaration))
              .Append(" <span class=\"line\">line ").Append(p.Line + 1).Append("</span></h2>\n")
              .Append("<pre class=\"code\">").Append(E(p.Statement)).Append("</pre>\n");
            if (p.InWords is { Length: > 0 } w)
            {
                sb.Append("<p class=\"words\">In words: ").Append(E(w)).Append("</p>\n");
            }
            sb.Append("<div class=\"state start\"><div class=\"label\">Goal to prove</div>");
            AppendGoals(sb, p.GoalsBefore);
            sb.Append("</div>\n<ol class=\"steps\">\n");
            foreach (WalkStep s in p.Steps)
            {
                bool done = s.Change == "goals accomplished";
                sb.Append("<li class=\"step").Append(done ? " done" : "").Append("\" tabindex=\"0\">")
                  .Append("<div class=\"tactic\"><code>").Append(E(s.Tactic)).Append("</code><span class=\"change\">")
                  .Append(E(s.Change)).Append("</span></div>");
                if (s.Explanation is { Length: > 0 } x)
                {
                    sb.Append("<p class=\"explain\">").Append(E(x)).Append("</p>");
                }
                if (s.Change != "continues below")
                {
                    sb.Append("<div class=\"state\">");
                    AppendGoals(sb, s.GoalsAfter);
                    sb.Append("</div>");
                }
                sb.Append("</li>\n");
            }
            sb.Append("</ol>\n</section>\n");
        }
        sb.Append("<details class=\"source\"><summary>The whole file</summary><pre class=\"code\">").Append(E(source)).Append("</pre></details>\n")
          .Append("<footer>Made with <a href=\"https://github.com/keithadler/leanstudio\">Lean Studio</a>, by Keith Adler "
                  + "(<a href=\"https://x.com/keithadler\">@keithadler</a>). Goals are exactly as Lean reported them.</footer>\n")
          .Append("</main>\n<script>\n").Append(Script).Append("</script>\n</body></html>\n");
        return sb.ToString();
    }

    private static void AppendGoals(StringBuilder sb, IReadOnlyList<string> goals)
    {
        if (goals.Count == 0)
        {
            sb.Append("<div class=\"none\">✓ No goals left: this part of the proof is complete.</div>");
            return;
        }
        foreach (string g in goals)
        {
            sb.Append("<pre class=\"goal\">").Append(E(g)).Append("</pre>");
        }
    }

    private const string Css = """
        :root { --bg:#fbfaf8; --fg:#1d1d1f; --dim:#6b6b70; --card:#ffffff; --line:#e4e2dd; --accent:#3f6fd8; --ok:#1f8a3b; --code:#f3f1ec; }
        @media (prefers-color-scheme: dark) { :root { --bg:#16171a; --fg:#e6e6e9; --dim:#9a9aa3; --card:#1e2024; --line:#2e3036; --accent:#7aa2f7; --ok:#4cc36b; --code:#23252b; } }
        * { box-sizing: border-box; }
        body { margin:0; background:var(--bg); color:var(--fg); font:16px/1.55 -apple-system, "Segoe UI", system-ui, sans-serif; }
        main { max-width: 860px; margin: 0 auto; padding: 32px 16px 64px; }
        h1 { font-size: 28px; margin: 0 0 4px; } h2 { font-size: 20px; margin: 0 0 12px; }
        .sub, .line, .words, footer, .explain { color: var(--dim); }
        .line { font-size: 13px; font-weight: normal; margin-left: 6px; }
        a { color: var(--accent); }
        .button { display:inline-block; padding:6px 14px; border:1px solid var(--accent); border-radius:6px; text-decoration:none; }
        nav { margin: 20px 0; display:flex; flex-wrap:wrap; gap:6px 12px; font-size:14px; }
        pre, code { font-family: "JetBrains Mono", "SF Mono", Menlo, Consolas, monospace; font-size: 14px; }
        pre { margin: 0; white-space: pre-wrap; word-break: break-word; }
        pre.code { background: var(--code); padding: 12px 14px; border-radius: 8px; }
        .proof { background: var(--card); border: 1px solid var(--line); border-radius: 12px; padding: 20px; margin: 24px 0; }
        .state { margin-top: 8px; }
        .state.start { margin: 12px 0 4px; }
        .label { font-size: 12px; text-transform: uppercase; letter-spacing: .06em; color: var(--dim); margin-bottom: 4px; }
        .goal { border-left: 3px solid var(--accent); padding: 6px 10px; margin: 4px 0; background: var(--code); border-radius: 0 6px 6px 0; }
        .none { color: var(--ok); font-weight: 600; }
        ol.steps { list-style: none; counter-reset: s; padding: 0; margin: 16px 0 0; }
        .step { counter-increment: s; position: relative; padding: 12px 12px 12px 44px; border-top: 1px solid var(--line); outline: none; }
        .step::before { content: counter(s); position: absolute; left: 8px; top: 12px; width: 24px; height: 24px; border-radius: 50%;
                        background: var(--code); color: var(--dim); font-size: 13px; text-align: center; line-height: 24px; }
        .step.done::before { background: var(--ok); color: #fff; content: "✓"; }
        .step.current { background: color-mix(in srgb, var(--accent) 8%, transparent); border-radius: 8px; }
        .tactic { display: flex; flex-wrap: wrap; gap: 6px 12px; align-items: baseline; }
        .tactic code { font-weight: 600; }
        .change { font-size: 13px; color: var(--dim); }
        .explain { margin: 4px 0 0; font-size: 14px; }
        details.source { margin-top: 24px; } footer { margin-top: 32px; font-size: 13px; }
        """;

    private const string Script = """
        const steps = [...document.querySelectorAll('.step')];
        let at = -1;
        function go(i) {
          if (i < 0 || i >= steps.length) return;
          steps.forEach(s => s.classList.remove('current'));
          at = i; steps[i].classList.add('current'); steps[i].focus({ preventScroll: true });
          steps[i].scrollIntoView({ block: 'center', behavior: 'smooth' });
        }
        steps.forEach((s, i) => s.addEventListener('click', () => go(i)));
        document.addEventListener('keydown', e => {
          if (e.key === 'ArrowRight' || e.key === 'j') { go(at + 1); e.preventDefault(); }
          if (e.key === 'ArrowLeft' || e.key === 'k') { go(at - 1); e.preventDefault(); }
        });
        """;
}
