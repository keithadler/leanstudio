using System.Diagnostics;
using LeanStudio.Core.Git;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Workflow;

namespace LeanStudio.Tests;

/// <summary>Features for formalizations of a great theorem's size: what to prove next, what blocks what, who is stuck on what, how long it will take.</summary>
public sealed class ScaleTests
{
    private const string A =
        "theorem base : 1 = 1 := rfl\n"
        + "theorem step : 2 = 2 := by\n  sorry\n"
        + "theorem uses_base : 1 = 1 := base\n"
        + "theorem top : 3 = 3 := by\n  have := step\n  exact rfl\n"
        + "theorem top2 : 4 = 4 := by\n  have := top\n  rfl\n"
        + "theorem deep : 5 = 5 := by\n  sorry\n"
        + "theorem deeper : 6 = 6 := by\n  have := deep\n  sorry\n";

    private static DeclGraph Graph(params (string Path, string Text)[] files) => DeclGraph.Build(files);

    // ---- the graph ----

    [Fact]
    public void ReadsDeclarationsAndWhatEachUses()
    {
        DeclGraph g = Graph(("A.lean", A));
        Assert.Equal(["base", "step", "uses_base", "top", "top2", "deep", "deeper"], g.Declarations.Select(d => d.Name));
        Assert.Equal(["base"], g.Find("uses_base")!.Uses);
        Assert.Equal(["step"], g.Find("top")!.Uses);
        Assert.Equal(["top"], g.Find("top2")!.Uses);
        Assert.Equal((1, 3), (g.Find("step")!.Line, g.Find("step")!.EndLine));
        Assert.Equal([false, true, false, false, false, true, true], g.Declarations.Select(d => d.HasSorry));
        Assert.Equal(2, g.Find("step")!.ProofLines);
    }

    [Fact]
    public void TaintReachesEverythingThatRestsOnASorry()
    {
        DeclGraph g = Graph(("A.lean", A));
        Assert.Equal(["step", "top", "top2", "deep", "deeper"], g.Declarations.Where(d => g.IsTainted(d.Name)).Select(d => d.Name));
        Assert.False(g.IsTainted("base"));
        Assert.False(g.IsTainted("no_such_thing"));
    }

    [Fact]
    public void NextUpIsTheSorriesNothingStandsInTheWayOfAndTheMostBlockingIsFirst()
    {
        DeclGraph g = Graph(("A.lean", A));
        IReadOnlyList<(Decl Decl, int Blocking)> next = g.NextUp();
        Assert.Equal([("step", 2), ("deep", 1)], next.Select(x => (x.Decl.Name, x.Blocking))); // `deeper` has to wait for `deep`
        IReadOnlyList<(Decl Decl, int Blocking, bool Ready)> most = g.MostBlocking(10);
        Assert.Equal([("step", 2, true), ("deep", 1, true), ("deeper", 0, false)], most.Select(x => (x.Decl.Name, x.Blocking, x.Ready)));
        Assert.Single(g.MostBlocking(1));
    }

    [Fact]
    public void NamespacesAreFollowedAndASectionsEndDoesNotCloseOne()
    {
        const string text = "namespace Foo\ntheorem helper : True := trivial\ntheorem user : True := by\n  exact helper\nend Foo\n"
            + "theorem outside : True := Foo.helper\n"
            + "namespace Bar\nsection\ntheorem inner : True := trivial\nend\ntheorem after_section : True := inner\nend Bar\n";
        DeclGraph g = Graph(("B.lean", text));
        Assert.Equal(["Foo.helper", "Foo.user", "outside", "Bar.inner", "Bar.after_section"], g.Declarations.Select(d => d.Name));
        Assert.Equal(["Foo.helper"], g.Find("Foo.user")!.Uses); // unqualified, from inside the namespace
        Assert.Equal(["Foo.helper"], g.Find("outside")!.Uses); // qualified
        Assert.Equal(["Bar.inner"], g.Find("Bar.after_section")!.Uses);
    }

