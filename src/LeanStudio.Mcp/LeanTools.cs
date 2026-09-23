using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using LeanStudio.Core.Agents;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Proofs;
using LeanStudio.Core.Toolchains;
using LeanStudio.Core.Verification;
using LeanStudio.Lsp;

namespace LeanStudio.Mcp;

/// <summary>
/// The tools Lean Studio offers an AI assistant. Each takes plain arguments (paths, 1-based lines and columns,
/// declaration names) and answers in text written for a model to read: Lean's messages with locations, goals
/// with what the last tactic changed, Tenet's verdicts. Paths may be absolute or relative to the project the
/// server was started in.
/// </summary>
public static class LeanTools
{
    public const string Instructions = """
        Lean Studio gives you Lean 4 itself: Lean's language server and build tool, and Tenet, an independent
        checker of Lean's output. Use it to write and fix Lean with real feedback instead of guessing.

        How to work:
        - After you edit a .lean file, call check_file on it. It waits for Lean to finish and returns every error,
          warning and message with its line and column. Pass `content` to check text without saving it.
        - To see what a tactic proof needs, call goals at a line and column inside the proof, or proof_steps on
          any line of it for every step's resulting state. Lines and columns are 1-based.
        - Write `exact?`, `apply?`, `simp?` or `rw?` where you are stuck, then call suggestions on that line: Lean
          searches for a proof, and suggestions can apply the one you choose.
        - run_lean checks a snippet (#eval, #check, #print axioms, an example) inside the project, so its imports work.
        - A theorem is only proved when check_file shows no errors and no "declaration uses 'sorry'" warning.
          For a stronger answer, run build and then verify: Tenet re-checks every declaration with a second kernel
          and lists any that rest on sorry or on axioms the project introduces.
        - Stuck on a goal? Leave `sorry` there and call prove: it tries rfl, decide, simp, omega, norm_num, ring,
          linarith, aesop, grind, exact? and more on each sorry, and can write the first one that works.
        - search_declarations, declaration and axioms read the compiled library (Mathlib included once built).
          search_mathlib finds Mathlib results from a description in plain English when you do not know a name.
        - If verify says a theorem rests on sorry, why_not_proved shows the chain of lemmas down to the one to fix.
        - profile shows which declarations make a file slow to check, and the step inside each that costs most.
        - If the person has Lean Studio open, studio_context tells you which file, line and goal they are looking
          at, and studio_show opens a file at a line in their window so they can review your change.
        """;

    public static McpServer Create(Workbench bench, string version) =>
        new("leanstudio", version, Instructions, Tools(bench));

