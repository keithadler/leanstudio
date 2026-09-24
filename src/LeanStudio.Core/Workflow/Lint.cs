using System.Text;
using System.Text.RegularExpressions;
using LeanStudio.Core.Processes;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Toolchains;

namespace LeanStudio.Core.Workflow;

/// <summary>One thing a linter found.</summary>
/// <param name="Path">The file.</param>
/// <param name="Line">0-based line.</param>
/// <param name="Column">0-based column.</param>
/// <param name="IsError">Whether it is reported as an error rather than a warning.</param>
/// <param name="Message">What it says, without the note on how to turn it off.</param>
/// <param name="Linter">The linter's option, such as <c>linter.missingDocs</c>, when the message names it.</param>
public sealed record LintFinding(string Path, int Line, int Column, bool IsError, string Message, string? Linter);

/// <summary>
/// The linters, run the way CI runs them (Lean ▸ Lint File): <c>lake lint --builtin-only</c> on the file's module,
/// with the linters a library is held to turned on: in a project that uses Mathlib, Mathlib's standard set (its style
/// linters among them), and elsewhere every linter Lean has. In a project that depends on Batteries (Mathlib does),
/// Batteries' environment linters run too (<c>lake exe runLinter</c>: missing docstrings, simp normal form, unused
/// arguments…), as in Mathlib's CI. Lake builds the module with those options, so the file must be saved.
/// </summary>
public static class Lint
{
    /// <summary>The linters to turn on for <paramref name="project"/>, in <c>lake lint --linters</c> form.</summary>
    public static string LintersFor(LeanProject project) => project.DependsOnMathlib ? "linter.mathlibStandardSet" : "linter.all";

    /// <summary>Lint the module of <paramref name="file"/>. Returns the findings, or Lake's output when it failed.</summary>
    public static async Task<(IReadOnlyList<LintFinding> Findings, string? Error)> RunAsync(LeanProject project, string file, CancellationToken ct = default)
    {
        if (!project.IsLakeProject || project.ModuleNameOf(file) is not string module)
        {
            return ([], "Linting needs a file in a Lake project's library.");
        }
        string lake = Elan.FindExecutable("lake") ?? "lake";
        ProcessResult r = await ProcessRunner.RunAsync(lake,
            ["lint", "--builtin-only", "--linters", LintersFor(project), module], project.Root, ct: ct).ConfigureAwait(false);
        IReadOnlyList<LintFinding> findings = Parse(r.Output, project.Root);
        if (project.DependsOnBatteries)
        {
            // Batteries' environment linters (docBlame, simpNF, unusedArguments…), as Mathlib's CI runs them.
            ProcessResult env = await ProcessRunner.RunAsync(lake, ["exe", "runLinter", module], project.Root, ct: ct).ConfigureAwait(false);
            findings = [.. findings, .. Parse(env.Output, project.Root)];
        }
        // Lake names files by their real path; put them back under the root as the editor knows it.
        string real = RealPath(project.Root);
        if (real != project.Root)
        {
            findings = findings.Select(f => f.Path.StartsWith(real + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                ? f with { Path = Path.Combine(project.Root, f.Path[(real.Length + 1)..]) } : f).ToList();
        }
        // Lake exits non-zero when a linter reports something; only a run with nothing to show is a failure.
        return findings.Count == 0 && !r.Success ? ([], r.Output.Trim()) : (findings, null);
    }

    /// <summary>The path with every symbolic link in it resolved (as on macOS, where /var is /private/var).</summary>
    public static string RealPath(string path)
    {
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        string? root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return full;
        }
        string current = root;
        foreach (string part in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is FileSystemInfo target)
                {
                    current = target.FullName;
                }
            }
            catch (IOException)
            {
                return full;
            }
        }
        return current;
    }

    private static readonly Regex Head = new(@"^(?<file>.+?\.lean):(?<line>\d+):(?<col>\d+): (?<sev>warning|error): (?<msg>.*)$", RegexOptions.Compiled);
    private static readonly Regex LinterBlock = new(@"^/- The `(?<name>[\w.]+)` linter reports:", RegexOptions.Compiled);
    private static readonly Regex Note = new(@"^Note: This linter can be disabled with `set_option (?<opt>[\w.]+) false`", RegexOptions.Compiled);

    /// <summary>
    /// Read <c>lake lint</c>'s output: <c>file:line:col: severity: message</c>, the message running on until the
    /// next finding. Text linters count columns from 0, as Lean's messages do; environment linters from 1.
    /// </summary>
    /// <param name="output">What Lake printed.</param>
    /// <param name="root">The project root, for relative file names.</param>
    public static IReadOnlyList<LintFinding> Parse(string output, string root)
    {
        var findings = new List<LintFinding>();
        (string File, int Line, int Col, bool Error, StringBuilder Msg, string? Linter)? current = null;
        bool fromOne = false;
        string? envLinter = null;
        void Flush()
        {
            if (current is var (f, l, c, e, m, lint))
            {
                findings.Add(new LintFinding(f, l, c, e, m.ToString().Trim(), lint));
            }
            current = null;
        }
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            Match h = Head.Match(line);
            if (h.Success)
            {
                Flush();
                string file = h.Groups["file"].Value;
                current = (Path.IsPathRooted(file) ? file : Path.GetFullPath(Path.Combine(root, file)),
                    int.Parse(h.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture) - 1,
                    Math.Max(0, int.Parse(h.Groups["col"].Value, System.Globalization.CultureInfo.InvariantCulture) - (fromOne ? 1 : 0)),
                    h.Groups["sev"].Value == "error", new StringBuilder(h.Groups["msg"].Value), fromOne ? envLinter : null);
                continue;
            }
            Match block = LinterBlock.Match(line);
            if (block.Success)
            {
                Flush();
                envLinter = block.Groups["name"].Value;
                continue;
            }
            if (line.StartsWith("-- ", StringComparison.Ordinal) || line.StartsWith('✔') || line.StartsWith("⚠ ", StringComparison.Ordinal))
            {
                Flush();
                if (line.StartsWith("-- ", StringComparison.Ordinal))
                {
                    fromOne = !line.StartsWith("-- Text linter", StringComparison.Ordinal);
                }
                continue;
            }
            if (current is null)
            {
                continue;
            }
            Match n = Note.Match(line);
            if (n.Success)
            {
                current = current.Value with { Linter = n.Groups["opt"].Value };
                continue;
            }
            current.Value.Msg.Append('\n').Append(line);
        }
        Flush();
        return findings;
    }
}
