using LeanStudio.Core.Workflow;

namespace LeanStudio.Tests;

/// <summary>Reading how far a long task has got from what it prints, with lines as zeta5's build printed them.</summary>
public sealed class ProgressTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 23, 22, 14, 0, TimeSpan.Zero);

    [Fact]
    public void ReadsLakesBuild()
    {
        var r = new ProgressReader();
        Assert.True(r.Feed("⚠ [8729/8951] Built Challenge (164s)", Start));
        Assert.True(r.Feed("warning: Challenge.lean:33:8: declaration uses `sorry`", Start));
        TaskProgress p = r.Progress;
        Assert.Equal(("Building", 8729, 8951, "Challenge", TimeSpan.FromSeconds(164)), (p.Stage, p.Done, p.Total, p.Last, p.LastTook!.Value));
        Assert.Equal(1, p.Warnings);
        Assert.Equal(0, p.Errors);
        Assert.Null(p.Remaining); // not enough to go on yet

        // Four more compiled modules over two minutes: about 30 s each, with 218 jobs to go.
        for (int i = 0; i < 4; i++)
        {
            r.Feed($"✔ [{8730 + i}/8951] Built Apery.Table.V0{i} (83s)", Start.AddSeconds(30 * (i + 1)));
        }
        Assert.Equal(5, p.Compiled);
        Assert.InRange(p.Remaining!.Value.TotalMinutes, 100, 115); // 217 left at one per 30 s
        Assert.Equal(["Challenge", "Apery.Table.V00", "Apery.Table.V01", "Apery.Table.V02", "Apery.Table.V03"], p.Slowest.Select(s => s.Name));
        Assert.StartsWith("8,733 / 8,951 · about 1 h", p.Detail, StringComparison.Ordinal);
        Assert.EndsWith("Apery.Table.V03 (83 s)", p.Detail, StringComparison.Ordinal);

        // Replayed jobs (from the cache or an earlier build) count as done but not as work.
        r.Feed("⚠ [8946/8951] Replayed Challenge", Start.AddSeconds(200));
        Assert.Equal((5, 1), (p.Compiled, p.Replayed));
        Assert.Equal(0.9994, p.Fraction!.Value, 4);
        Assert.False(r.Feed("Build completed successfully (8951 jobs).", Start.AddSeconds(201)));
        Assert.True(r.Feed("✔ [5/6] Built Demo (214ms)", Start));
        Assert.Equal(TimeSpan.FromMilliseconds(214), p.LastTook);

        // Every compiled module's time is kept, for the Timing panel; every finished target, for the file tree.
        Assert.Equal(6, p.Timings.Count);
        Assert.Equal(("Demo", TimeSpan.FromMilliseconds(214)), p.Timings[^1]);
        Assert.Equal(["Apery.Table.V00", "Apery.Table.V01", "Apery.Table.V02", "Apery.Table.V03", "Challenge", "Demo"], p.Finished.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void FindsTheModulesABuildIsCompiling()
    {
        const string ps = """
            29719 412345 01:02:03 /Users/me/.elan/toolchains/lean4/bin/lean --worker file:///Users/me/p/P/Basic.lean
            30001 900000    02:10 /Users/me/.elan/toolchains/lean4/bin/lean /Users/me/p/P/Slow.lean -R /Users/me/p -o /Users/me/p/.lake/build/lib/lean/P/Slow.olean -i /Users/me/p/.lake/build/lib/lean/P/Slow.ilean -c /Users/me/p/.lake/build/ir/P/Slow.c --json
            30002 100000    00:05 /Users/me/.elan/toolchains/lean4/bin/lean /Users/me/p/P/Quick.lean -R /Users/me/p -o /Users/me/p/.lake/build/lib/lean/P/Quick.olean
            30003 100000    00:05 /usr/bin/python3 /Users/me/p/P/Quick.lean -o x
            """;
        var compiling = Core.Toolchains.LeanProcesses.ParsePs(ps, Core.Toolchains.LeanProcesses.CompiledFile);
        Assert.Equal(["/Users/me/p/P/Slow.lean", "/Users/me/p/P/Quick.lean"], compiling.Select(c => c.File));
        Assert.Equal(TimeSpan.FromSeconds(130), compiling[0].Running);
        Assert.Null(Core.Toolchains.LeanProcesses.CompiledFile("/bin/lean --worker file:///x.lean"));
    }

    [Fact]
    public void ReadsTheMathlibCacheAndElan()
    {
        var r = new ProgressReader();
        Assert.True(r.Feed("[lean] info: downloading https://releases.lean-lang.org/lean4/v4.34.0-rc1/lean-4.34.0-rc1-darwin_aarch64.tar.zst", Start));
        Assert.Equal("Downloading Lean 4.34.0-rc1", r.Progress.Stage);
        Assert.True(r.Feed("info: installing /Users/x/.elan/toolchains/leanprover--lean4---v4.34.0-rc1", Start));
        Assert.StartsWith("Installing Lean", r.Progress.Stage, StringComparison.Ordinal);

        Assert.True(r.Feed("Downloaded: 334 file(s) [attempted 334/8700 = 3%, 137 KB/s], Decompressed: 227", Start));
        TaskProgress p = r.Progress;
        Assert.Equal(("Fetching Mathlib's cache", 334, 8700, "137 KB/s"), (p.Stage, p.Done, p.Total, p.Rate));
        Assert.Equal("334 / 8,700 · 137 KB/s", p.Detail);
        Assert.False(r.Feed("Decompressed 8700 file(s)", Start));
    }

    [Fact]
    public void SaysDurationsTheWayAPersonWould()
    {
        Assert.Equal("45 s", TaskProgress.Format(TimeSpan.FromSeconds(45)));
        Assert.Equal("12 min", TaskProgress.Format(TimeSpan.FromMinutes(12.2)));
        Assert.Equal("1 h 35 min", TaskProgress.Format(TimeSpan.FromMinutes(95)));
    }
}