    // ---- schema helpers ----

    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, description, req) in props)
        {
            JsonObject p = type == "string[]"
                ? new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = description }
                : new JsonObject { ["type"] = type, ["description"] = description };
            properties[name] = p;
            if (req)
            {
                required.Add(name);
            }
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }

    private static string Str(JsonObject a, string name) =>
        a[name]?.GetValue<string>() is { Length: > 0 } s ? s : throw new ToolException($"missing required argument '{name}'");

    private static string? OptStr(JsonObject a, string name) => a[name] is JsonValue v && v.TryGetValue(out string? s) && s.Length > 0 ? s : null;

    private static int Int(JsonObject a, string name)
    {
        JsonNode? n = a[name] ?? throw new ToolException($"missing required argument '{name}'");
        if (n is JsonValue v && v.TryGetValue(out int i))
        {
            return i;
        }
        if (n is JsonValue s && s.TryGetValue(out string? str) && int.TryParse(str, CultureInfo.InvariantCulture, out int j))
        {
            return j;
        }
        throw new ToolException($"'{name}' must be a number");
    }

    private static int? OptInt(JsonObject a, string name) => a.ContainsKey(name) ? Int(a, name) : null;

    private static string LeanFile(Workbench bench, JsonObject a)
    {
        string path = bench.Resolve(Str(a, "path"));
        if (!File.Exists(path) && OptStr(a, "content") is null)
        {
            throw new ToolException($"no such file: {path}");
        }
        return path;
    }

    // ---- the tools ----

    public static IReadOnlyList<McpTool> Tools(Workbench bench) =>
    [
        new("project_info",
            "Describe the Lean project: its root, Lean toolchain, whether it is a Lake project, whether it uses Mathlib, whether it has been built, and its source files.",
            Schema(("path", "string", "Any file or folder in the project; defaults to the project the server was started in.", false)),
            (a, ct) => Task.FromResult(ProjectInfo(bench.ProjectFor(OptStr(a, "path"))))),

        new("check_file",
            "Elaborate a Lean file and return every error, warning and message Lean reports, with 1-based line:column. Waits until Lean has finished. Pass `content` to check text without writing it to disk.",
            Schema(("path", "string", "The .lean file (absolute, or relative to the project).", true),
                   ("content", "string", "Optional full text to check instead of what is on disk.", false)),
            async (a, ct) =>
            {
                string path = LeanFile(bench, a);
                FileReport r = await bench.Session(path).CheckAsync(path, OptStr(a, "content"), ct);
                return FormatReport(r);
            }),

        new("goals",
            "The goals and hypotheses at a position in a Lean file: what remains to prove there. Marks hypotheses the tactic at that position adds (+) or removes (-). Lines and columns are 1-based.",
            Schema(("path", "string", "The .lean file.", true),
                   ("line", "integer", "1-based line.", true),
                   ("column", "integer", "1-based column; put it on or just after a tactic.", true)),
            async (a, ct) =>
            {
                string path = LeanFile(bench, a);
                ProjectSession s = bench.Session(path);
                FileReport r = await s.CheckAsync(path, null, ct);
                var pos = new Position(Int(a, "line") - 1, Math.Max(0, Int(a, "column") - 1));
                LeanServer server = await s.ServerAsync(ct);
                InteractiveGoals goals = await server.InteractiveGoalsAsync(r.Uri, pos, ct);
                var sb = new StringBuilder();
                sb.Append(FormatGoals(goals));
                InteractiveGoals term = await server.InteractiveTermGoalAsync(r.Uri, pos, ct);
                if (term.Goals.FirstOrDefault() is InteractiveGoal t)
                {
                    sb.Append("\n\nexpected type here: ").Append(t.Type.Text);
                }
                var here = r.Diagnostics.Where(d => d.Extent.Start.Line <= pos.Line && pos.Line <= d.Extent.End.Line).ToList();
                if (here.Count > 0)
                {
                    sb.Append("\n\nmessages on this line:\n").Append(string.Join('\n', here.Select(d => FormatDiagnostic(r.Path, d))));
                }
                return sb.ToString();
            }),

        new("proof_steps",
            "Every step of the tactic proof containing a line: each tactic, the state it leaves, and what it changed (hypotheses added/removed, goals opened or closed). Use it to see where a proof goes wrong.",
            Schema(("path", "string", "The .lean file.", true),
                   ("line", "integer", "Any 1-based line inside the proof.", true)),
            async (a, ct) =>
            {
                string path = LeanFile(bench, a);
                ProjectSession s = bench.Session(path);
                FileReport r = await s.CheckAsync(path, null, ct);
                string[] lines = r.Text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
                TacticProof proof = ProofSteps.Find(lines, Int(a, "line") - 1)
                    ?? throw new ToolException("that line is not inside a tactic proof (a declaration whose body starts with `by`)");
                LeanServer server = await s.ServerAsync(ct);
                var sb = new StringBuilder();
                sb.Append(CultureInfo.InvariantCulture, $"proof of {proof.Declaration} (line {proof.DeclarationLine + 1}), {proof.Steps.Count} steps\n");
                InteractiveGoals start = await server.InteractiveGoalsAsync(r.Uri, proof.Start, ct);
                sb.Append("\nstarting state:\n").Append(Indent(FormatGoals(start))).Append('\n');
                for (int i = 0; i < proof.Steps.Count; i++)
                {
                    ProofStep step = proof.Steps[i];
                    InteractiveGoals before = await server.InteractiveGoalsAsync(r.Uri, step.Before, ct);
                    InteractiveGoals after = await server.InteractiveGoalsAsync(r.Uri, step.After, ct);
                    StepChange change = StepChange.Between(before, after);
                    var errors = r.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error && d.Extent.Start.Line <= step.Line && step.Line <= d.Extent.End.Line).ToList();
                    string summary = ProofSteps.ContinuesBelow(proof.Steps, i) && change.Summary == "no change"
                        ? "continues on the lines below"
                        : $"{change.Summary}; {after.Goals.Count} goal{(after.Goals.Count == 1 ? "" : "s")} left";
                    sb.Append(CultureInfo.InvariantCulture, $"\nline {step.Line + 1}: {step.Text}\n  → {summary}\n");
                    foreach (Diagnostic e in errors)
                    {
                        sb.Append("  error: ").Append(e.Message.Replace("\n", "\n    ", StringComparison.Ordinal)).Append('\n');
                    }
                    if (after.Goals.Count > 0 && (change.Added.Count > 0 || change.Changed.Count > 0 || change.TargetChanged || errors.Count > 0))
                    {
                        sb.Append(Indent(FormatGoals(after), "    ")).Append('\n');
                    }
                }
                return sb.ToString().TrimEnd();
            }),

        new("hover",
            "What Lean shows when hovering a position: the type and documentation of the name or term there. 1-based line and column.",
            Schema(("path", "string", "The .lean file.", true),
                   ("line", "integer", "1-based line.", true),
                   ("column", "integer", "1-based column.", true)),
            async (a, ct) =>
            {
                string path = LeanFile(bench, a);
                ProjectSession s = bench.Session(path);
                FileReport r = await s.CheckAsync(path, null, ct);
                Hover? h = await (await s.ServerAsync(ct)).HoverAsync(r.Uri, new Position(Int(a, "line") - 1, Math.Max(0, Int(a, "column") - 1)), ct);
                return h is null || h.Contents.Trim().Length == 0 ? "nothing to show at that position" : h.Contents.Trim();
            }),

        new("suggestions",
            "Lean's suggestions on a line: the \"Try this\" results of exact?, apply?, simp?, rw? and other tactics, and quick fixes. Put such a tactic in the proof, call this on its line, and pass `apply` (1-based) to write the chosen suggestion into the file.",
            Schema(("path", "string", "The .lean file.", true),
                   ("line", "integer", "1-based line of the tactic or message.", true),
                   ("apply", "integer", "Optional: the number of the suggestion to apply (from a previous call); the file is edited on disk.", false)),
            async (a, ct) =>
            {
                string path = LeanFile(bench, a);
                ProjectSession s = bench.Session(path);
                FileReport r = await s.CheckAsync(path, null, ct);
                int line = Int(a, "line") - 1;
                LeanServer server = await s.ServerAsync(ct);
                var here = r.Diagnostics.Where(d => d.Extent.Start.Line <= line && line <= d.Extent.End.Line).ToList();
                var actions = new List<CodeAction>();
                foreach (Diagnostic d in here)
                {
                    actions.AddRange(await server.CodeActionsAsync(r.Uri, d.Range, [d], ct));
                }
                if (here.Count == 0)
                {
                    actions.AddRange(await server.CodeActionsAsync(r.Uri, new Lsp.Range(new Position(line, 0), new Position(line, 0)), [], ct));
                }
                actions = actions.GroupBy(x => x.Title).Select(g => g.First()).ToList();
                if (actions.Count == 0)
                {
                    return "No suggestions on that line. Put `exact?`, `apply?`, `simp?` or `rw?` there, then ask again.";
                }
                if (OptInt(a, "apply") is int pick)
                {
                    if (pick < 1 || pick > actions.Count)
                    {
                        throw new ToolException($"apply must be between 1 and {actions.Count}");
                    }
                    CodeAction chosen = await server.ResolveAsync(actions[pick - 1], ct);
                    if (chosen.Edit is not WorkspaceEdit edit || !edit.Changes.TryGetValue(r.Uri, out IReadOnlyList<TextEdit>? edits))
                    {
                        throw new ToolException("that suggestion has no edit to this file");
                    }
                    string updated = WorkspaceEdit.Apply(r.Text, edits);
                    await File.WriteAllTextAsync(path, updated, ct);
                    FileReport after = await s.CheckAsync(path, null, ct);
                    return $"Applied \"{chosen.Title}\" and saved the file.\n\n" + FormatReport(after);
                }
                return string.Join('\n', actions.Select((x, i) => $"{i + 1}. {x.Title}")) + "\n\nCall again with apply=<number> to use one.";
            }),

        new("references",
            "Every place a name is used, across the project (1-based line and column of the name).",
            Schema(("path", "string", "The .lean file.", true),
                   ("line", "integer", "1-based line.", true),
                   ("column", "integer", "1-based column on the name.", true)),
            async (a, ct) =>
            {
                string path = LeanFile(bench, a);
                ProjectSession s = bench.Session(path);
                FileReport r = await s.CheckAsync(path, null, ct);
                IReadOnlyList<Location> locs = await (await s.ServerAsync(ct)).ReferencesAsync(r.Uri, new Position(Int(a, "line") - 1, Math.Max(0, Int(a, "column") - 1)), ct: ct);
                if (locs.Count == 0)
                {
                    return "No references found (is the column on a name?).";
                }
                return string.Join('\n', locs.Select(l =>
                {
                    string file = LeanServer.PathOf(l.Uri);
                    string text = File.Exists(file) ? File.ReadLines(file).Skip(l.Range.Start.Line).FirstOrDefault()?.Trim() ?? "" : "";
                    return $"{Path.GetRelativePath(s.Project.Root, file)}:{l.Range.Start.Line + 1}:{l.Range.Start.Character + 1}  {text}";
                }));
            }),

        new("run_lean",
            "Check a Lean snippet inside the project (so its imports resolve) and return what Lean says: #eval output, #check types, #print axioms, errors. The snippet is a complete file: put `import` lines first.",
            Schema(("code", "string", "Lean source, e.g. \"import Mathlib\\n#eval 2 + 2\".", true),
                   ("project", "string", "Any path in the project to run it in; defaults to the server's project.", false)),
            async (a, ct) =>
            {
                ProjectSession s = bench.Session(OptStr(a, "project"));
                string dir = Path.Combine(s.Project.Root, ".lake", "leanstudio");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "Scratch.lean");
                string code = Str(a, "code");
                await File.WriteAllTextAsync(file, code, ct);
                FileReport r = await s.CheckAsync(file, code, ct);
                return r.Diagnostics.Count == 0 ? "Lean accepted the snippet with no messages." : FormatDiagnostics(r, "snippet");
            }),

        new("build",
            "Build the project with `lake build` (or one target) and return the result with the end of Lake's output. Required before verify, and before other files can import a changed module.",
            Schema(("project", "string", "Any path in the project; defaults to the server's project.", false),
                   ("target", "string", "Optional Lake target or module, e.g. `Mathlib.Data.Nat.Basic`.", false)),
            async (a, ct) =>
            {
                ProjectSession s = bench.Session(OptStr(a, "project"));
                if (!s.Project.IsLakeProject)
                {
                    throw new ToolException($"{s.Project.Root} is not a Lake project (no lakefile), so there is nothing to build");
                }
                var lines = new List<string>();
                var result = await Lake.BuildAsync(s.Project, OptStr(a, "target"), l => { lock (lines) { lines.Add(l); } }, ct);
                s.BuildChanged();
                string tail;
                lock (lines)
                {
                    tail = string.Join('\n', lines.TakeLast(80));
                }
                return (result.Success ? "Build succeeded." : $"Build FAILED (exit {result.ExitCode}).") + "\n\n" + tail;
            }),

        new("verify",
            "Re-check the project's built declarations with Tenet, an independent Lean kernel, and report which are verified, which rest on sorry or on axioms the project introduces, and which Tenet rejects. Run build first.",
            Schema(("project", "string", "Any path in the project; defaults to the server's project.", false),
                   ("modules", "string[]", "Optional module names to check, e.g. [\"MyProject.Basic\"]; default all of the project's own.", false)),
            async (a, ct) =>
            {
                ProjectSession s = bench.Session(OptStr(a, "project"));
                TenetWorkspace ws = s.Tenet();
                if (ws.OwnModules.Count == 0)
                {
                    throw new ToolException("nothing built yet: call build first");
                }
                List<string>? modules = a["modules"] is JsonArray arr ? arr.Select(n => n!.GetValue<string>()).ToList() : null;
                VerificationReport r = await ws.VerifyAsync(modules, ct: ct);
                return FormatVerification(r);
            }),

        new("axioms",
            "Every axiom a declaration depends on, transitively, computed by Tenet from the compiled library (like #print axioms). sorryAx means it rests on sorry.",
            Schema(("name", "string", "Fully qualified declaration name, e.g. Nat.add_comm.", true),
                   ("project", "string", "Any path in the project; defaults to the server's project.", false)),
            (a, ct) =>
            {
                TenetWorkspace ws = bench.Session(OptStr(a, "project")).Tenet();
                string name = Str(a, "name");
                if (ws.Details(name) is null)
                {
                    throw new ToolException($"{name} is not in the compiled library (is the project built, and is the name fully qualified?)");
                }
                IReadOnlyList<string> axioms = ws.AxiomsOf(name);
                return Task.FromResult(axioms.Count == 0 ? $"{name} depends on no axioms." : $"{name} depends on:\n" + string.Join('\n', axioms.Select(x => "  " + x)));
            }),

        new("why_not_proved",
            "Why a declaration is not fully proved: for each sorry or project axiom it rests on, the shortest chain of declarations leading to it, with file and line of each. The last declaration before the sorry is the one to fix. Run build first.",
            Schema(("name", "string", "Fully qualified declaration name.", true),
                   ("project", "string", "Any path in the project; defaults to the server's project.", false)),
            (a, ct) =>
            {
                TenetWorkspace ws = bench.Session(OptStr(a, "project")).Tenet();
                string name = Str(a, "name");
                if (ws.Details(name) is null)
                {
                    throw new ToolException($"{name} is not in the compiled library (is the project built, and is the name fully qualified?)");
                }
                IReadOnlyList<AssumptionTrail> trails = ws.WhyNotProved(name, ct);
                if (trails.Count == 0)
                {
                    return Task.FromResult($"{name} is fully proved: it rests on no sorry and no axiom beyond propext, Classical.choice and Quot.sound.");
                }
                var sb = new StringBuilder();
                foreach (AssumptionTrail t in trails)
                {
                    sb.Append(t.IsSorry ? "rests on sorry" : $"rests on the axiom {t.Assumption}").Append(":\n");
                    foreach (TrailLink l in t.Path)
                    {
                        sb.Append("  ").Append(l.Display);
                        if (l.SourceFile is not null && l.Line is int line)
                        {
                            sb.Append(CultureInfo.InvariantCulture, $"  ({l.SourceFile}:{line})");
                        }
                        sb.Append('\n');
                    }
                    if (t.Culprit is TrailLink c)
                    {
                        sb.Append("  → fix ").Append(c.Name).Append(t.IsSorry ? ", which uses sorry itself" : $", which uses {t.Assumption} directly").Append('\n');
                    }
                }
                return Task.FromResult(sb.ToString().TrimEnd());
            }),

        new("prove",
            "Try a portfolio of tactics (rfl, decide, simp, omega, norm_num, ring, linarith, aesop, grind, exact? and more) on the goal at each sorry in a file, independently, and report which close it and how long each took. Pass `line` to try only the sorry on that line, and `apply` true to write the first working tactic in place of each sorry.",
            Schema(("path", "string", "The .lean file.", true),
                   ("line", "integer", "Optional 1-based line: only the sorry on (or nearest) this line.", false),
                   ("apply", "boolean", "Write the first tactic that works in place of each sorry it proves.", false)),
            async (a, ct) =>
            {
                string path = LeanFile(bench, a);
                ProjectSession s = bench.Session(path);
                FileReport r = await s.CheckAsync(path, null, ct);
                IReadOnlyList<SorrySite> sites = ProofSearch.Sites(r.Text);
                if (OptInt(a, "line") is int ln)
                {
                    sites = ProofSearch.At(sites, ln - 1, 0) is SorrySite one ? [one] : [];
                }
                if (sites.Count == 0)
                {
                    return "no sorry to prove there";
                }
                IReadOnlyList<SearchResult> results = await ProofSearch.RunAsync(await s.ServerAsync(ct), path, r.Text, sites, ct);
                var sb = new StringBuilder();
                foreach (SearchResult res in results)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"sorry at line {res.Site.Line + 1}, column {res.Site.Column + 1}")
                      .Append(res.Site.Declaration is string d ? $" in {d}" : "").Append(":\n");
                    if (!res.Reached)
                    {
                        sb.Append("  not reached (an error earlier in the file stops Lean before it)\n");
                        continue;
                    }
                    foreach (TacticTrial t in res.Trials.Where(t => t.Outcome != TrialOutcome.Unavailable))
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"  {(t.Closes ? "closes" : "fails ")}  {(t.Closes ? t.Replacement : t.Tactic)}  ({t.Time})\n");
                    }
                    sb.Append(res.Best is TacticTrial b ? $"  best: {res.Fill(b)}\n" : "  none of the tactics closes this goal\n");
                }
                bool apply = a["apply"] is JsonValue v && v.TryGetValue(out bool ap) && ap;
                if (apply && results.Any(x => x.Best is not null))
                {
                    string filled = ProofSearch.Apply(r.Text, results.Where(x => x.Best is not null).Select(x => (x, x.Best!)));
                    await File.WriteAllTextAsync(path, filled, ct);
                    sb.Append(CultureInfo.InvariantCulture, $"\nwrote {results.Count(x => x.Best is not null)} proof(s) into {path}; call check_file to confirm.");
                }
                return sb.ToString().TrimEnd();
            }),

        new("profile",
            "Lean's profiler over a file: how long each declaration takes to elaborate, slowest first, and the step inside it that costs the most. Use it to find what makes a file slow. The file's imports must be built.",
            Schema(("path", "string", "The .lean file.", true),
                   ("content", "string", "Optional full text to profile instead of what is on disk.", false)),
            async (a, ct) =>
            {
                string path = LeanFile(bench, a);
                LeanProject project = bench.ProjectFor(path);
                string text = OptStr(a, "content") ?? await File.ReadAllTextAsync(path, ct);
                var (timings, error) = await Profiler.RunAsync(project, path, text, ct);
                if (error is not null)
                {
                    throw new ToolException(error);
                }
                if (timings.Count == 0)
                {
                    return "nothing in this file takes more than a few milliseconds";
                }
                double total = timings.Sum(t => t.Seconds);
                var sb = new StringBuilder(DeclarationTiming.Format(total) + " in total; slowest first:\n");
                foreach (DeclarationTiming t in timings.Take(25))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"  line {t.Line + 1}: {t.Detail}\n    {t.Declaration}\n");
                }
                return sb.ToString().TrimEnd();
            }),

        new("export_walkthrough",
            "Write a proof walkthrough of a Lean file as one self-contained web page: every tactic proof, step by step, with the goals before and after each tactic and what the tactic does in plain words. Also returns a link that opens the file in the Lean 4 web editor.",
            Schema(("path", "string", "The .lean file.", true),
                   ("output", "string", "Where to write the .html file; defaults to <file>-walkthrough.html beside it.", false)),
            async (a, ct) =>
            {
                string path = LeanFile(bench, a);
                ProjectSession s = bench.Session(path);
                FileReport r = await s.CheckAsync(path, null, ct);
                string[] lines = r.Text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
                IReadOnlyList<WalkProof> proofs = await Walkthrough.BuildAsync(await s.ServerAsync(ct), r.Uri, lines, ct);
                if (proofs.Count == 0)
                {
                    throw new ToolException("there are no tactic proofs (… := by …) in this file");
                }
                string output = OptStr(a, "output") is string o ? bench.Resolve(o)
                    : Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-walkthrough.html");
                await File.WriteAllTextAsync(output, Walkthrough.Html(Path.GetFileName(path), proofs, r.Text), ct);
                return $"wrote a walkthrough of {proofs.Count} proof(s) to {output}\nopen it in the Lean 4 web editor: {Walkthrough.ShareUrl(r.Text)}";
            }),

        new("search_mathlib",
            "Search Mathlib by meaning, in plain English (e.g. \"the sum of the first n odd numbers is n squared\"), with LeanSearch (leansearch.net). Returns names, statements and informal descriptions. Use it when you do not know a lemma's name; use search_declarations or Loogle-style name search when you do.",
            Schema(("question", "string", "What the result says, in words.", true),
                   ("limit", "integer", "How many results (default 10, at most 50).", false)),
            async (a, ct) =>
            {
                int limit = Math.Clamp(OptInt(a, "limit") ?? 10, 1, 50);
                IReadOnlyList<Core.Workflow.MeaningHit> hits;
                try
                {
                    hits = await new Core.Workflow.LeanSearch().SearchAsync(Str(a, "question"), limit, ct);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    throw new ToolException("could not reach LeanSearch: " + e.Message);
                }
                if (hits.Count == 0)
                {
                    return "no results";
                }
                var sb = new StringBuilder();
                foreach (Core.Workflow.MeaningHit h in hits)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"{h.Name}  ({h.Kind}, {h.Module})\n  {h.Type}\n");
                    if (h.InformalStatement is { Length: > 0 } inf)
                    {
                        sb.Append("  ").Append(inf.Replace("\n", " ", StringComparison.Ordinal)).Append('\n');
                    }
                }
                return sb.ToString().TrimEnd();
            }),

        new("declaration",
            "A declaration from the compiled library: kind, type, docstring, module, source location and what it uses.",
            Schema(("name", "string", "Fully qualified declaration name.", true),
                   ("project", "string", "Any path in the project; defaults to the server's project.", false)),
            (a, ct) =>
            {
                TenetWorkspace ws = bench.Session(OptStr(a, "project")).Tenet();
                DeclarationDetails d = ws.Details(Str(a, "name")) ?? throw new ToolException($"{Str(a, "name")} is not in the compiled library");
                var sb = new StringBuilder();
                sb.Append(CultureInfo.InvariantCulture, $"{d.Kind} {d.Name}\n  type: {d.Type}\n  module: {d.Module}\n");
                if (d.SourceFile is not null)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"  source: {d.SourceFile}:{d.Line}\n");
                }
                if (d.Deprecation is not null)
                {
                    sb.Append("  ").Append(d.Deprecation).Append('\n');
                }
                if (d.DocString is not null)
                {
                    sb.Append("  doc: ").Append(d.DocString.Trim().Replace("\n", "\n       ", StringComparison.Ordinal)).Append('\n');
                }
                sb.Append(CultureInfo.InvariantCulture, $"  uses ({d.Uses.Count}): {string.Join(", ", d.Uses.Take(60))}{(d.Uses.Count > 60 ? ", …" : "")}");
                return Task.FromResult(sb.ToString());
            }),

        new("search_declarations",
            "Search declaration names in the compiled library: the project, its dependencies (Mathlib included) and Lean's core. Every space-separated word must appear in the name, case-insensitively.",
            Schema(("query", "string", "Words in the name, e.g. \"add comm\" or \"Nat.succ_le\".", true),
                   ("limit", "integer", "At most this many results (default 50).", false),
                   ("project", "string", "Any path in the project; defaults to the server's project.", false)),
            (a, ct) =>
            {
                TenetWorkspace ws = bench.Session(OptStr(a, "project")).Tenet();
                var hits = ws.Search(Str(a, "query"), Math.Clamp(OptInt(a, "limit") ?? 50, 1, 500), ct);
                return Task.FromResult(hits.Count == 0 ? "no declarations match" : string.Join('\n', hits.Select(h => $"{h.Name}    ({h.Module})")));
            }),

        new("toolchains",
            "The Lean toolchains elan has installed and the one the project pins.",
            Schema(("project", "string", "Any path in the project; defaults to the server's project.", false)),
            async (a, ct) =>
            {
                LeanProject p = bench.ProjectFor(OptStr(a, "project"));
                IReadOnlyList<Toolchain> list = await Elan.ListAsync(ct);
                return $"project pins: {p.Toolchain ?? "(nothing: no lean-toolchain file)"}\ninstalled:\n"
                       + (list.Count == 0 ? "  (none)" : string.Join('\n', list.Select(t => "  " + t.Name + (t.IsDefault ? " (default)" : ""))));
            }),

        new("studio_context",
            "What the person is looking at in Lean Studio right now: the open file, cursor position, selected text, the goals at the cursor, and messages on that line. Needs Lean Studio to be running.",
            Schema(),
            async (a, ct) =>
            {
                JsonObject? r = await StudioBridge.RequestAsync(new JsonObject { ["method"] = "context" }, ct: ct);
                if (r is null)
                {
                    throw new ToolException("Lean Studio is not running (or not answering), so there is no editor context.");
                }
                return FormatContext(r);
            }),

        new("studio_show",
            "Open a file in the person's Lean Studio window at a 1-based line and column, so they can see a change or a problem. Needs Lean Studio to be running.",
            Schema(("path", "string", "The file to show.", true),
                   ("line", "integer", "1-based line (default 1).", false),
                   ("column", "integer", "1-based column (default 1).", false)),
            async (a, ct) =>
            {
                string path = bench.Resolve(Str(a, "path"));
                JsonObject? r = await StudioBridge.RequestAsync(new JsonObject
                {
                    ["method"] = "show",
                    ["path"] = path,
                    ["line"] = OptInt(a, "line") ?? 1,
                    ["column"] = OptInt(a, "column") ?? 1,
                }, ct: ct);
                if (r is null)
                {
                    throw new ToolException("Lean Studio is not running (or not answering).");
                }
                return r["error"] is JsonNode e ? throw new ToolException(e.GetValue<string>()) : $"Showing {path} in Lean Studio.";
            }),
    ];

    // ---- formatting ----

    private static string ProjectInfo(LeanProject p)
    {
        var sb = new StringBuilder();
        sb.Append("root: ").Append(p.Root).Append('\n');
        sb.Append("toolchain: ").Append(p.Toolchain ?? "(not pinned)").Append('\n');
        sb.Append("lake project: ").Append(p.IsLakeProject ? "yes (" + Path.GetFileName(p.Lakefile) + ")" : "no").Append('\n');
        sb.Append("uses Mathlib: ").Append(p.DependsOnMathlib ? "yes" : "no").Append('\n');
        sb.Append("built: ").Append(Directory.Exists(p.BuildLibDirectory) ? "yes" : "no").Append('\n');
        var files = p.SourceFiles().Select(f => Path.GetRelativePath(p.Root, f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        sb.Append(CultureInfo.InvariantCulture, $"source files ({files.Count}):\n");
        foreach (string f in files.Take(200))
        {
            sb.Append("  ").Append(f).Append('\n');
        }
        if (files.Count > 200)
        {
            sb.Append("  …\n");
        }
        return sb.ToString().TrimEnd();
    }

    private static string Severity(DiagnosticSeverity s) => s switch
    {
        DiagnosticSeverity.Error => "error",
        DiagnosticSeverity.Warning => "warning",
        DiagnosticSeverity.Information => "info",
        _ => "hint",
    };

    public static string FormatDiagnostic(string path, Diagnostic d) =>
        $"{Path.GetFileName(path)}:{d.Range.Start.Line + 1}:{d.Range.Start.Character + 1}: {Severity(d.Severity)}: {d.Message.Trim().Replace("\n", "\n    ", StringComparison.Ordinal)}";

    private static string FormatDiagnostics(FileReport r, string? label = null) =>
        string.Join('\n', r.Diagnostics.OrderBy(d => d.Range.Start).Select(d => FormatDiagnostic(label ?? r.Path, d)));

    public static string FormatReport(FileReport r)
    {
        string verdict = r.Errors > 0 ? $"{r.Errors} error{(r.Errors == 1 ? "" : "s")}"
            : r.UsesSorry ? "no errors, but something uses sorry, so it is not proved"
            : "no errors";
        string head = $"{r.Path}: {verdict}" + (r.Warnings > 0 ? $", {r.Warnings} warning{(r.Warnings == 1 ? "" : "s")}" : "");
        return r.Diagnostics.Count == 0 ? head + ". Lean accepts the file." : head + "\n\n" + FormatDiagnostics(r);
    }

    public static string FormatGoals(InteractiveGoals goals)
    {
        if (goals.Goals.Count == 0)
        {
            return "no goals";
        }
        var sb = new StringBuilder();
        sb.Append(goals.Goals.Count == 1 ? "1 goal" : $"{goals.Goals.Count} goals");
        foreach (InteractiveGoal g in goals.Goals)
        {
            sb.Append("\n\n");
            if (g.UserName is not null)
            {
                sb.Append("case ").Append(g.UserName);
                sb.Append(g.IsRemoved ? "   (closed by this tactic)" : g.IsInserted ? "   (new)" : "");
                sb.Append('\n');
            }
            foreach (InteractiveHypothesis h in g.Hypotheses)
            {
                sb.Append(h.IsInserted ? "+ " : h.IsRemoved ? "- " : "  ");
                sb.Append(string.Join(' ', h.Names)).Append(" : ").Append(h.Type.Text);
                if (h.Value is not null)
                {
                    sb.Append(" := ").Append(h.Value.Text);
                }
                sb.Append('\n');
            }
            sb.Append(g.GoalPrefix).Append(g.Type.Text);
        }
        return sb.ToString();
    }

    private static string Indent(string s, string by = "  ") => by + s.Replace("\n", "\n" + by, StringComparison.Ordinal);

    public static string FormatVerification(VerificationReport r)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture,
            $"Tenet checked {r.Declarations.Count} declarations in {r.ModulesChecked} modules (Lean {r.LeanVersion}): "
            + $"{r.Verified} verified, {r.Conditional} resting on an assumption, {r.Rejected} rejected.\n");
        foreach (DeclarationVerdict d in r.Declarations.Where(d => d.Status == VerificationStatus.Rejected))
        {
            sb.Append(CultureInfo.InvariantCulture, $"\nREJECTED {d.Name} ({d.Module}{(d.Line is int l ? $":{l}" : "")}): {d.Message}");
        }
        foreach (DeclarationVerdict d in r.Declarations.Where(d => d.Status == VerificationStatus.RestsOnAssumption))
        {
            string why = d.Assumptions.Contains(d.Name) ? "is an axiom the project introduces"
                : "rests on " + string.Join(", ", d.Assumptions.Select(x => x == "sorryAx" ? "sorry" : x));
            sb.Append(CultureInfo.InvariantCulture, $"\n{d.Name} ({d.Module}{(d.Line is int l ? $":{l}" : "")}) {why}");
        }
        var ok = r.Declarations.Where(d => d.Status == VerificationStatus.Verified).Select(d => d.Name).ToList();
        if (ok.Count > 0)
        {
            sb.Append("\n\nverified: ").Append(string.Join(", ", ok.Take(100))).Append(ok.Count > 100 ? ", …" : "");
        }
        return sb.ToString();
    }

    private static string FormatContext(JsonObject r)
    {
        if (r["file"] is null)
        {
            return "Lean Studio is open" + (r["project"] is JsonNode p ? $" on {p.GetValue<string>()}" : "") + ", with no file open.";
        }
        var sb = new StringBuilder();
        sb.Append("project: ").Append(r["project"]?.GetValue<string>() ?? "(none)").Append('\n');
        sb.Append("file: ").Append(r["file"]!.GetValue<string>()).Append('\n');
        sb.Append(CultureInfo.InvariantCulture, $"cursor: line {r["line"]}, column {r["column"]}\n");
        sb.Append("unsaved changes: ").Append(r["dirty"]?.GetValue<bool>() == true ? "yes" : "no").Append('\n');
        if (r["lineText"] is JsonNode lt)
        {
            sb.Append("line text: ").Append(lt.GetValue<string>()).Append('\n');
        }
        if (r["selection"] is JsonNode sel && sel.GetValue<string>().Length > 0)
        {
            sb.Append("selection:\n").Append(sel.GetValue<string>()).Append('\n');
        }
        if (r["goals"] is JsonNode goals && goals.GetValue<string>().Length > 0)
        {
            sb.Append("goals at the cursor:\n").Append(goals.GetValue<string>()).Append('\n');
        }
        if (r["messages"] is JsonArray msgs && msgs.Count > 0)
        {
            sb.Append("messages on this line:\n");
            foreach (JsonNode? m in msgs)
            {
                sb.Append("  ").Append(m!.GetValue<string>()).Append('\n');
            }
        }
        return sb.ToString().TrimEnd();
    }
}
