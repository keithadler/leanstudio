using System.Text.RegularExpressions;

namespace LeanStudio.Core.Workflow;

/// <summary>
/// Places where a definition's docstring says more than the definition and the project back up: the prose that makes a
/// wrong definition pass a cursory read. Lean checks that a proof proves the statement as written, never that a
/// definition is the one its comment names, so this reads the docstrings. Found from the text, so nothing has to be built
/// first. It cannot decide whether a definition is the right one; each hit is a claim to anchor with a theorem, not a bug.
/// Reported as <c>doc-claim-unproved</c>, <c>doc-claim-unanchored</c> (strict only) and <c>doc-overclaims-generality</c>.
/// </summary>
public static class DocCheck
{
    private static readonly Regex Declaration = new(
        @"^(?:@\[[^\]]*\]\s*)*(?:(?:protected|private|public|noncomputable|partial|unsafe|nonrec)\s+)*(?<kw>def|abbrev|structure|class|inductive|opaque)\s+(?<name>[^\s:({\[]+)",
        RegexOptions.Compiled);

    private static readonly Regex StatementStart = new(
        @"^(?:@\[[^\]]*\]\s*)*(?:(?:protected|private|public|nonrec)\s+)*(?:theorem|lemma|example)\b",
        RegexOptions.Compiled);

    private static readonly Regex StatementName = new(
        @"^(?:@\[[^\]]*\]\s*)*(?:(?:protected|private|public|nonrec)\s+)*(?:theorem|lemma)\s+(?<name>[^\s:({\[]+)",
        RegexOptions.Compiled);

    private static readonly Regex Token = new(@"[A-Za-z_][A-Za-z0-9_.'!?]*", RegexOptions.Compiled);
    private static readonly Regex Backticked = new(@"`([A-Za-z_][A-Za-z0-9_.']*)`", RegexOptions.Compiled);

