using System.Text;
using System.Text.RegularExpressions;
using LeanStudio.Core.Editing;

namespace LeanStudio.Core.Workflow;

/// <summary>One theorem with its proof read into tokens.</summary>
/// <param name="Path">The file.</param>
/// <param name="Line">0-based line of the declaration.</param>
/// <param name="Name">The theorem's name, as written.</param>
/// <param name="Statement">Its statement, as <see cref="DuplicateStatements"/> reads it.</param>
/// <param name="Tokens">The proof with comments left out and local names numbered in the order they appear.</param>
public sealed record TheoremProof(string Path, int Line, string Name, string Statement, IReadOnlyList<string> Tokens);

/// <summary>Proofs that are the same, or nearly, found together.</summary>
/// <param name="Members">The theorems, in the order found.</param>
/// <param name="Similarity">How alike the least alike of the linked pairs are, from 0 to 1; 1 means the same tokens.</param>
/// <param name="SameStatement">Whether every member states the same thing, so that this is a duplicate, not a proof reused.</param>
/// <param name="IsVariantFamily">Whether the names differ in a single word (<c>sum_push</c>, <c>prod_push</c>): a family written on purpose.</param>
/// <param name="Differences">How each member's statement differs from the first's, or empty when they are the same.</param>
public sealed record ProofCluster(IReadOnlyList<TheoremProof> Members, double Similarity, bool SameStatement, bool IsVariantFamily, string Differences)
{
    /// <summary>The tokens a lemma that took the shared argument out would save: one copy of the proof for each member after the first.</summary>
    public int Savings => (Members.Count - 1) * Members[0].Tokens.Count;
}

/// <summary>
/// Theorems whose proofs are the same up to the names of the variables they introduce, or nearly: the same tactic script
/// copied and edited, or one argument made twice. Each proof is read into tokens (comments dropped, local names numbered
/// in the order they appear, so <c>intro a b; exact h a b</c> is <c>intro x y; exact h x y</c>) and proofs are compared by the
/// four-token runs they share. It reads the text, so nothing has to be built first; it cannot see that two different
/// scripts close the same goal, and it does not unfold anything. lean-linter reads the compiled proof terms instead.
/// </summary>
public static class SimilarProofs
{
    private const int Shingle = 4;
    private const int CommonShingle = 100;
    private static readonly Regex Token = new(@"[\p{L}_][\p{L}\p{N}_'.!?₀-₉]*|\d+|:=|=>|[^\s]", RegexOptions.Compiled);
    private static readonly HashSet<string> Binders = new(StringComparer.Ordinal)
    {
        "intro", "intros", "rintro", "obtain", "rcases", "rename_i", "by_contra", "fun", "λ", "have", "let", "set", "∀", "∃", "generalize",
    };
    private static readonly HashSet<string> Ends = new(StringComparer.Ordinal) { ":=", ":", "=>", "↦", ";", "\n", "at", "using", "from" };

