using System.Text;
using LeanStudio.Core.Processes;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Toolchains;

namespace LeanStudio.Core.Workflow;

/// <summary>Two proofs set side by side.</summary>
/// <param name="A">The first theorem.</param>
/// <param name="B">The second.</param>
/// <param name="Similarity">The share of their four-token runs they have in common, from 0 to 1.</param>
/// <param name="StatementDiff">How their statements differ, or empty.</param>
/// <param name="Diff">The proofs token by token: what is shared, and what only one has.</param>
/// <param name="Identical">Whether the proofs are the same tokens, so they differ only in the names of what they introduce.</param>
public sealed record ProofComparison(TheoremProof A, TheoremProof B, double Similarity, string StatementDiff, string Diff, bool Identical);

/// <summary>What the compiled terms of two theorems say, as Lean reads them.</summary>
/// <param name="SameType">The statements are the same term.</param>
/// <param name="SameProof">The proofs are the same term, up to the names of bound variables.</param>
/// <param name="LemmasA">How many distinct theorems the first proof applies.</param>
/// <param name="LemmasB">How many the second applies.</param>
/// <param name="Shared">How many of them both apply.</param>
public sealed record CompiledComparison(bool SameType, bool SameProof, int LemmasA, int LemmasB, int Shared);

/// <summary>
/// One proof against another: how alike they are, where they differ, and the proofs most like a given one. The
/// text comparison needs nothing built; <see cref="CompiledAsync"/> asks Lean whether the compiled terms are the same.
/// </summary>
public static class CompareProofs
{
    private const int MaxTokens = 3000;

    /// <summary>Compare two theorems' proofs token by token.</summary>
    /// <param name="a">The first.</param>
    /// <param name="b">The second.</param>
    public static ProofComparison Compare(TheoremProof a, TheoremProof b) =>
        new(a, b, SimilarProofs.Similarity(a, b), SimilarProofs.Differences([a, b]), Diff(a.Tokens, b.Tokens), a.Tokens.SequenceEqual(b.Tokens));

    /// <summary>The <paramref name="count"/> proofs most like <paramref name="target"/>, most alike first, that are at least <paramref name="minimum"/> alike.</summary>
    /// <param name="target">The proof to match.</param>
    /// <param name="all">Where to look.</param>
    /// <param name="count">How many to return.</param>
    /// <param name="minimum">The least similarity worth returning.</param>
    public static IReadOnlyList<(TheoremProof Proof, double Similarity)> Nearest(TheoremProof target, IEnumerable<TheoremProof> all, int count = 3, double minimum = 0.3)
    {
        HashSet<long> mine = SimilarProofs.Shingles(target.Tokens);
        if (mine.Count == 0)
        {
            return [];
        }
        return [.. all.Where(p => !(p.Path == target.Path && p.Line == target.Line) && p.Tokens.Count >= 8)
            .Select(p =>
            {
                HashSet<long> theirs = SimilarProofs.Shingles(p.Tokens);
                int both = mine.Count(theirs.Contains);
                int union = mine.Count + theirs.Count - both;
                return (Proof: p, Similarity: union == 0 ? 0.0 : (double)both / union);
            })
            .Where(x => x.Similarity >= minimum)
            .OrderByDescending(x => x.Similarity)
            .Take(count)];
    }

    /// <summary>
    /// The theorems named <paramref name="name"/> (as written, or its last components) among <paramref name="all"/>;
    /// an exact match wins over a suffix match.
    /// </summary>
    /// <param name="all">The project's theorems.</param>
    /// <param name="name">A name, such as <c>Nat.add_comm</c> or <c>add_comm</c>.</param>
    public static IReadOnlyList<TheoremProof> Find(IEnumerable<TheoremProof> all, string name)
    {
        TheoremProof[] list = [.. all];
        TheoremProof[] exact = [.. list.Where(p => p.Name == name)];
        return exact.Length > 0 ? exact : [.. list.Where(p => p.Name.EndsWith("." + name, StringComparison.Ordinal))];
    }