    // A sentence that says one thing is another. "standard", "usual" and "canonical" are not here: in Mathlib they are mostly
    // ordinary adjectives, not claims of equivalence.
    private static readonly Regex EquivalenceCue = new(
        @"\b(equivalent to|equivalent|iff|if and only if|same as|coincides with|agrees with|equals|reduces to|identical to)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // A sentence that calls the definition the standard one without any reference (strict mode only).
    private static readonly Regex StandardCue = new(@"\b(the standard|the usual|standard definition|usual definition|canonical)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The docstring claims to hold in general or to be the standard notion.
    private static readonly Regex GeneralityCue = new(
        @"\b(for any|for every|for all|over any|over every|arbitrary|in general|the standard|the usual|standard definition|usual definition|any (?:commutative |unital |nonzero )?(?:ring|field|group|monoid|module|type|semiring)|every (?:commutative |unital |nonzero )?(?:ring|field|group|monoid|module|type|semiring))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Hypotheses that narrow a definition a lot, and the words a docstring would use to admit them.
    private static readonly (string Class, string[] Words)[] Restrictions =
    [
        ("IsDomain", ["domain"]),
        ("Field", ["field"]),
        ("DivisionRing", ["division ring", "skew field"]),
        ("CharZero", ["characteristic zero", "characteristic 0", "char 0", "charzero"]),
        ("CharP", ["characteristic", "char "]),
        ("IsAlgClosed", ["algebraically closed"]),
        ("Finite", ["finite"]),
        ("Fintype", ["finite", "fintype"]),
        ("NoZeroDivisors", ["zero divisor", "domain"]),
        ("IsPrincipalIdealRing", ["principal ideal", "pid"]),
        ("UniqueFactorizationMonoid", ["unique factorization", "ufd"]),
        ("IsNoetherianRing", ["noetherian"]),
        ("IsDedekindDomain", ["dedekind"]),
        ("NeZero", ["nonzero", "non-zero", "invertible"]),
    ];

    private static readonly HashSet<string> NotAReference = new(StringComparer.Ordinal)
    {
        "Prop", "Type", "Sort", "Nat", "Int", "Real", "True", "False", "Bool", "Set", "Finset", "List", "Fin", "Option",
    };

    /// <summary>The statements of the theorems, lemmas and examples in some texts, as a token to statement-id index.</summary>
    public sealed class Scope
    {
        private readonly Dictionary<string, List<int>> _byToken = new(StringComparer.Ordinal);
        private readonly HashSet<string> _theorems = new(StringComparer.Ordinal);
        private int _count;

        /// <summary>Whether a theorem or lemma of this name (or last component) was added.</summary>
        public bool IsTheorem(string name) => _theorems.Contains(Last(name));

        /// <summary>Add the statements (up to the first <c>:=</c>) of every theorem, lemma and example in <paramref name="text"/>.</summary>
        public void Add(string text)
        {
            string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0 || char.IsWhiteSpace(lines[i][0]) || !StatementStart.IsMatch(lines[i]))
                {
                    continue;
                }
                var sb = new System.Text.StringBuilder();
                for (int k = i; k < lines.Length && k < i + 30; k++)
                {
                    string line = lines[k];
                    int comment = line.IndexOf("--", StringComparison.Ordinal);
                    if (comment >= 0)
                    {
                        line = line[..comment];
                    }
                    int assign = line.IndexOf(":=", StringComparison.Ordinal);
                    sb.Append(assign >= 0 ? line[..assign] : line).Append(' ');
                    if (assign >= 0)
                    {
                        break;
                    }
                }
                Match named = StatementName.Match(lines[i]);
                if (named.Success)
                {
                    _theorems.Add(Last(named.Groups["name"].Value));
                }
                int id = _count++;
                foreach (string t in TokensOf(sb.ToString()))
                {
                    if (!_byToken.TryGetValue(t, out List<int>? ids))
                    {
                        _byToken[t] = ids = [];
                    }
                    if (ids.Count == 0 || ids[^1] != id)
                    {
                        ids.Add(id);
                    }
                }
            }
        }

        /// <summary>Whether some statement mentions both names (by their last components).</summary>
        public bool Relates(string a, string b)
        {
            if (!_byToken.TryGetValue(Last(a), out List<int>? x) || !_byToken.TryGetValue(Last(b), out List<int>? y))
            {
                return false;
            }
            var set = new HashSet<int>(x);
            return y.Any(set.Contains);
        }
    }

    /// <summary>
    /// The docstring claims in <paramref name="text"/> that nothing in <paramref name="scope"/> backs up. Without a scope only
    /// <paramref name="text"/>'s own theorems count. <paramref name="strict"/> also lists "standard definition" claims that name
    /// no reference.
    /// </summary>
    public static IReadOnlyList<StyleProblem> Find(string text, Scope? scope = null, bool strict = false)
    {
        if (scope is null)
        {
            scope = new Scope();
            scope.Add(text);
        }
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var found = new List<StyleProblem>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("/--", StringComparison.Ordinal))
            {
                continue;
            }
            var doc = new System.Text.StringBuilder();
            int end = i;
            while (end < lines.Length)
            {
                doc.Append(lines[end]).Append('\n');
                if (lines[end].Contains("-/", StringComparison.Ordinal))
                {
                    break;
                }
                end++;
            }
            int d = end + 1;
            while (d < lines.Length && lines[d].StartsWith("@[", StringComparison.Ordinal) && lines[d].TrimEnd().EndsWith(']'))
            {
                d++;
            }
            if (d >= lines.Length)
            {
                continue;
            }
            Match m = Declaration.Match(lines[d]);
            if (!m.Success)
            {
                continue;
            }
            string name = m.Groups["name"].Value;
            string docText = doc.ToString().Replace("/--", "", StringComparison.Ordinal).Replace("-/", "", StringComparison.Ordinal);
            string signature = SignatureOf(lines, d);
            Check(name, docText, signature, d, scope, strict, found);
            i = end;
        }
        return found;
    }

    // A definition that IS an equivalence says "equivalent to X" about the type it relates, not about another definition.
    private static readonly Regex EquivalenceType = new(@"≃|≅|≌|\bEquiv\b|\bIso\b|\bIsoModule\b|\bOrderIso\b|\bRelIso\b", RegexOptions.Compiled);

    private static void Check(string name, string doc, string signature, int line, Scope scope, bool strict, List<StyleProblem> found)
    {
        string flat = Regex.Replace(doc, @"\s+", " ");
        bool isEquivalence = EquivalenceType.IsMatch(signature) || Regex.IsMatch(Last(name), @"[Ee]quiv|[Ii]so");
        foreach (string sentence in Regex.Split(flat, @"(?<=[.!?])\s+"))
        {
            if (isEquivalence && EquivalenceCue.IsMatch(sentence))
            {
                continue;
            }
            Match cue = EquivalenceCue.Match(sentence);
            if (!cue.Success)
            {
                Match standard = StandardCue.Match(sentence);
                if (strict && standard.Success && Backticked.Matches(sentence).Count == 0)
                {
                    found.Add(new StyleProblem(line, "doc-claim-unanchored", $"`{name}`: the docstring calls it \"{standard.Value.ToLowerInvariant()}\" but names no reference definition and no theorem compares it to one."));
                }
                continue;
            }
            var refs = Backticked.Matches(sentence).Select(x => x.Groups[1].Value)
                .Where(r => r.Length >= 3 && Last(r) != Last(name) && !NotAReference.Contains(r) && !scope.IsTheorem(r) && (r.Any(char.IsUpper) || r.Contains('.') || r.Contains('_')))
                .Distinct().ToList();
            if (refs.Count == 0)
            {
                continue;
            }
            foreach (string r in refs)
            {
                if (!scope.Relates(name, r))
                {
                    found.Add(new StyleProblem(line, "doc-claim-unproved", $"`{name}`: the docstring says \"{cue.Value.ToLowerInvariant()}\" about `{r}`, but no theorem in the scope relates them. Prove `{Last(name)} ↔ {Last(r)}` (or `=`) to anchor the claim."));
                }
            }
        }
        Match general = GeneralityCue.Match(flat);
        if (!general.Success)
        {
            return;
        }
        string lower = flat.ToLowerInvariant();
        foreach ((string cls, string[] words) in Restrictions)
        {
            if (Regex.IsMatch(signature, $@"\[(?:[^\[\]]*:\s*)?(?:[\w.]+\.)?{cls}\s") && !words.Any(w => lower.Contains(w, StringComparison.Ordinal)))
            {
                found.Add(new StyleProblem(line, "doc-overclaims-generality", $"`{name}`: the docstring claims generality (\"{general.Value}\") but the definition requires `{cls}`, which it never mentions."));
            }
        }
    }

    private static string SignatureOf(string[] lines, int declaration)
    {
        var sb = new System.Text.StringBuilder();
        for (int k = declaration; k < lines.Length && k < declaration + 25; k++)
        {
            string line = lines[k];
            int comment = line.IndexOf("--", StringComparison.Ordinal);
            if (comment >= 0)
            {
                line = line[..comment];
            }
            int assign = line.IndexOf(":=", StringComparison.Ordinal);
            sb.Append(assign >= 0 ? line[..assign] : line).Append(' ');
            if (assign >= 0 || line.TrimEnd().EndsWith(" where", StringComparison.Ordinal) || (k > declaration && lines[k].Length > 0 && !char.IsWhiteSpace(lines[k][0]) && k + 1 < lines.Length && lines[k + 1].Length > 0 && !char.IsWhiteSpace(lines[k + 1][0])))
            {
                break;
            }
        }
        return sb.ToString();
    }

    private static IEnumerable<string> TokensOf(string text)
    {
        foreach (Match m in Token.Matches(text))
        {
            yield return m.Value;
            string last = Last(m.Value);
            if (last != m.Value)
            {
                yield return last;
            }
        }
    }

    private static string Last(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }
}
