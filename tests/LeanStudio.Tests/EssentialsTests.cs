using LeanStudio.Core.Workflow;

namespace LeanStudio.Tests;

public sealed class EssentialsTests
{
    [Fact]
    public void InstallsElanTheOfficialWay()
    {
        var (url, file, args) = ElanInstaller.Plan("/tmp/elan-init.sh", windows: false);
        Assert.Equal("https://raw.githubusercontent.com/leanprover/elan/master/elan-init.sh", url);
        Assert.Equal("sh", file);
        Assert.Equal(["/tmp/elan-init.sh", "-y", "--default-toolchain", "stable"], args);
        var (wurl, wfile, wargs) = ElanInstaller.Plan(@"C:\t\elan-init.ps1", windows: true);
        Assert.EndsWith("elan-init.ps1", wurl, StringComparison.Ordinal);
        Assert.Equal("powershell", wfile);
        Assert.Contains("-NoPrompt", wargs);
        Assert.Contains("stable", wargs);
    }

    [Theory]
    [InlineData("unknown identifier 'Real.sqrt'", "Real.sqrt")]
    [InlineData("unknown constant 'Nat.Prime'", "Nat.Prime")]
    [InlineData("unknown identifier '«my name»'", "my name")]
    [InlineData("type mismatch", null)]
    public void FindsTheMissingName(string message, string? name) => Assert.Equal(name, ImportFinder.MissingName(message));

    [Fact]
    public void RanksExactNamesFirstAndCoreBeforeMathlib()
    {
        var hits = new[]
        {
            new LoogleHit("Finset.sum_comm", "", "Mathlib.Algebra.BigOperators.Basic", null),
            new LoogleHit("Nat.add_comm", "", "Init.Data.Nat.Basic", null),
            new LoogleHit("add_comm", "", "Mathlib.Algebra.Group.Defs", null),
            new LoogleHit("add_comm_left", "", "Mathlib.Algebra.Group.Basic", null),
        };
        var ranked = ImportFinder.Rank(hits, "add_comm");
        Assert.Equal(["add_comm", "Nat.add_comm"], ranked.Select(r => r.Name));
        Assert.True(ImportFinder.Available("Init.Data.Nat.Basic", dependsOnMathlib: false));
        Assert.False(ImportFinder.Available("Mathlib.Algebra.Group.Defs", dependsOnMathlib: false));
        Assert.True(ImportFinder.Available("Mathlib.Algebra.Group.Defs", dependsOnMathlib: true));
    }

    [Fact]
    public void AddsAnImportAfterTheOthersOnlyOnce()
    {
        string text = "import Mathlib.Data.Nat.Basic\n\ntheorem t : True := trivial\n";
        string once = ImportFinder.AddImport(text, "Mathlib.Data.Real.Sqrt");
        Assert.Equal("import Mathlib.Data.Nat.Basic\nimport Mathlib.Data.Real.Sqrt\n\ntheorem t : True := trivial\n", once);
        Assert.Equal(once, ImportFinder.AddImport(once, "Mathlib.Data.Real.Sqrt"));
        Assert.Equal("import Std\ndef x := 1", ImportFinder.AddImport("def x := 1", "Std"));
        Assert.StartsWith("import Std\n/-! doc -/", ImportFinder.AddImport("/-! doc -/\ndef x := 1", "Std"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MovesAFileToTheTrash()
    {
        Assert.SkipWhen(OperatingSystem.IsLinux() && Core.Toolchains.Elan.FindExecutable("gio") is null, "no gio on this Linux");
        string dir = Directory.CreateTempSubdirectory("leanstudio-trash").FullName;
        string file = Path.Combine(dir, $"leanstudio-trash-test-{Guid.NewGuid():N}.lean");
        File.WriteAllText(file, "-- to the trash\n");
        string? where = await Core.Workflow.FileOps.MoveToTrashWhereAsync(file);
        Assert.NotNull(where);
        Assert.False(File.Exists(file));
        if (OperatingSystem.IsMacOS())
        {
            // In the Trash, where Put Back can find it; then gone for good, as it is only a test's file.
            Assert.EndsWith(Path.GetFileName(file), where, StringComparison.Ordinal);
            Assert.True(File.Exists(where), where);
            File.Delete(where);
        }
        Directory.Delete(dir, true);
    }
}
