using LeanStudio.Core.Projects;
using LeanStudio.Core.Verification;

namespace LeanStudio.Tests;

/// <summary>Lean Studio's own project templates build, are fully proved, and pass the strictest assurance check.</summary>
[Collection(Lean.Collection)]
public sealed class TemplateTests
{
    [Theory]
    [InlineData(ProjectTemplate.VerifiedCrypto, "my_crypto", "powMod_eq_spec")]
    [InlineData(ProjectTemplate.VerifiedParser, "my_parser", "parse_print")]
    [InlineData(ProjectTemplate.VerifiedFileFormat, "my_format", "decodeAll_encodeAll")]
    public async Task EachTemplateIsFullyProvedFromTheStart(ProjectTemplate template, string name, string theorem)
    {
        Lean.RequireLean();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string parent = Directory.CreateTempSubdirectory("leanstudio-template").FullName;
        try
        {
            var (result, project) = await Lake.NewAsync(parent, name, template, Lean.Toolchain, ct: ct, studioVersion: "1.1.0");
            Assert.True(project is not null, result.Output);
            string lib = File.ReadAllText(Path.Combine(project.Root, "lakefile.toml")).Contains("name = \"My", StringComparison.Ordinal)
                ? Directory.GetFiles(project.Root, "My*.lean").Select(Path.GetFileNameWithoutExtension).Single()!
                : throw new InvalidOperationException("unexpected library name");
            Assert.False(File.Exists(Path.Combine(project.Root, lib, "Basic.lean")));
            Assert.True(File.Exists(Path.Combine(project.Root, ".github", "workflows", "lean_assurance.yml")));
            Assert.Contains($"`{lib}/Spec.lean`", File.ReadAllText(Path.Combine(project.Root, "README.md")), StringComparison.Ordinal);

            var build = await Lake.BuildAsync(project, ct: ct);
            Assert.True(build.Success, build.Output);
            Assert.DoesNotContain("warning", build.Output, StringComparison.Ordinal); // no sorry, no unused anything

            // Everything proved outright, nothing widening the trust surface, every definition stated about.
            var strict = new AssurancePolicy(AssurancePolicy.ParseCategories("all"), new HashSet<string>());
            AssuranceReport r = await Assurance.RunAsync(project, strict, ct: ct);
            Assert.True(r.Passed, Assurance.ToMarkdown(r));
            Assert.Equal(r.Declarations.Count, r.Count(AssuranceLevel.Proved));
            Assert.Contains(r.Declarations, d => d.Name == $"{lib}.{theorem}");
            Assert.True(r.Definitions >= 2);
        }
        finally
        {
            Lean.DeleteTree(parent);
        }
    }
}