    /// <summary>
    /// The proofs as runs: <c>= n</c> for n shared tokens, <c>- …</c> for tokens only the first has, <c>+ …</c> for tokens only
    /// the second has (longest common subsequence; proofs over 3,000 tokens are cut there).
    /// </summary>
    /// <param name="a">The first proof's tokens.</param>
    /// <param name="b">The second's.</param>
    public static string Diff(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int n = Math.Min(a.Count, MaxTokens), m = Math.Min(b.Count, MaxTokens);
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }
        var sb = new StringBuilder();
        var removed = new List<string>();
        var added = new List<string>();
        int same = 0;
        void Flush()
        {
            if (same > 0)
            {
                sb.Append("  = ").Append(same).Append(same == 1 ? " token\n" : " tokens\n");
                same = 0;
            }
            if (removed.Count > 0)
            {
                sb.Append("  - ").Append(string.Join(' ', removed)).Append('\n');
                removed.Clear();
            }
            if (added.Count > 0)
            {
                sb.Append("  + ").Append(string.Join(' ', added)).Append('\n');
                added.Clear();
            }
        }
        for (int i = 0, j = 0; i < n || j < m;)
        {
            if (i < n && j < m && a[i] == b[j])
            {
                if (removed.Count + added.Count > 0)
                {
                    Flush();
                }
                same++;
                i++;
                j++;
            }
            else
            {
                if (same > 0)
                {
                    Flush();
                }
                if (j >= m || (i < n && lcs[i + 1, j] >= lcs[i, j + 1]))
                {
                    removed.Add(a[i++]);
                }
                else
                {
                    added.Add(b[j++]);
                }
            }
        }
        Flush();
        if (a.Count > MaxTokens || b.Count > MaxTokens)
        {
            sb.Append("  (only the first ").Append(MaxTokens).Append(" tokens were compared)\n");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>One comparison as the text Output and MCP show.</summary>
    /// <param name="c">The comparison.</param>
    /// <param name="root">Paths are shown relative to this.</param>
    /// <param name="compiled">What Lean said about the compiled terms, when it was asked.</param>
    public static string Describe(ProofComparison c, string root, CompiledComparison? compiled = null)
    {
        var sb = new StringBuilder();
        sb.Append(c.A.Name).Append("  ").Append(Path.GetRelativePath(root, c.A.Path)).Append(':').Append(c.A.Line + 1).Append('\n');
        sb.Append(c.B.Name).Append("  ").Append(Path.GetRelativePath(root, c.B.Path)).Append(':').Append(c.B.Line + 1).Append('\n');
        sb.Append(c.Identical ? "the proofs are the same tokens: they differ only in the names of what they introduce\n" : $"{(int)(c.Similarity * 100)}% alike\n");
        if (c.StatementDiff.Length > 0)
        {
            sb.Append("statements differ: ").Append(c.StatementDiff.Replace('\n', ';')).Append('\n');
        }
        if (compiled is not null)
        {
            sb.Append("compiled: statement ").Append(compiled.SameType ? "the same" : "different")
              .Append(", proof ").Append(compiled.SameProof ? "the same term" : "a different term")
              .Append(", ").Append(compiled.Shared).Append(" lemma").Append(compiled.Shared == 1 ? "" : "s").Append(" applied by both (")
              .Append(compiled.LemmasA).Append(" and ").Append(compiled.LemmasB).Append(")\n");
        }
        if (!c.Identical)
        {
            sb.Append(c.Diff).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Ask Lean about the compiled terms of two theorems: are the statements and the proofs the same term, and how many
    /// lemmas do the proofs share. The files' modules must be built. Returns an error instead when Lean could not say.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="a">The first theorem.</param>
    /// <param name="b">The second.</param>
    /// <param name="ct">Cancels the run.</param>
    public static async Task<(CompiledComparison? Result, string? Error)> CompiledAsync(LeanProject project, TheoremProof a, TheoremProof b, CancellationToken ct = default)
    {
        string? modA = project.ModuleNameOf(a.Path), modB = project.ModuleNameOf(b.Path);
        if (!project.IsLakeProject || modA is null || modB is null || project.OleanOf(a.Path) is null || project.OleanOf(b.Path) is null)
        {
            return (null, "Both files must be built to compare the compiled terms.");
        }
        string imports = string.Join('\n', new[] { modA, modB }.Distinct().Select(m => "import " + m));
        string snippet = $$"""
            import Lean
            {{imports}}
            open Lean
            def resolve (env : Environment) (n : String) : Except String ConstantInfo :=
              let found := Id.run do
                let mut acc : Array ConstantInfo := #[]
                for data in env.header.moduleData do
                  for c in data.constNames do
                    let s := toString c
                    if s == n || s.endsWith ("." ++ n) then
                      if let some info := env.find? c then acc := acc.push info
                return acc
              match found.size with
              | 1 => .ok found[0]!
              | 0 => .error s!"no theorem {n}"
              | _ => match found.find? (fun i => toString i.name == n) with
                | some i => .ok i
                | none => .error s!"{n} is ambiguous"
            def lemmas (env : Environment) (v : Option Expr) : Array Name :=
              (v.map (·.getUsedConstants) |>.getD #[]).filter fun c => match env.find? c with
                | some (.thmInfo _) => true
                | _ => false
            #eval show CoreM Unit from do
              let env ← getEnv
              match resolve env "{{a.Name}}", resolve env "{{b.Name}}" with
              | .ok x, .ok y =>
                let vx := x.value? (allowOpaque := true)
                let vy := y.value? (allowOpaque := true)
                let lx := lemmas env vx
                let ly := lemmas env vy
                IO.println s!"COMPARE {x.type == y.type} {match vx, vy with | some p, some q => p == q | _, _ => false} {lx.size} {ly.size} {(lx.filter ly.contains).size}"
              | .error e, _ | _, .error e => IO.println s!"ERROR {e}"
            """;
        string file = Path.Combine(Path.GetTempPath(), "leanstudio-compare-" + Guid.NewGuid().ToString("N") + ".lean");
        try
        {
            await File.WriteAllTextAsync(file, snippet, ct).ConfigureAwait(false);
            string lake = Elan.FindExecutable("lake") ?? "lake";
            ProcessResult r = await ProcessRunner.RunAsync(lake, ["env", "lean", file], project.Root, ct: ct).ConfigureAwait(false);
            foreach (string line in r.Output.Split('\n'))
            {
                string[] w = line.Trim().Split(' ');
                if (w.Length == 6 && w[0] == "COMPARE" && int.TryParse(w[3], out int la) && int.TryParse(w[4], out int lb) && int.TryParse(w[5], out int shared))
                {
                    return (new CompiledComparison(w[1] == "true", w[2] == "true", la, lb, shared), null);
                }
                if (line.StartsWith("ERROR ", StringComparison.Ordinal))
                {
                    return (null, line[6..].Trim());
                }
            }
            return (null, r.Output.Trim());
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }
}