    /// <summary>The theorems and lemmas in <paramref name="text"/> that start at the margin, with their proofs.</summary>
    /// <param name="path">The file's path, kept in the result.</param>
    /// <param name="text">The file's text.</param>
    public static IReadOnlyList<TheoremProof> Proofs(string path, string text)
    {
        string joined = string.Join('\n', text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
        bool[] code = LeanText.CodeMask(joined);
        var sb = new StringBuilder(joined.Length);
        for (int i = 0; i < joined.Length; i++)
        {
            sb.Append(i < code.Length && !code[i] && joined[i] != '\n' ? ' ' : joined[i]);
        }
        string[] lines = sb.ToString().Split('\n');
        var found = new List<TheoremProof>();
        for (int i = 0; i < lines.Length; i++)
        {
            Match m = DuplicateStatements.Declaration.Match(lines[i]);
            if (!m.Success)
            {
                continue;
            }
            // The declaration runs to the next line that starts at the margin.
            int end = i + 1;
            while (end < lines.Length && end < i + 400 && (lines[end].Length == 0 || char.IsWhiteSpace(lines[end][0])))
            {
                end++;
            }
            string whole = string.Join('\n', lines[i..end]);
            int define = whole.IndexOf(":=", StringComparison.Ordinal);
            int nameEnd = m.Groups["name"].Index + m.Groups["name"].Length;
            if (define < nameEnd)
            {
                continue;
            }
            string statement = Regex.Replace(whole[nameEnd..define], @"\s+", " ").Trim();
            found.Add(new TheoremProof(path, i, m.Groups["name"].Value, statement, Tokens(statement, whole[(define + 2)..])));
        }
        return found;
    }

    /// <summary>
    /// <paramref name="proof"/> as tokens, with every name the statement or the proof introduces replaced by its number.
    /// </summary>
    /// <param name="statement">The theorem's statement, whose binders are local names.</param>
    /// <param name="proof">What follows <c>:=</c>.</param>
    public static IReadOnlyList<string> Tokens(string statement, string proof)
    {
        var locals = new List<string>();
        void Add(string name)
        {
            if (!locals.Contains(name, StringComparer.Ordinal) && Regex.IsMatch(name, @"^[\p{L}_][\p{L}\p{N}_'₀-₉]*$"))
            {
                locals.Add(name);
            }
        }
        foreach (Match binder in Regex.Matches(statement, @"[({](?<n>[^:(){}\[\]]+?)\s:\s"))
        {
            foreach (string n in binder.Groups["n"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                Add(n);
            }
        }
        var tokens = new List<string>();
        foreach (string line in proof.Split('\n'))
        {
            tokens.AddRange(Token.Matches(line).Select(t => t.Value));
            tokens.Add("\n");
        }
        for (int i = 0; i < tokens.Count; i++)
        {
            if (!Binders.Contains(tokens[i]))
            {
                continue;
            }
            int from = i + 1;
            if (tokens[i] == "rcases")
            {
                // rcases e with ⟨a, b⟩: only the pattern after `with` introduces names; e is something that exists.
                while (from < tokens.Count && tokens[from] is not ("with" or "\n" or ";"))
                {
                    from++;
                }
                from++;
            }
            for (int k = from; k < tokens.Count && !Ends.Contains(tokens[k]) && !(tokens[k] == "," && tokens[i] is "∀" or "∃"); k++)
            {
                if (tokens[k] is not ("⟨" or "⟩" or "(" or ")" or "|" or "-" or "_" or "@"))
                {
                    Add(tokens[k]);
                }
            }
        }
        Dictionary<string, string> number = locals.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => "_" + x.i, StringComparer.Ordinal);
        return [.. tokens.Where(t => t != "\n").Select(t => number.TryGetValue(t, out string? r) ? r : t)];
    }

    /// <summary>
    /// The groups of theorems whose proofs share at least <paramref name="threshold"/> of their four-token runs (Jaccard
    /// similarity), counting proofs of at least <paramref name="minTokens"/> tokens; the biggest groups first. A proof is
    /// in a group with every proof it is linked to, so a chain of copies, each edited a little, is one group.
    /// </summary>
    /// <param name="proofs">What to compare.</param>
    /// <param name="threshold">From 0 to 1.</param>
    /// <param name="minTokens">Shorter proofs (<c>by simp</c>, <c>rfl</c>) say too little to compare.</param>
    public static IReadOnlyList<ProofCluster> Find(IEnumerable<TheoremProof> proofs, double threshold = 0.85, int minTokens = 20)
    {
        TheoremProof[] all = [.. proofs.Where(p => p.Tokens.Count >= minTokens)];
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var sets = new long[all.Length][];
        var index = new Dictionary<long, List<int>>();
        for (int d = 0; d < all.Length; d++)
        {
            int[] t = [.. all[d].Tokens.Select(x => ids.TryGetValue(x, out int id) ? id : ids[x] = ids.Count)];
            var set = new HashSet<long>();
            for (int i = 0; i + Shingle <= t.Length; i++)
            {
                long h = 17;
                for (int k = 0; k < Shingle; k++)
                {
                    h = unchecked((h * 1000003) + t[i + k]);
                }
                set.Add(h);
            }
            sets[d] = [.. set.Order()];
            foreach (long h in sets[d])
            {
                if (!index.TryGetValue(h, out List<int>? list))
                {
                    index[h] = list = [];
                }
                list.Add(d);
            }
        }
        int[] parent = [.. Enumerable.Range(0, all.Length)];
        int Root(int x) => parent[x] == x ? x : parent[x] = Root(parent[x]);
        var edges = new List<(int A, double S)>();
        for (int a = 0; a < all.Length; a++)
        {
            var candidates = new HashSet<int>();
            foreach (long h in sets[a])
            {
                List<int> list = index[h];
                if (list.Count <= CommonShingle)
                {
                    candidates.UnionWith(list.Where(b => b > a));
                }
            }
            foreach (int b in candidates)
            {
                double s = Jaccard(sets[a], sets[b]);
                if (s >= threshold)
                {
                    edges.Add((a, s));
                    parent[Root(a)] = Root(b);
                }
            }
        }
        var worst = new Dictionary<int, double>();
        foreach ((int a, double s) in edges)
        {
            int r = Root(a);
            worst[r] = Math.Min(worst.GetValueOrDefault(r, 1.0), s);
        }
        return [.. Enumerable.Range(0, all.Length).GroupBy(Root).Where(g => g.Count() > 1)
            .Select(g =>
            {
                TheoremProof[] members = [.. g.Select(i => all[i])];
                bool same = members.Select(m => DuplicateStatements.Normalize(m.Statement)).Distinct(StringComparer.Ordinal).Count() == 1;
                return new ProofCluster(members, worst.GetValueOrDefault(g.Key, 1.0), same, IsVariantFamily(members.Select(m => m.Name)), Differences(members));
            })
            .OrderBy(c => c.IsVariantFamily)
            .ThenByDescending(c => c.Savings)];
    }

    private static string[] Words(string name) =>
        name.Split(['.', '_'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Whether the names have the same number of words (split at <c>.</c> and <c>_</c>) and differ in at most one place:
    /// <c>max_add_left</c> and <c>max_add_right</c>, <c>Array.any_eq</c> and <c>Vector.any_eq</c>. Such a group is a family
    /// of lemmas written together on purpose.
    /// </summary>
    /// <param name="names">The theorems' names.</param>
    public static bool IsVariantFamily(IEnumerable<string> names)
    {
        string[][] words = [.. names.Select(Words)];
        if (words.Length < 2 || words.Any(w => w.Length != words[0].Length))
        {
            return false;
        }
        return Enumerable.Range(0, words[0].Length).Count(i => words.Any(w => w[i] != words[0][i])) <= 1;
    }

    private static readonly Regex Identifier = new(@"[\p{L}_][\p{L}\p{N}_'.!?₀-₉]*", RegexOptions.Compiled);

    /// <summary>
    /// How the other members' statements differ from the first member's, as the names each has and the first lacks
    /// (<c>Ico → Ioo</c>), one line per member; empty when the statements are the same.
    /// </summary>
    /// <param name="members">The group.</param>
    public static string Differences(IReadOnlyList<TheoremProof> members)
    {
        HashSet<string> first = [.. Identifier.Matches(members[0].Statement).Select(m => m.Value)];
        var lines = new List<string>();
        foreach (TheoremProof m in members.Skip(1))
        {
            HashSet<string> mine = [.. Identifier.Matches(m.Statement).Select(x => x.Value)];
            string[] gone = [.. first.Except(mine).Take(4)];
            string[] added = [.. mine.Except(first).Take(4)];
            if (gone.Length + added.Length > 0)
            {
                lines.Add($"{m.Name}: {(gone.Length == 0 ? "—" : string.Join(", ", gone))} → {(added.Length == 0 ? "—" : string.Join(", ", added))}");
            }
        }
        return string.Join('\n', lines);
    }

    private static double Jaccard(long[] x, long[] y)
    {
        int i = 0, j = 0, both = 0;
        while (i < x.Length && j < y.Length)
        {
            if (x[i] == y[j])
            {
                both++;
                i++;
                j++;
            }
            else if (x[i] < y[j])
            {
                i++;
            }
            else
            {
                j++;
            }
        }
        return (double)both / (x.Length + y.Length - both);
    }

    /// <summary>The groups of similar proofs among the Lean files under <paramref name="root"/>.</summary>
    /// <param name="root">The project folder.</param>
    /// <param name="threshold">From 0 to 1.</param>
    /// <param name="ct">Cancels the scan.</param>
    public static IReadOnlyList<ProofCluster> Scan(string root, double threshold = 0.85, CancellationToken ct = default)
    {
        var all = new List<TheoremProof>();
        foreach (string file in ProjectSearch.Files(root, leanOnly: true))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                all.AddRange(Proofs(file, File.ReadAllText(file)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a file that can't be read has no proofs to compare
            }
        }
        return Find(all, threshold);
    }

    /// <summary>The groups as the lines Output and MCP show: each group's kind and similarity, then its members and how their statements differ.</summary>
    /// <param name="groups">What <see cref="Scan"/> found.</param>
    /// <param name="root">Paths are shown relative to this.</param>
    public static string Describe(IReadOnlyList<ProofCluster> groups, string root)
    {
        var sb = new StringBuilder();
        foreach (ProofCluster g in groups)
        {
            sb.Append(g.Similarity >= 1 ? "same proof" : $"{(int)(g.Similarity * 100)}% alike")
              .Append(g.SameStatement ? ", same statement: a duplicate" : g.IsVariantFamily ? ", a family of variants" : "")
              .Append(" (").Append(g.Members.Count).Append(" theorems, ").Append(g.Members[0].Tokens.Count).Append(" tokens each, a shared lemma would save ").Append(g.Savings).Append(")\n");
            foreach (TheoremProof t in g.Members)
            {
                sb.Append("  ").Append(t.Name).Append("  ").Append(System.IO.Path.GetRelativePath(root, t.Path)).Append(':').Append(t.Line + 1).Append('\n');
            }
            foreach (string d in g.Differences.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                sb.Append("  differs  ").Append(d).Append('\n');
            }
        }
        return sb.ToString().TrimEnd();
    }
}
