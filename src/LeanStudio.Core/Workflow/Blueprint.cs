using System.Text.RegularExpressions;

namespace LeanStudio.Core.Workflow;

/// <summary>One statement of a blueprint (leanblueprint's LaTeX): a definition, lemma, theorem….</summary>
/// <param name="Kind">The environment: <c>theorem</c>, <c>lemma</c>, <c>definition</c>….</param>
/// <param name="Label">Its <c>\label</c>, or empty.</param>
/// <param name="Title">The optional title in brackets, or empty.</param>
/// <param name="LeanNames">The declarations its <c>\lean{…}</c> names.</param>
/// <param name="StatementOk">Whether the statement is marked <c>\leanok</c> (formalized).</param>
/// <param name="ProofOk">Whether its proof is marked <c>\leanok</c> (proved in Lean).</param>
/// <param name="File">The .tex file.</param>
/// <param name="Line">The 0-based line of its <c>\begin</c>.</param>
public sealed record BlueprintNode(string Kind, string Label, string Title, IReadOnlyList<string> LeanNames, bool StatementOk, bool ProofOk, string File, int Line)
{
    /// <summary>What the blueprint says about it: a definition only needs its statement; the rest need a proof.</summary>
    public bool IsDefinition => Kind is "definition" or "defn" or "def";
}

/// <summary>What Lean says about one declaration a blueprint names.</summary>
public enum LeanStatus
{
    /// <summary>No module of the build declares it.</summary>
    Missing,
    /// <summary>It exists and rests on sorry (or an axiom the project adds).</summary>
    Sorry,
    /// <summary>It exists and is fully proved.</summary>
    Proved,
}

/// <summary>A blueprint node checked against Lean.</summary>
/// <param name="Node">The node.</param>
/// <param name="Lean">What Lean says about each declaration it names.</param>
/// <param name="Verdict">In a word: <c>done</c>, <c>ready to prove</c>, <c>not started</c>, or a disagreement.</param>
/// <param name="Disagrees">Whether the blueprint's marks say more than Lean does (a <c>\leanok</c> over a sorry or a missing name).</param>
public sealed record BlueprintCheck(BlueprintNode Node, IReadOnlyDictionary<string, LeanStatus> Lean, string Verdict, bool Disagrees);

/// <summary>
/// A leanblueprint blueprint (<c>blueprint/src/*.tex</c>) read and checked against what Lean built: which
/// statements are formalized and proved, which are ready to prove, and, most usefully, where a <c>\leanok</c>
/// claims more than Lean shows, or a <c>\lean{…}</c> names a declaration that doesn't exist.
/// </summary>
public static partial class Blueprint
{
    private static readonly string[] Environments =
        ["theorem", "lemma", "proposition", "corollary", "definition", "defn", "def", "conjecture", "claim", "remark", "example"];

    /// <summary>The blueprint's source folder in a project (<c>blueprint/src</c>), or null when it has none.</summary>
    public static string? SourceFolder(string projectRoot)
    {
        string src = Path.Combine(projectRoot, "blueprint", "src");
        return Directory.Exists(src) ? src : Directory.Exists(Path.Combine(projectRoot, "blueprint")) ? Path.Combine(projectRoot, "blueprint") : null;
    }

