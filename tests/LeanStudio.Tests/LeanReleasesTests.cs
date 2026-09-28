using System.Text.Json;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Toolchains;

namespace LeanStudio.Tests;

public sealed class LeanReleasesTests
{
    [Theory]
    [InlineData("v4.34.1", 4, 34, 1)]
    [InlineData("leanprover/lean4:v4.34.0", 4, 34, 0)]
    [InlineData(" leanprover/lean4:v4.9.12 ", 4, 9, 12)]
    public void ReadsAPlainStableRelease(string name, int major, int minor, int patch) =>
        Assert.Equal(new Version(major, minor, patch), LeanReleases.StableVersion(name));

    [Theory]
    [InlineData("leanprover/lean4:v4.35.0-rc3")]
    [InlineData("leanprover/lean4-nightly:nightly-2026-09-26")]
    [InlineData("leanprover/lean4:stable")]
    [InlineData("leanprover/lean4:nightly")]
    [InlineData("someone/lean4:v4.34.1")]
    [InlineData("v4.34")]
    [InlineData("4.34.1")]
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElseIsNotAStableRelease(string? name) => Assert.Null(LeanReleases.StableVersion(name));

    [Fact]
    public void SuggestsANewerStableForAProjectThatDependsOnNothing()
    {
        NewerLean? n = LeanReleases.Suggest("leanprover/lean4:v4.34.0", "v4.34.1", hasDependencies: false);
        Assert.NotNull(n);
        Assert.Equal("v4.34.0", n.Pinned);
        Assert.Equal("v4.34.1", n.Latest);
        Assert.Equal("leanprover/lean4:v4.34.1", n.Toolchain);
        // Minor and major steps count too, compared as versions rather than as text.
        Assert.NotNull(LeanReleases.Suggest("leanprover/lean4:v4.9.0", "v4.10.0", hasDependencies: false));
    }

    [Theory]
    [InlineData("leanprover/lean4:v4.34.1", "v4.34.1", false)] // already current
    [InlineData("leanprover/lean4:v4.35.0", "v4.34.1", false)] // ahead of stable
    [InlineData("leanprover/lean4:v4.35.0-rc3", "v4.35.0", false)] // chose release candidates
    [InlineData("leanprover/lean4-nightly:nightly-2026-09-26", "v4.34.1", false)] // chose nightlies
    [InlineData("leanprover/lean4:stable", "v4.34.1", false)] // follows stable already
    [InlineData("leanprover/lean4:v4.34.0", "v4.35.0-rc3", false)] // not a stable tag
    [InlineData("leanprover/lean4:v4.34.0", null, false)] // lookup off or failed
    [InlineData(null, "v4.34.1", false)] // nothing pinned
    [InlineData("leanprover/lean4:v4.34.0", "v4.34.1", true)] // depends on something: its toolchain follows that
    public void StaysQuietOtherwise(string? pinned, string? latest, bool hasDependencies) =>
        Assert.Null(LeanReleases.Suggest(pinned, latest, hasDependencies));

    [Fact]
    public void ReadsDependenciesFromTheManifestOrTheLakefile()
    {
        string root = Directory.CreateTempSubdirectory("leanstudio-deps").FullName;
        try
        {
            var p = new LeanProject(root);
            string toml = Path.Combine(root, "lakefile.toml");
            File.WriteAllText(toml, "name = \"Solo\"\n\n[[lean_lib]]\nname = \"Solo\"\n");
            Assert.False(LeanReleases.HasDependencies(p)); // before the first build writes a manifest

            File.WriteAllText(p.ManifestPath, """{"version": "1.1.0", "packages": [], "name": "Solo"}""");
            Assert.False(LeanReleases.HasDependencies(p));

            File.WriteAllText(p.ManifestPath, """{"version": "1.1.0", "packages": [{"name": "batteries"}], "name": "Solo"}""");
            Assert.True(LeanReleases.HasDependencies(p));

            // A require the manifest hasn't caught up with yet still counts.
            File.WriteAllText(p.ManifestPath, """{"version": "1.1.0", "packages": [], "name": "Solo"}""");
            File.AppendAllText(toml, "\n[[require]]\nname = \"mathlib\"\n");
            Assert.True(LeanReleases.HasDependencies(p));

            File.Delete(toml);
            File.Delete(p.ManifestPath);
            string lean = Path.Combine(root, "lakefile.lean");
            File.WriteAllText(lean, "import Lake\nopen Lake DSL\n\npackage solo\n\n@[default_target]\nlean_lib Solo\n");
            Assert.False(LeanReleases.HasDependencies(p));
            File.AppendAllText(lean, "\nrequire mathlib from git \"https://github.com/leanprover-community/mathlib4\"\n");
            Assert.True(LeanReleases.HasDependencies(p));

            // A manifest that can't be read is taken as "yes": staying quiet is the safe mistake.
            File.Delete(lean);
            File.WriteAllText(p.ManifestPath, "{ not json");
            Assert.True(LeanReleases.HasDependencies(p));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("""{"tag_name": "v4.34.1", "draft": false, "prerelease": false}""", "v4.34.1")]
    [InlineData("""{"tag_name": "v4.35.0-rc3", "draft": false, "prerelease": true}""", null)]
    [InlineData("""{"tag_name": "v4.36.0", "draft": true, "prerelease": false}""", null)]
    [InlineData("""{"tag_name": "nightly-2026-09-26", "prerelease": false}""", null)]
    [InlineData("""{"name": "no tag"}""", null)]
    public void TakesOnlyAPublishedStableReleaseAsTheLatest(string json, string? expected)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(expected, LeanReleases.LatestStableTag(doc.RootElement));
    }
}