    [Fact]
    public void CommentsAndStringsAreNotCodeAndAmbiguousNamesAreLeftAlone()
    {
        const string text = "theorem a : True := by\n  -- sorry here is a comment\n  trivial\ntheorem b : True := by\n  have s := \"sorry\"\n  trivial\n"
            + "namespace X\ntheorem foo : True := trivial\nend X\nnamespace Y\ntheorem foo : True := trivial\nend Y\ntheorem c : True := foo\n";
        DeclGraph g = Graph(("C.lean", text));
        Assert.All(g.Declarations.Where(d => d.Name is "a" or "b"), d => Assert.False(d.HasSorry));
        Assert.Empty(g.Find("c")!.Uses); // `foo` could be X.foo or Y.foo: neither is guessed
    }

    [Fact]
    public void ACycleDoesNotHangAndATallChainDoesNotOverflow()
    {
        DeclGraph cyc = Graph(("D.lean", "theorem a : True := by\n  have := b\n  trivial\ntheorem b : True := by\n  have := a\n  sorry\n"));
        Assert.True(cyc.IsTainted("a") && cyc.IsTainted("b"));
        Assert.Equal(1, cyc.Blocking("b"));

        var sb = new System.Text.StringBuilder("theorem d0 : True := by\n  sorry\n");
        for (int i = 1; i < 20000; i++)
        {
            sb.Append("theorem d").Append(i).Append(" : True := d").Append(i - 1).Append('\n');
        }
        var clock = Stopwatch.StartNew();
        DeclGraph tall = Graph(("E.lean", sb.ToString()));
        Assert.Equal(19999, tall.Blocking("d0"));
        Assert.Equal("d0", Assert.Single(tall.NextUp()).Decl.Name);
        Assert.True(tall.IsTainted("d19999"));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), clock.Elapsed.ToString());
    }

    // ---- work packages ----

    [Fact]
    public void WorkPackagesShareOutTheReadyWorkSoNobodyWaitsOnAnybody()
    {
        string File(string prefix, int n) => string.Concat(Enumerable.Range(0, n).Select(i => $"theorem {prefix}{i} : True := by\n  sorry\n"));
        // F1 has 3 ready sorries, F2 has 2, F3 has 1; `top` waits on f10 and so is not ready.
        DeclGraph g = Graph(("F1.lean", File("f1", 3) + "theorem top : True := by\n  have := f10\n  sorry\n"), ("F2.lean", File("f2", 2)), ("F3.lean", File("f3", 1)));
        IReadOnlyList<WorkPackage> two = g.WorkPackages(2);
        Assert.Equal(2, two.Count);
        List<string> all = [.. two.SelectMany(p => p.Declarations.Select(d => d.Name))];
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(["f10", "f11", "f12", "f20", "f21", "f30"], all.Order());
        Assert.DoesNotContain("top", all);
        Assert.Equal(["F1.lean"], two[0].Declarations.Select(d => Path.GetFileName(d.Path)).Distinct()); // a file stays in one package
        Assert.True(Math.Abs(two[0].Weight - two[1].Weight) <= 3, $"{two[0].Weight} vs {two[1].Weight}");
        Assert.Single(g.WorkPackages(1));
        Assert.Equal(3, g.WorkPackages(10).Count); // no more packages than there are files with work
    }

    // ---- splitting and long proofs ----

    [Fact]
    public void IndependentGroupsAreWhereAFileCouldBeSplit()
    {
        string cluster(string p) => $"theorem {p}0 : True := trivial\n" + string.Concat(Enumerable.Range(1, 3).Select(i => $"theorem {p}{i} : True := by\n  have := {p}{i - 1}\n  trivial\n"));
        DeclGraph g = Graph(("G.lean", cluster("a") + cluster("b") + "theorem lone : True := trivial\n"));
        IReadOnlyList<IReadOnlyList<Decl>> groups = g.IndependentGroups();
        Assert.Equal([["a0", "a1", "a2", "a3"], ["b0", "b1", "b2", "b3"], ["lone"]], groups.Select(x => x.Select(d => d.Name).ToList()));
        IReadOnlyList<IReadOnlyList<Decl>> big = g.IndependentGroups(minLines: 6); // the loner is too small to be a file: it stays with the first
        Assert.Equal([["a0", "a1", "a2", "a3", "lone"], ["b0", "b1", "b2", "b3"]], big.Select(x => x.Select(d => d.Name).ToList()));
        Assert.Single(g.IndependentGroups(minLines: 1000)); // nothing is big enough: do not split
    }

    [Fact]
    public void LongestProofsComeFirst()
    {
        DeclGraph g = Graph(("H.lean", "theorem short : True := trivial\ntheorem long : True := by\n  trivial\n  skip\n  skip\n  skip\ntheorem mid : True := by\n  trivial\n  skip\n"));
        Assert.Equal(["long", "mid"], g.Longest(2).Select(d => d.Name));
        Assert.Equal(["long"], g.Longest(5, minLines: 4).Select(d => d.Name));
        Assert.Equal(5, g.Find("long")!.ProofLines);
    }

    // ---- the build ----

    [Fact]
    public void TheCriticalPathIsTheLongestChainAndSaysHowMuchMachinesCanHelp()
    {
        var imports = new Dictionary<string, IReadOnlyList<string>> { ["A"] = [], ["B"] = ["A"], ["C"] = ["A"], ["D"] = ["B", "C"], ["E"] = [], ["F"] = ["Missing", "E"] };
        double Cost(string m) => m switch { "A" => 1, "B" => 5, "C" => 2, "D" => 1, "E" => 3, _ => 1 };
        BuildPath path = CriticalPath.Find(imports, Cost);
        Assert.Equal(["A", "B", "D"], path.Chain);
        Assert.Equal(7, path.Length);
        Assert.Equal(13, path.TotalCost);
        Assert.Equal(13.0 / 7, path.MaxParallelism, 6);
        string text = CriticalPath.ToText(path, Cost, "lines");
        Assert.Contains("3 modules in a row, 7 lines, out of 13 lines in all", text, StringComparison.Ordinal);
        Assert.Contains("A → B → D", text, StringComparison.Ordinal);
        Assert.Contains("B: 5 lines (71% of the chain)", text, StringComparison.Ordinal);
        Assert.Equal("No modules found.", CriticalPath.ToText(CriticalPath.Find(new Dictionary<string, IReadOnlyList<string>>(), _ => 1), _ => 1, "lines"));
    }

    [Fact]
    public void TheCriticalPathSurvivesACycleAndAVeryLongChain()
    {
        var cycle = new Dictionary<string, IReadOnlyList<string>> { ["A"] = ["B"], ["B"] = ["A"] };
        Assert.Equal(2, CriticalPath.Find(cycle, _ => 1).Length);
        var chain = Enumerable.Range(0, 5000).ToDictionary(i => $"M{i}", i => (IReadOnlyList<string>)(i == 0 ? [] : [$"M{i - 1}"]));
        BuildPath path = CriticalPath.Find(chain, _ => 2);
        Assert.Equal(5000, path.Chain.Count);
        Assert.Equal(10000, path.Length);
        Assert.Equal("M0", path.Chain[0]);
        Assert.Equal(1, path.MaxParallelism);
    }

    // ---- layers ----

    [Fact]
    public void LayerRulesFlagImportsThatReachUp()
    {
        LayerRules rules = LayerRules.Parse("""{ "layers": [["L.Basic", "L.Util"], ["L.Mid"], "L.Top"] }""")!;
        Assert.Equal([0, 0, 1, 2, -1], new[] { "L.Basic.Group", "L.Util", "L.Mid.X", "L.Top", "Other.M" }.Select(rules.LayerOf));
        ImportEdge E(string m, int line, string imported) => new(m, m + ".lean", line, imported);
        IReadOnlyList<LayerViolation> violations = rules.Check([
            E("L.Mid.X", 0, "L.Basic.Group"), // down: fine
            E("L.Basic.Z", 3, "L.Top"), // up: a violation
            E("L.Basic.Z", 1, "L.Mid.X"), // up
            E("L.Top", 0, "L.Top"), // the same layer: fine
            E("Other.M", 0, "L.Top"), // not in a layer: ignored
            E("L.Mid.X", 2, "Mathlib.Data"), // not in a layer: ignored
        ]);
        Assert.Equal([("L.Basic.Z", 1, 0, 1), ("L.Basic.Z", 3, 0, 2)], violations.Select(v => (v.Module, v.Line, v.FromLayer, v.ToLayer)));
    }

    [Fact]
    public void ThePrefixWithTheLongestMatchWinsAndBadRulesAreRefused()
    {
        var rules = new LayerRules([["L"], ["L.Special"]]);
        Assert.Equal([0, 1, 1, 0], new[] { "L.B", "L.Special", "L.Special.A", "L.Specialist" }.Select(rules.LayerOf)); // a prefix is a whole component
        Assert.Null(LayerRules.Parse("not json"));
        Assert.Null(LayerRules.Parse("""{ "layers": 3 }"""));
        Assert.Null(LayerRules.Parse("""{ "layers": [[1]] }"""));
        Assert.Equal(Path.Combine("root", ".leanstudio", "layers.json"), LayerRules.FileFor("root"));
    }

    // ---- locked statements ----

    [Fact]
    public void ALockedStatementSurvivesRenamesAndProofsButNotAChange()
    {
        var statement = new TheoremStatement("F.lean", 0, "main_theorem", "(n : ℕ) (h : 2 < n) : ¬ ∃ a b c : ℕ, a ^ n + b ^ n = c ^ n");
        IReadOnlyList<LockedStatement> locks = StatementLock.Lock([], statement);
        Assert.Equal(StatementLock.HashOf(statement.Statement), Assert.Single(locks).Hash);
        Assert.Equal(16, locks[0].Hash.Length);

        TheoremStatement T(string name, string s) => new("G.lean", 9, name, s);
        IReadOnlyList<LockResult> results = StatementLock.Check(locks, [
            T("main_theorem", "(k : ℕ)  (hk : 2 < k) : ¬ ∃ x y z : ℕ, x ^ k + y ^ k = z ^ k"), // renamed variables and spacing: the same
        ]);
        Assert.Equal(LockStatus.Same, Assert.Single(results).Status);

        Assert.Equal(LockStatus.Changed, StatementLock.Check(locks, [T("main_theorem", "(n : ℕ) (h : 3 < n) : ¬ ∃ a b c : ℕ, a ^ n + b ^ n = c ^ n")])[0].Status); // 2 became 3
        LockResult missing = StatementLock.Check(locks, [T("other", "True")])[0];
        Assert.Equal((LockStatus.Missing, null), (missing.Status, missing.Now));
    }

    [Fact]
    public void LockingAgainReplacesAndTheFileRoundTrips()
    {
        var a = new TheoremStatement("F.lean", 0, "b", "(x : ℕ) : x = x");
        var b = new TheoremStatement("F.lean", 1, "a", "True");
        IReadOnlyList<LockedStatement> locks = StatementLock.Lock(StatementLock.Lock([], a), b);
        Assert.Equal(["a", "b"], locks.Select(l => l.Name)); // by name
        locks = StatementLock.Lock(locks, a with { Statement = "(x : ℕ) : x ≤ x" });
        Assert.Equal(2, locks.Count);
        Assert.Equal("(x : ℕ) : x ≤ x", locks.Single(l => l.Name == "b").Statement);

        string dir = Directory.CreateTempSubdirectory("leanstudio-locks-").FullName;
        try
        {
            Assert.Empty(StatementLock.Read(dir)); // no file yet
            StatementLock.Write(dir, locks);
            Assert.Equal(locks, StatementLock.Read(dir));
            File.WriteAllText(StatementLock.FileFor(dir), "{ broken");
            Assert.Empty(StatementLock.Read(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- forecast ----

    private static SorryPoint P(string date, int sorries) => new("0123456789abcdef0123456789abcdef01234567", date, "x", sorries);

    [Fact]
    public void AForecastFollowsTheLineThroughTheLastCommits()
    {
        Forecast f = Forecast.From([P("2026-01-01", 100), P("2026-01-11", 90), P("2026-01-21", 80), P("2026-01-31", 70)]);
        Assert.Equal(70, f.Remaining);
        Assert.Equal(-1, f.SlopePerDay, 6);
        Assert.Equal(new DateOnly(2026, 4, 11), f.Projected);
        Assert.Equal("high", f.Confidence);
        Assert.Contains("70 sorries remain", f.Message, StringComparison.Ordinal);
        Assert.Contains("2026-04-11", f.Message, StringComparison.Ordinal);
        Assert.Contains("1.00 proved a day", f.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AForecastSaysSoWhenThereIsNoPaceToGoOn()
    {
        Assert.Null(Forecast.From([P("2026-01-01", 50), P("2026-01-11", 50), P("2026-01-21", 50)]).Projected); // flat
        Forecast rising = Forecast.From([P("2026-01-01", 10), P("2026-01-11", 20), P("2026-01-21", 30)]);
        Assert.Null(rising.Projected);
        Assert.Contains("has not been falling", rising.Message, StringComparison.Ordinal);
        Assert.Contains("Too little history", Forecast.From([P("2026-01-01", 5), P("2026-01-02", 4)]).Message, StringComparison.Ordinal);
        Assert.Contains("Too little history", Forecast.From([P("2026-01-01", 5), P("2026-01-01", 4), P("2026-01-01", 3)]).Message, StringComparison.Ordinal); // all one day
        Assert.Equal("There are no sorries left.", Forecast.From([P("2026-01-01", 2), P("2026-01-02", 1), P("2026-01-03", 0)]).Message);
        Assert.Equal("low", Forecast.From([P("2026-01-01", 50), P("2026-01-02", 10), P("2026-01-03", 60), P("2026-01-04", 5), P("2026-01-05", 40)]).Confidence); // a bumpy history
    }

    // ---- how long a sorry has stood ----

    [Fact]
    public void ParsesGitBlameAndTreatsUncommittedLinesAsNew()
    {
        const string h1 = "1111111111111111111111111111111111111111", h0 = "0000000000000000000000000000000000000000";
        string blame = $"{h1} 1 1 2\nauthor Ada\nauthor-mail <a@x>\nauthor-time 1577836800\nauthor-tz +0000\nsummary Start\nfilename F.lean\n\ttheorem a : True := by\n"
            + $"{h1} 2 2\nauthor Ada\nauthor-time 1577836800\nsummary Start\nfilename F.lean\n\t  sorry\n"
            + $"{h0} 3 3 1\nauthor Not Committed Yet\nauthor-time 1700000000\nsummary Version of F.lean from F.lean\nfilename F.lean\n\t  -- new\n";
        IReadOnlyList<BlamedLine> lines = SorryAge.ParseBlame(blame);
        Assert.Equal([1, 2, 3], lines.Select(l => l.Line));
        Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), lines[1].Time);
        Assert.Equal(("Ada", "Start"), (lines[1].Author, lines[1].Summary));
        Assert.Null(lines[2].Time);
    }

    [Fact]
    public async Task FindsHowLongEachSorryHasStoodInARealRepository()
    {
        string dir = Directory.CreateTempSubdirectory("leanstudio-age-").FullName;
        try
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            Assert.NotNull((await GitRepository.InitAsync(dir, ct)).Path);
            var repo = GitRepository.Find(dir)!;
            string file = Path.Combine(dir, "A.lean");
            async Task Commit(string text, string date, string message)
            {
                await File.WriteAllTextAsync(file, text, ct);
                Assert.True((await repo.RunAsync(["add", "-A"], ct: ct)).Success);
                Assert.True((await repo.RunAsync(["-c", "user.name=Ada", "-c", "user.email=a@x", "-c", "commit.gpgsign=false", "commit", "-m", message, "--date", date], ct: ct)).Success);
            }
            await Commit("theorem old : True := by\n  sorry\n", "2020-01-01T12:00:00", "An old sorry");
            await Commit("theorem old : True := by\n  sorry\ntheorem recent : True := by\n  sorry\n", "2026-09-01T12:00:00", "A recent sorry");
            IReadOnlyList<Marker> markers = [.. Markers.ScanText(file, File.ReadAllLines(file))];
            var today = new DateOnly(2026, 10, 3);
            IReadOnlyList<SorryAgeEntry> ages = await SorryAge.ReadAsync(repo, markers, today, ct);
            Assert.Equal(["old", "recent"], ages.Select(a => a.Declaration));
            Assert.Equal(today.DayNumber - new DateOnly(2020, 1, 1).DayNumber, ages[0].DaysOld);
            Assert.Equal(32, ages[1].DaysOld);
            Assert.Equal(("Ada", "An old sorry"), (ages[0].Author, ages[0].Summary));
            Assert.Empty(await SorryAge.ReadAsync(repo, [new Marker(Path.Combine(dir, "Untracked.lean"), 0, 0, MarkerKind.Sorry, "x", "sorry")], today, ct)); // git cannot blame what it does not track
        }
        finally
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(dir, true);
        }
    }

    // ---- the reports, and the tools an assistant gets ----

    private static async Task GitAsync(string dir, params string[] args)
    {
        var r = await Core.Processes.ProcessRunner.RunAsync("git", args, dir, ct: TestContext.Current.CancellationToken);
        Assert.True(r.Success, string.Join(' ', args) + ": " + r.Output);
    }

    [Fact]
    public async Task AssistantsCanPlanTheWorkOfALargeProject()
    {
        string dir = Directory.CreateTempSubdirectory("leanstudio-scale-").FullName;
        try
        {
            CancellationToken ct = TestContext.Current.CancellationToken;
            Directory.CreateDirectory(Path.Combine(dir, "Proj"));
            async Task Write(string rel, string text) => await File.WriteAllTextAsync(Path.Combine(dir, rel), text, ct);
            await Write("Proj/Basic.lean", "theorem base : 1 = 1 := rfl\n");
            await Write("Proj/Mid.lean", "import Proj.Basic\ntheorem mid : 2 = 2 := by\n  have := base\n  sorry\n");
            await Write("Proj/Main.lean", "import Proj.Mid\ntheorem main_thm (n : ℕ) : n = n := by\n  have := mid\n  rfl\n");

            await using var bench = new Core.Agents.Workbench(dir);
            Mcp.McpServer server = Mcp.LeanTools.Create(bench, "test");
            async Task<string> Call(string tool, System.Text.Json.Nodes.JsonObject args)
            {
                var r = await server.HandleAsync(new System.Text.Json.Nodes.JsonObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                    ["params"] = new System.Text.Json.Nodes.JsonObject { ["name"] = tool, ["arguments"] = args },
                }, ct);
                var result = r!["result"]!;
                Assert.False(result["isError"]?.GetValue<bool>() ?? false, result.ToJsonString());
                return result["content"]![0]!["text"]!.GetValue<string>();
            }

            string next = await Call("sorry_next_up", new());
            Assert.Contains("1 of the 1 sorries can be proved now", next, StringComparison.Ordinal);
            Assert.Contains("Proj.mid".Replace("Proj.", ""), next, StringComparison.Ordinal);
            Assert.Contains("(Proj/Mid.lean:2): unblocks 1", next, StringComparison.Ordinal);
            Assert.Contains("held up  mid  (Proj/Mid.lean:2)  ← can start now", await Call("sorry_blocking", new()), StringComparison.Ordinal);
            string packages = await Call("work_packages", new() { ["people"] = 3 });
            Assert.Contains("in 1 share for 3 people", packages, StringComparison.Ordinal);
            Assert.Contains("Proj/Mid.lean: mid", packages, StringComparison.Ordinal);
            Assert.Contains("Proj/Main.lean:", await Call("long_proofs", new() { ["count"] = 5 }), StringComparison.Ordinal);

            string path = await Call("build_critical_path", new());
            Assert.Contains("3 modules in a row, 9 lines, out of 9 lines in all", path, StringComparison.Ordinal);
            Assert.Contains("Proj.Basic → Proj.Mid → Proj.Main", path, StringComparison.Ordinal);

            string split = await Call("split_advice", new() { ["path"] = Path.Combine(dir, "Proj", "Main.lean") });
            Assert.Contains("there is no clean place to split it", split, StringComparison.Ordinal);

            Assert.Contains("No layers are written down", await Call("layer_check", new()), StringComparison.Ordinal);
            Directory.CreateDirectory(Path.Combine(dir, ".leanstudio"));
            await Write(".leanstudio/layers.json", """{ "layers": [["Proj.Basic"], ["Proj.Mid"], ["Proj.Main"]] }""");
            Assert.Contains("no import reaches up, across 3 layers", await Call("layer_check", new()), StringComparison.Ordinal);
            await Write(".leanstudio/layers.json", """{ "layers": [["Proj.Mid"], ["Proj.Basic"], ["Proj.Main"]] }""");
            Assert.Contains("Proj/Mid.lean:1  Proj.Mid (layer 1) imports Proj.Basic (layer 2)", await Call("layer_check", new()), StringComparison.Ordinal);

            Assert.Equal("nothing is locked", await Call("statement_lock", new() { ["action"] = "list" }));
            Assert.Contains("No statements are locked", await Call("statement_lock", new()), StringComparison.Ordinal);
            Assert.Equal("locked main_thm: (n : ℕ) : n = n", await Call("statement_lock", new() { ["action"] = "lock", ["name"] = "main_thm" }));
            Assert.Contains("Locked statements: 1 of 1 unchanged.", await Call("statement_lock", new()), StringComparison.Ordinal);
            await Write("Proj/Main.lean", "import Proj.Mid\ntheorem main_thm (n : ℕ) : n ≤ n := by\n  omega\n"); // weakened: still compiles
            string changed = await Call("statement_lock", new());
            Assert.Contains("CHANGED  main_thm", changed, StringComparison.Ordinal);
            Assert.Contains("locked: (n : ℕ) : n = n", changed, StringComparison.Ordinal);
            Assert.Contains("now:    (n : ℕ) : n ≤ n", changed, StringComparison.Ordinal);
            await Write("Proj/Main.lean", "import Proj.Mid\n"); // gone
            Assert.Contains("GONE     main_thm", await Call("statement_lock", new()), StringComparison.Ordinal);

            // Git: how old the sorry is, and when they run out.
            await GitAsync(dir, "init", "-q", "-b", "main");
            async Task Commit(string date, int sorries)
            {
                await Write("Proj/Mid.lean", "import Proj.Basic\n" + string.Concat(Enumerable.Range(0, sorries).Select(i => $"theorem s{i} : True := by\n  sorry\n")));
                await GitAsync(dir, "add", "-A");
                await GitAsync(dir, "-c", "user.name=Ada", "-c", "user.email=a@x", "-c", "commit.gpgsign=false", "commit", "-q", "-m", $"{sorries} left", "--date", date);
            }
            await Commit("2020-01-01T12:00:00", 4);
            await Commit("2026-01-11T12:00:00", 3);
            await Commit("2026-01-21T12:00:00", 2);
            await Commit("2026-01-31T12:00:00", 1);
            string age = await Call("sorry_age", new());
            Assert.Contains("1 in all, from git blame", age, StringComparison.Ordinal);
            Assert.Contains("(Proj/Mid.lean:", age, StringComparison.Ordinal);
            Assert.Contains("Ada: ", age, StringComparison.Ordinal);
            string forecast = await Call("sorry_forecast", new() { ["commits"] = 3 }); // the 2020 commit is outside the fit
            Assert.Contains("1 sorries remain", forecast, StringComparison.Ordinal);
            Assert.Contains("around 2026-02-10", forecast, StringComparison.Ordinal);
        }
        finally
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(dir, true);
        }
    }
}