    /// <summary>Every node in the .tex files under <paramref name="folder"/>.</summary>
    public static IReadOnlyList<BlueprintNode> Read(string folder) =>
        Directory.EnumerateFiles(folder, "*.tex", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                 .SelectMany(f => Parse(File.ReadAllText(f), f)).ToList();

    [GeneratedRegex(@"\\begin\{(?<env>\w+)\}(?:\[(?<title>[^\]]*)\])?")]
    private static partial Regex Begin();

    [GeneratedRegex(@"\\label\{(?<l>[^}]*)\}")]
    private static partial Regex Label();

    [GeneratedRegex(@"\\lean\{(?<n>[^}]*)\}")]
    private static partial Regex Lean();

    /// <summary>The nodes of one .tex file's text.</summary>
    public static IReadOnlyList<BlueprintNode> Parse(string tex, string file)
    {
        var nodes = new List<BlueprintNode>();
        string[] lines = tex.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            Match b = Begin().Match(StripComment(lines[i]));
            if (!b.Success || !Environments.Contains(b.Groups["env"].Value))
            {
                continue;
            }
            string env = b.Groups["env"].Value;
            // The statement runs to its \end; a proof environment right after it belongs to it.
            int end = FindEnd(lines, i, env);
            string statement = string.Join('\n', lines[i..Math.Min(end + 1, lines.Length)].Select(StripComment));
            int next = end + 1;
            while (next < lines.Length && StripComment(lines[next]).Trim().Length == 0)
            {
                next++;
            }
            string proof = "";
            if (next < lines.Length && StripComment(lines[next]).Contains(@"\begin{proof}", StringComparison.Ordinal))
            {
                int proofEnd = FindEnd(lines, next, "proof");
                proof = string.Join('\n', lines[next..Math.Min(proofEnd + 1, lines.Length)].Select(StripComment));
            }
            var names = Lean().Matches(statement).SelectMany(m => m.Groups["n"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToList();
            nodes.Add(new BlueprintNode(env, Label().Match(statement) is { Success: true } l ? l.Groups["l"].Value : "",
                b.Groups["title"].Value, names, statement.Contains(@"\leanok", StringComparison.Ordinal), proof.Contains(@"\leanok", StringComparison.Ordinal), file, i));
        }
        return nodes;
    }

    private static int FindEnd(string[] lines, int from, string env)
    {
        for (int j = from; j < lines.Length; j++)
        {
            if (StripComment(lines[j]).Contains($@"\end{{{env}}}", StringComparison.Ordinal))
            {
                return j;
            }
        }
        return lines.Length - 1;
    }

    /// <summary>A line without its LaTeX comment (from an unescaped <c>%</c>).</summary>
    private static string StripComment(string line)
    {
        for (int k = 0; k < line.Length; k++)
        {
            if (line[k] == '%' && (k == 0 || line[k - 1] != '\\'))
            {
                return line[..k];
            }
        }
        return line;
    }

    /// <summary>Check each node against Lean: <paramref name="lean"/> says what Lean has for a declaration name.</summary>
    public static IReadOnlyList<BlueprintCheck> Check(IEnumerable<BlueprintNode> nodes, Func<string, LeanStatus> lean) =>
        nodes.Select(n =>
        {
            var status = n.LeanNames.Distinct().ToDictionary(x => x, lean);
            bool anyMissing = status.Values.Any(s => s == LeanStatus.Missing), anySorry = status.Values.Any(s => s == LeanStatus.Sorry);
            bool formalized = status.Count > 0 && !anyMissing;
            bool proved = formalized && !anySorry;
            (string verdict, bool disagrees) = (n, status.Count) switch
            {
                (_, 0) when n.StatementOk => ("marked \\leanok, but names no declaration (\\lean{…})", true),
                (_, 0) => ("not started", false),
                _ when anyMissing && (n.StatementOk || n.ProofOk) => ($"marked \\leanok, but Lean has no {string.Join(", ", status.Where(s => s.Value == LeanStatus.Missing).Select(s => s.Key))}", true),
                _ when anyMissing => ($"names {string.Join(", ", status.Where(s => s.Value == LeanStatus.Missing).Select(s => s.Key))}, which Lean doesn't have yet", false),
                _ when n.ProofOk && anySorry => ($"proof marked \\leanok, but {string.Join(", ", status.Where(s => s.Value == LeanStatus.Sorry).Select(s => s.Key))} rests on sorry", true),
                _ when n.IsDefinition => (n.StatementOk ? "done" : "done in Lean (add \\leanok)", false),
                _ when proved => (n.ProofOk ? "done" : "proved in Lean (add \\leanok to the proof)", false),
                _ => ("ready to prove", false),
            };
            return new BlueprintCheck(n, status, verdict, disagrees);
        }).ToList();
}
