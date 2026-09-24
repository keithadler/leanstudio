using LeanStudio.Core.Projects;
using LeanStudio.Core.Toolchains;

namespace LeanStudio.Tests;

public sealed class ProjectTests
{
    [Fact]
    public void ALakeProjectIsFoundFromAFileInside()
    {
        LeanProject? p = LeanProject.FindEnclosing(Lean.Sample("Proofs", "Proofs", "Basic.lean"));
        Assert.NotNull(p);
        Assert.True(p.IsLakeProject);
        Assert.Equal("Proofs", p.Name);
        Assert.Equal(Lean.Toolchain, p.Toolchain);
        Assert.Equal("Proofs.Basic", p.ModuleNameOf(Lean.Sample("Proofs", "Proofs", "Basic.lean")));
        Assert.Equal("serve", p.ServerCommand().Arguments[0]);
    }

    [Fact]
    public void AFolderWithOnlyAToolchainUsesLeanDirectly()
    {
        LeanProject? p = LeanProject.FindEnclosing(Lean.Sample("Demo", "Demo.lean"));
        Assert.NotNull(p);
        Assert.False(p.IsLakeProject);
        Assert.Equal(["--server"], p.ServerCommand().Arguments);
    }

    [Fact]
    public void ParsesElanToolchainList()
    {
        var list = Elan.ParseList("leanprover/lean4:v4.34.0\nleanprover/lean4:v4.35.0-rc1 (default)\n");
        Assert.Equal(2, list.Count);
        Assert.True(list[1].IsDefault);
        Assert.Equal("v4.35.0-rc1", list[1].Version);
        Assert.Empty(Elan.ParseList("no installed toolchains\n"));
    }

    [Fact]
    public void ToolchainDirectoriesAreNamedTheWayElanNamesThem() =>
        Assert.EndsWith("leanprover--lean4---v4.34.0", Elan.ToolchainDirectory("leanprover/lean4:v4.34.0"), StringComparison.Ordinal);

    [Theory]
    [InlineData("Proofs", true)]
    [InlineData("my-project", true)]
    [InlineData("1abc", false)]
    [InlineData("", false)]
    [InlineData("a b", false)]
    public void ValidatesProjectNames(string name, bool ok) => Assert.Equal(ok, Lake.IsValidName(name));

    [Theory]
    [InlineData(ProjectTemplate.Standard)]
    [InlineData(ProjectTemplate.Library)]
    [InlineData(ProjectTemplate.Executable)]
    public async Task EachTemplateMakesAProjectThatBuilds(ProjectTemplate template)
    {
        Lean.RequireLean();
        string parent = Directory.CreateTempSubdirectory("leanstudio-new").FullName;
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var (result, project) = await Lake.NewAsync(parent, "fresh_" + template.ToString().ToLowerInvariant(), template, Lean.Toolchain, ct: ct);
            Assert.True(result.Success, result.Output);
            Assert.NotNull(project);
            Assert.Equal(Lean.Toolchain, project.Toolchain);
            Assert.True(project.IsLakeProject);
            var build = await Lake.BuildAsync(project, ct: ct);
            Assert.True(build.Success, build.Output);
        }
        finally
        {
            Lean.DeleteTree(parent);
        }
    }

    [Fact]
    public async Task InstallsAndSetsTheDefaultThroughElan()
    {
        // Without downloading anything or changing the machine: install a toolchain that is already installed, and
        // make the default what it already is. Both go through elan as the Toolchains panel does.
        Lean.RequireLean();
        Assert.SkipWhen(!Elan.IsInstalled, "elan is not installed");
        var ct = TestContext.Current.CancellationToken;
        IReadOnlyList<Toolchain> before = await Elan.ListAsync(ct);
        Assert.Contains(before, t => t.Name == Lean.Toolchain);
        var install = await Elan.InstallAsync(Lean.Toolchain, ct: ct);
        Assert.True(install.Success, install.Output);
        Assert.Equal(before.Select(t => t.Name), (await Elan.ListAsync(ct)).Select(t => t.Name));

        Toolchain? current = before.FirstOrDefault(t => t.IsDefault);
        Assert.SkipWhen(current is null, "elan has no default here");
        var set = await Elan.SetDefaultAsync(current.Name, ct: ct);
        Assert.True(set.Success, set.Output);
        Assert.Equal(current.Name, (await Elan.ListAsync(ct)).Single(t => t.IsDefault).Name);
    }
}
