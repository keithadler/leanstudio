using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using LeanStudio.Core.Agents;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Proofs;
using LeanStudio.Core.Toolchains;
using LeanStudio.Core.Verification;
using LeanStudio.Core.Workflow;
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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> ScratchGates = new(StringComparer.Ordinal);

    /// <summary>
    /// The guidance sent to clients in the <c>initialize</c> result: which tool to use when, and what counts as proved.
    /// Keep it in step with the tools in <see cref="Tools"/>.
    /// </summary>
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
        - For code that calls C (@[extern]), ffi_bindings checks each binding against the C files and writes stubs.
        - If the person has Lean Studio open, studio_context tells you which file, line and goal they are looking
          at, and studio_show opens a file at a line in their window so they can review your change.
        """;

    /// <summary>The Lean Studio MCP server, named <c>leanstudio</c>, with every tool in <see cref="Tools"/>.</summary>
    /// <param name="bench">The workbench the tools run Lean through; it owns the language servers and must outlive the server.</param>
    /// <param name="version">The version reported to clients.</param>
    public static McpServer Create(Workbench bench, string version) =>
        new("leanstudio", version, Instructions, Tools(bench));

    // ---- schema helpers ----

    /// <summary>
    /// The JSON Schema of a tool's arguments object. A property's type is a JSON Schema type, or <c>string[]</c> for an
    /// array of strings.
    /// </summary>
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

    /// <summary>A required integer argument; a number written as a string is accepted too, since some clients send them so.</summary>
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

    /// <summary>A Tenet query whose name several modules declare becomes a tool error that says which to pick.</summary>
    private static T Scoped<T>(Func<T> query)
    {
        try
        {
            return query();
        }
        catch (Exception e) when (e is InvalidOperationException or KeyNotFoundException)
        {
            throw new ToolException(e.Message + " (pass module)");
        }
    }

    private static bool? OptBool(JsonObject a, string name) => a[name] is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    private static int? OptInt(JsonObject a, string name) => a.ContainsKey(name) ? Int(a, name) : null;

    /// <summary>The resolved <c>path</c> argument; it must exist unless the call passes <c>content</c> to check instead.</summary>
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

    /// <summary>
    /// Every tool, in the order clients list them. Arguments and results use 1-based lines and columns. Some tools have
    /// side effects: <c>build</c> runs <c>lake build</c> and <c>profile</c> runs Lean's profiler; <c>suggestions</c>
    /// (with <c>apply</c>), <c>prove</c> and <c>extract_lemma</c> can rewrite the file on disk; <c>run_lean</c> writes a
    /// scratch file under the project's <c>.lake/leanstudio</c>; <c>export_walkthrough</c> writes an HTML file;
    /// <c>search_mathlib</c> queries leansearch.net over the network; the <c>studio_</c> tools talk to a running Lean
    /// Studio window.
    /// </summary>
    /// <param name="bench">The workbench the tools run Lean through.</param>
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
                // Snippets share one scratch file: write and check it one snippet at a time, or two writers collide
                // (Windows refuses the second) and a check could read another snippet's text.
                SemaphoreSlim gate = ScratchGates.GetOrAdd(file, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(ct);
                try
                {
                    await File.WriteAllTextAsync(file, code, ct);
                    FileReport r = await s.CheckAsync(file, code, ct);
                    return r.Diagnostics.Count == 0 ? "Lean accepted the snippet with no messages." : FormatDiagnostics(r, "snippet");
                }
                finally
                {
                    gate.Release();
                }
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
                   ("project", "string", "Any path in the project; defaults to the server's project.", false),
                   ("module", "string", "The module to read it in, when several of the project's modules declare the name (a challenge statement and its solution, say).", false)),
            (a, ct) =>
            {
                TenetWorkspace ws = bench.Session(OptStr(a, "project")).Tenet();
                string name = Str(a, "name");
                string? module = OptStr(a, "module");
                if (Scoped(() => ws.Details(name, module)) is null)
                {
                    throw new ToolException($"{name} is not in the compiled library (is the project built, and is the name fully qualified?)");
                }
                IReadOnlyList<string> axioms = Scoped(() => ws.AxiomsOf(name, module));
                return Task.FromResult(axioms.Count == 0 ? $"{name} depends on no axioms." : $"{name} depends on:\n" + string.Join('\n', axioms.Select(x => "  " + x)));
            }),

        new("why_not_proved",
            "Why a declaration is not fully proved: for each sorry or project axiom it rests on, the shortest chain of declarations leading to it, with file and line of each. The last declaration before the sorry is the one to fix. Run build first.",
            Schema(("name", "string", "Fully qualified declaration name.", true),
                   ("project", "string", "Any path in the project; defaults to the server's project.", false),
                   ("module", "string", "The module to read it in, when several of the project's modules declare the name (a challenge statement and its solution, say).", false)),
            (a, ct) =>
            {
                TenetWorkspace ws = bench.Session(OptStr(a, "project")).Tenet();
                string name = Str(a, "name");
                string? module = OptStr(a, "module");
                if (Scoped(() => ws.Details(name, module)) is null)
                {
                    throw new ToolException($"{name} is not in the compiled library (is the project built, and is the name fully qualified?)");
                }
                IReadOnlyList<AssumptionTrail> trails = Scoped(() => ws.WhyNotProved(name, ct, module));
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

        new("project_map",
            "An overview of the project's proof state from the last build: how many declarations are fully proved, rest on sorry, or rest on a project axiom, and the sorries and axioms that the most declarations depend on (fix those first), with file and line. Run build first.",
            Schema(("project", "string", "Any path in the project; defaults to the server's project.", false)),
            (a, ct) =>
            {
                TenetWorkspace ws = bench.Session(OptStr(a, "project")).Tenet();
                if (ws.OwnModules.Count == 0)
                {
                    throw new ToolException("nothing built yet: call build first");
                }
                ProjectMap map = ws.Map(ct);
                var sb = new StringBuilder(map.Nodes.Count.ToString(CultureInfo.InvariantCulture) + " declarations: ");
                sb.Append(CultureInfo.InvariantCulture, $"{map.Nodes.Count(n => n.Status == MapStatus.Proved)} fully proved, ")
                  .Append(CultureInfo.InvariantCulture, $"{map.Nodes.Count(n => n.Status == MapStatus.RestsOnSorry)} rest on sorry, ")
                  .Append(CultureInfo.InvariantCulture, $"{map.Nodes.Count(n => n.Status is MapStatus.RestsOnAxiom or MapStatus.Axiom)} are or rest on a project axiom.\n");
                var blockers = map.Blockers.Where(n => n.Status != MapStatus.Proved).ToList();
                if (blockers.Count == 0)
                {
                    sb.Append("Everything is fully proved.");
                }
                else
                {
                    sb.Append("\nFix these first (most depended on first):\n");
                    foreach (MapNode n in blockers.Take(30))
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"  {n.Name}  ({(n.Kind == "axiom" ? "axiom" : "uses sorry")}; {n.UsedBy} declaration(s) rest on it)");
                        if (n.SourceFile is not null && n.Line is int l)
                        {
                            sb.Append(CultureInfo.InvariantCulture, $"  {n.SourceFile}:{l}");
                        }
                        sb.Append('\n');
                    }
                }
                return Task.FromResult(sb.ToString().TrimEnd());
            }),

        new("prove",
            "Try a portfolio of tactics (rfl, decide, simp, omega, norm_num, ring, linarith, aesop, grind, exact? and more) on the goal at each sorry in a file, independently, and report which close it and how long each took. When none does, it searches for a counterexample (values that make the goal false). Pass `line` to try only the sorry on that line, and `apply` true to write the first working tactic in place of each sorry.",
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
                    foreach (TacticTrial t in res.Trials.Where(t => t.Outcome is TrialOutcome.Closes or TrialOutcome.Fails))
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"  {(t.Closes ? "closes" : "fails ")}  {(t.Closes ? t.Replacement : t.Tactic)}  ({t.Time})\n");
                    }
                    sb.Append(res.Best is TacticTrial b ? $"  best: {res.Fill(b)}\n" : "  none of the tactics closes this goal\n");
                    if (res.Counterexample is string cex)
                    {
                        sb.Append("  FALSE as stated: counterexample ").Append(cex).Append(" (these values satisfy the hypotheses and make the goal false; fix the statement)\n");
                    }
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

        new("extract_lemma",
            "Turn the goal at a sorry into a lemma of its own: Lean writes `theorem name <the hypotheses it needs> : <goal> := by sorry` above the declaration, and the sorry becomes a use of it. Use it to split a long proof, or to set a hard step aside. Writes the file unless `apply` is false.",
            Schema(("path", "string", "The .lean file.", true),
                   ("line", "integer", "1-based line of the sorry (the nearest one is used).", true),
                   ("name", "string", "The new lemma's name.", true),
                   ("apply", "boolean", "Write the change into the file (default true).", false)),
            async (a, ct) =>
            {
                string path = LeanFile(bench, a);
                string name = Str(a, "name");
                if (!ExtractLemma.IsValidName(name))
                {
                    throw new ToolException($"{name} is not a valid Lean name");
                }
                ProjectSession s = bench.Session(path);
                FileReport r = await s.CheckAsync(path, null, ct);
                SorrySite site = ProofSearch.At(ProofSearch.Sites(r.Text), Int(a, "line") - 1, 0)
                    ?? throw new ToolException("there is no sorry in this file");
                ExtractedLemma lemma = await ExtractLemma.RunAsync(await s.ServerAsync(ct), path, r.Text, site, name, ct)
                    ?? throw new ToolException($"Lean did not reach the sorry at line {site.Line + 1} (an earlier error stops it)");
                bool apply = a["apply"] is not JsonValue v || !v.TryGetValue(out bool ap) || ap;
                if (apply)
                {
                    await File.WriteAllTextAsync(path, ExtractLemma.Apply(r.Text, site, lemma), ct);
                }
                return (apply ? $"wrote into {path}:\n\n" : "would add:\n\n") + lemma.Text.TrimEnd()
                    + $"\n\nat line {lemma.InsertLine + 1}, and replace the sorry at line {site.Line + 1} with: {lemma.Call}";
            }),

        new("ffi_bindings",
            "Lean's C FFI in the project: every @[extern \"c_name\"] declaration and the C function that implements it (file and line), what does not agree (a missing C function, or a C function taking a different number of arguments than Lean passes), and a C stub with the exact signature Lean expects for each missing one.",
            Schema(("project", "string", "Any path in the project; defaults to the server's project.", false)),
            (a, ct) =>
            {
                LeanProject p = bench.ProjectFor(OptStr(a, "project"));
                var (externs, functions, problems) = Core.Workflow.Ffi.Check(p.Root);
                if (externs.Count == 0)
                {
                    return Task.FromResult("No @[extern] declarations in the project.");
                }
                var sb = new StringBuilder();
                foreach (Core.Workflow.ExternBinding b in externs)
                {
                    Core.Workflow.CFunctionSite? site = functions.Where(f => f.Name == b.CName).OrderBy(f => f.IsDefinition ? 0 : 1).FirstOrDefault();
                    sb.Append(CultureInfo.InvariantCulture, $"{b.LeanName} {b.Signature}  ({b.Path}:{b.Line + 1})\n  → {b.CName}: ")
                      .Append(site is null ? (b.CName.StartsWith("lean_", StringComparison.Ordinal) ? "Lean runtime" : "MISSING") : $"{site.Path}:{site.Line + 1}, {site.Parameters} parameter(s)")
                      .Append('\n');
                }
                if (problems.Count > 0)
                {
                    sb.Append("\nProblems:\n");
                    foreach (Core.Workflow.FfiProblem pr in problems)
                    {
                        sb.Append("  ").Append(pr.IsError ? "error: " : "warning: ").Append(pr.Message).Append('\n');
                    }
                }
                var missing = externs.Where(b => !b.CName.StartsWith("lean_", StringComparison.Ordinal) && !functions.Any(f => f.Name == b.CName)).ToList();
                if (missing.Count > 0)
                {
                    sb.Append("\nC stubs for the missing ones (with #include <lean/lean.h>):\n\n");
                    foreach (Core.Workflow.ExternBinding b in missing)
                    {
                        sb.Append(Core.Workflow.Ffi.Stub(b)).Append('\n');
                    }
                }
                return Task.FromResult(sb.ToString().TrimEnd());
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

        new("unused_imports",
            "Which imports of a Lean file it doesn't need: those nothing in it uses, and those another import already brings in. Lean elaborates the file and the check follows every constant, tactic, macro and notation it uses to the module that provides it, so imports needed only for a tactic or a notation are kept. With apply=true, removes them from the file on disk. The file's imports must be built.",
            Schema(("path", "string", "The .lean file.", true),
                   ("content", "string", "Optional full text to check instead of what is on disk.", false),
                   ("apply", "boolean", "Remove the imports it doesn't need from the file on disk (default false).", false)),
            async (a, ct) =>
            {
                string path = LeanFile(bench, a);
                string text = OptStr(a, "content") ?? await File.ReadAllTextAsync(path, ct);
                ImportReport report;
                try
                {
                    report = await ImportCheck.RunAsync(bench.ProjectFor(path), path, text, ct);
                }
                catch (InvalidOperationException e)
                {
                    throw new ToolException("Lean couldn't check the imports (are they built?): " + e.Message);
                }
                var sb = new StringBuilder();
                if (report.Errors > 0)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"warning: the file has {report.Errors} error(s), so some uses may be missed; fix them first.\n");
                }
                foreach (ImportVerdict i in report.Imports)
                {
                    sb.Append(i.Keep
                        ? $"keep   {i.Module}{(i.Uses.Count > 0 ? " (for " + string.Join(", ", i.Uses) + ")" : "")}\n"
                        : $"remove {i.Module}: {i.Explanation}\n");
                }
                if (report.Removable.Count == 0)
                {
                    sb.Append("every import is needed");
                }
                else if (OptBool(a, "apply") == true && report.Errors == 0)
                {
                    await File.WriteAllTextAsync(path, ImportCheck.Remove(text, report.Removable.Select(r => r.Module)), ct);
                    sb.Append(CultureInfo.InvariantCulture, $"removed {report.Removable.Count} import(s) from {path}");
                }
                return sb.ToString().TrimEnd();
            }),

        new("lint",
            "Run the linters CI runs on a Lean file of a Lake project: Mathlib's standard set in a project that uses Mathlib (its style linters among them), every linter Lean has elsewhere, and Batteries' environment linters (missing docstrings, simp normal form, unused arguments…) where Batteries is available. Lints the file as saved on disk.",
            Schema(("path", "string", "The .lean file.", true)),
            async (a, ct) =>
            {
                string path = LeanFile(bench, a);
                LeanProject project = bench.ProjectFor(path);
                var (findings, error) = await Lint.RunAsync(project, path, ct);
                if (error is not null)
                {
                    throw new ToolException(error);
                }
                if (findings.Count == 0)
                {
                    return $"no findings ({Lint.LintersFor(project)}{(project.DependsOnBatteries ? " and Batteries' environment linters" : "")})";
                }
                var sb = new StringBuilder();
                foreach (LintFinding f in findings)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"{Path.GetFileName(f.Path)}:{f.Line + 1}:{f.Column + 1}: {(f.IsError ? "error" : "warning")}{(f.Linter is null ? "" : " [" + f.Linter + "]")}: {f.Message}\n");
                }
                return sb.ToString().TrimEnd();
            }),

        new("heartbeats",
            "Heartbeats per top-level declaration of a Lean file (what maxHeartbeats limits; the default limit is 200000), heaviest first. Use it to see which proofs are close to the limit. The file's imports must be built.",
            Schema(("path", "string", "The .lean file.", true),
                   ("content", "string", "Optional full text instead of what is on disk.", false)),
            async (a, ct) =>
            {
                string path = LeanFile(bench, a);
                string text = OptStr(a, "content") ?? await File.ReadAllTextAsync(path, ct);
                var (counts, error) = await Heartbeats.RunAsync(bench.ProjectFor(path), path, text, ct);
                if (error is not null)
                {
                    throw new ToolException(error);
                }
                return counts.Count == 0 ? "no top-level declarations were counted"
                    : string.Join('\n', counts.Select(c => string.Create(CultureInfo.InvariantCulture, $"line {c.Line + 1}: {c.Heartbeats} ({c.OfLimit:P0} of the default limit)  {c.Declaration.Trim()}")));
            }),

        new("instances",
            "Every instance of a type class visible from a Lean file's imports, with its type, asked of Lean itself. The file's imports must be built.",
            Schema(("path", "string", "A .lean file whose imports decide what is visible.", true),
                   ("class", "string", "The class, e.g. Group or Inhabited.", true)),
            async (a, ct) =>
            {
                string path = LeanFile(bench, a);
                try
                {
                    var (cls, found) = await Instances.OfAsync(bench.ProjectFor(path), path, await File.ReadAllTextAsync(path, ct), Str(a, "class"), ct);
                    return found.Count == 0 ? $"{cls} has no instances visible here"
                        : $"{found.Count} instances of {cls}:\n" + string.Join('\n', found.Select(i => $"  {i.Name} : {i.Type}"));
                }
                catch (InvalidOperationException e)
                {
                    throw new ToolException(e.Message);
                }
            }),

        new("blueprint",
            "Check the project's leanblueprint blueprint (blueprint/src/*.tex) against the last build, with Tenet: for each node, whether the declarations its \\lean{…} names exist and are proved, and where a \\leanok says more than Lean does. Run build first.",
            Schema(("project", "string", "Any path in the project; defaults to the server's project.", false)),
            (a, ct) =>
            {
                ProjectSession s = bench.Session(OptStr(a, "project"));
                if (Blueprint.SourceFolder(s.Project.Root) is not string folder)
                {
                    throw new ToolException("this project has no blueprint folder (blueprint/src)");
                }
                TenetWorkspace ws = s.Tenet();
                IReadOnlyList<BlueprintCheck> checks = Blueprint.Check(Blueprint.Read(folder), ws.BlueprintStatus);
                var sb = new StringBuilder($"{checks.Count(c => c.Verdict == "done")} of {checks.Count} done; {checks.Count(c => c.Disagrees)} disagree with Lean.\n");
                foreach (BlueprintCheck c in checks.OrderBy(c => c.Disagrees ? 0 : 1))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"{(c.Disagrees ? "DISAGREES " : "")}{c.Node.Label} ({Path.GetFileName(c.Node.File)}:{c.Node.Line + 1}): {c.Verdict}\n");
                }
                return Task.FromResult(sb.ToString().TrimEnd());
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
                    throw new ToolException(LeanSearch.Explain(e));
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
                DeclarationDetails d = Scoped(() => ws.Details(Str(a, "name"), OptStr(a, "module"))) ?? throw new ToolException($"{Str(a, "name")} is not in the compiled library");
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

    /// <summary>
    /// One diagnostic as <c>File.lean:line:column: severity: message</c>, with the file name only, a 1-based position, and
    /// continuation lines of the message indented.
    /// </summary>
    public static string FormatDiagnostic(string path, Diagnostic d) =>
        $"{Path.GetFileName(path)}:{d.Range.Start.Line + 1}:{d.Range.Start.Character + 1}: {Severity(d.Severity)}: {d.Message.Trim().Replace("\n", "\n    ", StringComparison.Ordinal)}";

    private static string FormatDiagnostics(FileReport r, string? label = null) =>
        string.Join('\n', r.Diagnostics.OrderBy(d => d.Range.Start).Select(d => FormatDiagnostic(label ?? r.Path, d)));

    /// <summary>
    /// A file's check result for a model to read: a verdict line (error and warning counts, and whether something still
    /// uses <c>sorry</c>), then every diagnostic in order of position.
    /// </summary>
    public static string FormatReport(FileReport r)
    {
        string verdict = r.Errors > 0 ? $"{r.Errors} error{(r.Errors == 1 ? "" : "s")}"
            : r.UsesSorry ? "no errors, but something uses sorry, so it is not proved"
            : "no errors";
        string head = $"{r.Path}: {verdict}" + (r.Warnings > 0 ? $", {r.Warnings} warning{(r.Warnings == 1 ? "" : "s")}" : "");
        return r.Diagnostics.Count == 0 ? head + ". Lean accepts the file." : head + "\n\n" + FormatDiagnostics(r);
    }

    /// <summary>
    /// Goals as text, with each hypothesis marked <c>+</c> when the tactic adds it and <c>-</c> when it removes it, and
    /// named cases marked new or closed; <c>no goals</c> when there are none.
    /// </summary>
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

    /// <summary>
    /// Tenet's verdicts as text: the totals, then each rejected declaration with its message, each that rests on
    /// <c>sorry</c> or a project axiom, and the names of up to 100 verified ones.
    /// </summary>
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

    /// <summary>The editor context Lean Studio reported (the <c>context</c> bridge request) as text.</summary>
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
