using LeanStudio.Core.Processes;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Toolchains;

namespace LeanStudio.Core.Workflow;

/// <summary>
/// Running the <c>lean</c> command line on the editor's text: a copy of the file (saved or not) in a mirror of the
/// project under <c>.lake</c>, run with the project's dependencies on the path. The imports must be built, which
/// they are for any file Lean has open in the editor.
/// </summary>
public static class LeanCli
{
    /// <summary>The mirror folder for one purpose, so concurrent jobs do not overwrite each other's copies.</summary>
    public static string MirrorRoot(LeanProject project, string purpose) => Path.Combine(project.Root, ".lake", "leanstudio-" + purpose);

    /// <summary>Write <paramref name="text"/> to the file's place in the mirror, and return that path.</summary>
    public static async Task<string> MirrorAsync(LeanProject project, string sourcePath, string text, string purpose, CancellationToken ct = default)
    {
        string mirror = MirrorRoot(project, purpose);
        string rel = Path.GetRelativePath(project.Root, Path.GetFullPath(sourcePath));
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
        {
            rel = Path.GetFileName(sourcePath);
        }
        string leanFile = Path.Combine(mirror, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(leanFile)!);
        await File.WriteAllTextAsync(leanFile, text, ct).ConfigureAwait(false);
        return leanFile;
    }

    /// <summary>Run <c>lean</c> with the project's environment (<c>lake env lean</c> in a Lake project).</summary>
    public static Task<ProcessResult> RunAsync(LeanProject project, IReadOnlyList<string> args, CancellationToken ct = default) =>
        project.IsLakeProject
            ? ProcessRunner.RunAsync(Elan.FindExecutable("lake") ?? "lake", ["env", "lean", .. args], project.Root, ct: ct)
            : ProcessRunner.RunAsync(Elan.FindExecutable("lean") ?? "lean", args, project.Root, ct: ct);
}
