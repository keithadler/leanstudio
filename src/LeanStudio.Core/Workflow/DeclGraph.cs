using System.Text.RegularExpressions;
using LeanStudio.Core.Learn;

namespace LeanStudio.Core.Workflow;

/// <summary>One declaration of a project: where it is, whether it has a <c>sorry</c>, and which other declarations of the project it mentions.</summary>
/// <param name="Name">Its full name (the namespaces it is in, then its own).</param>
/// <param name="Path">The file.</param>
/// <param name="Line">0-based line it starts on.</param>
/// <param name="EndLine">The line after its last line (0-based, exclusive).</param>
/// <param name="HasSorry">Whether a <c>sorry</c> or <c>admit</c> is in it, outside comments and strings.</param>
/// <param name="ProofLines">Its lines that are not blank or comments.</param>
/// <param name="Uses">The full names of the project's declarations it mentions.</param>
public sealed record Decl(string Name, string Path, int Line, int EndLine, bool HasSorry, int ProofLines, IReadOnlyList<string> Uses);

/// <summary>A set of declarations a person could take on, none of which waits on another.</summary>
/// <param name="Declarations">The declarations, each with a <c>sorry</c> that nothing else stands in the way of.</param>
/// <param name="Weight">How many declarations their proofs would unblock, in all.</param>
public sealed record WorkPackage(IReadOnlyList<Decl> Declarations, int Weight);

/// <summary>
/// What a very large formalization needs to know about itself and Lean does not say: which declarations rest on which, so
/// which <c>sorry</c> can be proved now (every theorem it uses is already proved), which one holds up the most others, and how
/// the remaining work divides among people without anyone waiting on anyone. Read from the sources, so it is current without
/// a build and takes seconds on a project of a few hundred thousand lines.
/// <para>
/// A use is a name written in a declaration that is the name of another declaration of the project (qualified, or unqualified
/// from inside its namespace, or the only declaration anywhere with that name). It is a close read of the text, not Lean's:
/// a name that only appears through <c>open</c> or notation can be missed, and a variable named like a lemma can be taken for it.
/// </para>
/// </summary>
public sealed class DeclGraph
{
    private static readonly Regex Head = new(
        @"^(?:@\[[^\]]*\]\s*)*(?:(?:private|protected|public|noncomputable|partial|unsafe|nonrec)\s+)*(?<kw>theorem|lemma|def|abbrev|instance|structure|class|inductive|opaque|axiom)\b(?<rest>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex Sorry = new(@"\b(?:sorry|admit)\b", RegexOptions.Compiled);
    private static readonly Regex Identifier = new(@"[\p{L}_][\p{L}\p{N}_'!?]*(?:\.[\p{L}_][\p{L}\p{N}_'!?]*)*", RegexOptions.Compiled);

    private readonly Dictionary<string, Decl> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _usedBy = new(StringComparer.Ordinal);
    private HashSet<string>? _taint;

    /// <summary>Every declaration found, in the order of the files given and then of each file.</summary>
    public IReadOnlyList<Decl> Declarations { get; }

    private DeclGraph(List<Decl> declarations)
    {
        Declarations = declarations;
        foreach (Decl d in declarations)
        {
            _byName.TryAdd(d.Name, d);
        }
        foreach (Decl d in declarations)
        {
            foreach (string u in d.Uses)
            {
                if (!_usedBy.TryGetValue(u, out List<string>? list))
                {
                    _usedBy[u] = list = [];
                }
                list.Add(d.Name);
            }
        }
    }

    /// <summary>Read the declarations of <paramref name="files"/> (path and text of each) and what each uses.</summary>
    public static DeclGraph Build(IEnumerable<(string Path, string Text)> files)
    {
        var raw = new List<(string Name, string Ns, string Path, int Line, int End, bool Sorry, int ProofLines, string Body)>();
        foreach ((string path, string text) in files)
        {
            string[] lines = CodeText.Lines(text);
            var scopes = new List<(bool IsNamespace, string Name)>();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                Match ns = Regex.Match(line, @"^namespace\s+(\S+)");
                if (ns.Success)
                {
                    scopes.Add((true, ns.Groups[1].Value));
                    continue;
                }
                if (Regex.IsMatch(line, @"^section\b"))
                {
                    scopes.Add((false, ""));
                    continue;
                }
                if (Regex.IsMatch(line, @"^end\b") && scopes.Count > 0)
                {
                    scopes.RemoveAt(scopes.Count - 1);
                    continue;
                }
                Match m = Head.Match(line);
                if (!m.Success || line.Length == 0 || char.IsWhiteSpace(line[0]))
                {
                    continue;
                }
                Match nm = Regex.Match(m.Groups["rest"].Value.Trim(), @"^[^\s:({\[]+");
                string own = nm.Success && nm.Length > 0 ? nm.Value : $"{m.Groups["kw"].Value}@{i + 1}";
                string prefix = string.Join('.', scopes.Where(x => x.IsNamespace).Select(x => x.Name));
                string full = own.StartsWith("_root_.", StringComparison.Ordinal) ? own[7..] : prefix.Length > 0 ? prefix + "." + own : own;
                int end = Deprecation.EndOfDeclaration(lines, i);
                string[] body = lines[i..end];
                raw.Add((full, own.StartsWith("_root_.", StringComparison.Ordinal) ? "" : prefix, path, i, end, body.Any(l => Sorry.IsMatch(l)), body.Count(l => l.Trim().Length > 0), string.Join('\n', body)));
                i = end - 1;
            }
        }
        var shortNames = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var r in raw)
        {
            string last = r.Name[(r.Name.LastIndexOf('.') + 1)..];
            if (!shortNames.TryGetValue(last, out List<string>? l))
            {
                shortNames[last] = l = [];
            }
            l.Add(r.Name);
        }
        var fullNames = new HashSet<string>(raw.Select(r => r.Name), StringComparer.Ordinal);
        var decls = new List<Decl>(raw.Count);
        foreach (var r in raw)
        {
            var uses = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match id in Identifier.Matches(r.Body))
            {
                string token = id.Value;
                if (fullNames.Contains(token))
                {
                    uses.Add(token);
                    continue;
                }
                if (token.Contains('.', StringComparison.Ordinal) || !shortNames.TryGetValue(token, out List<string>? candidates))
                {
                    // a qualified name written from inside a namespace: Foo.bar for NS.Foo.bar
                    foreach (string ns in Ancestors(r.Ns))
                    {
                        if (fullNames.Contains(ns + "." + token))
                        {
                            uses.Add(ns + "." + token);
                            break;
                        }
                    }
                    continue;
                }
                string? pick = candidates.Count == 1 ? candidates[0]
                    : candidates.Where(c => c.Length > token.Length && r.Ns.Length > 0 && (r.Ns + ".").StartsWith(c[..^(token.Length + 1)] + ".", StringComparison.Ordinal))
                        .OrderByDescending(c => c.Length).FirstOrDefault();
                if (pick is not null)
                {
                    uses.Add(pick);
                }
            }
            uses.Remove(r.Name);
            decls.Add(new Decl(r.Name, r.Path, r.Line, r.End, r.Sorry, r.ProofLines, [.. uses.Order(StringComparer.Ordinal)]));
        }
        return new DeclGraph(decls);
    }

    private static IEnumerable<string> Ancestors(string ns)
    {
        while (ns.Length > 0)
        {
            yield return ns;
            int dot = ns.LastIndexOf('.');
            ns = dot < 0 ? "" : ns[..dot];
        }
    }

    /// <summary>The declaration called <paramref name="name"/> (its full name), or null.</summary>
    public Decl? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>
    /// Whether <paramref name="name"/> has a <c>sorry</c> in it or in anything it uses, however far down: it is not fully proved
    /// yet. A name that is not in the project is not tainted.
    /// </summary>
    public bool IsTainted(string name)
    {
        if (_taint is null)
        {
            // Everything that rests, however far, on a declaration with a sorry: found by walking up from the sorries once.
            var taint = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>();
            foreach (Decl d in Declarations.Where(d => d.HasSorry))
            {
                if (taint.Add(d.Name))
                {
                    pending.Enqueue(d.Name);
                }
            }
            while (pending.Count > 0)
            {
                foreach (string up in _usedBy.GetValueOrDefault(pending.Dequeue()) ?? [])
                {
                    if (taint.Add(up))
                    {
                        pending.Enqueue(up);
                    }
                }
            }
            _taint = taint;
        }
        return _taint.Contains(name);
    }

    /// <summary>
    /// How many declarations would be one step closer to proved if <paramref name="name"/> were: all those that use it, however
    /// far up (a theorem two lemmas away counts).
    /// </summary>
    public int Blocking(string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { name };
        var pending = new Stack<string>();
        pending.Push(name);
        while (pending.Count > 0)
        {
            foreach (string up in _usedBy.GetValueOrDefault(pending.Pop()) ?? [])
            {
                if (seen.Add(up))
                {
                    pending.Push(up);
                }
            }
        }
        return seen.Count - 1;
    }

    /// <summary>
    /// The declarations with a <c>sorry</c> that can be worked on now: every project declaration they use is already fully
    /// proved. Those that unblock the most come first.
    /// </summary>
    public IReadOnlyList<(Decl Decl, int Blocking)> NextUp() =>
        [.. Declarations.Where(d => d.HasSorry && d.Uses.All(u => !IsTainted(u)))
            .Select(d => (d, Blocking(d.Name)))
            .OrderByDescending(x => x.Item2).ThenBy(x => x.d.Path, StringComparer.Ordinal).ThenBy(x => x.d.Line)];

    /// <summary>The <paramref name="count"/> declarations with a <c>sorry</c> that hold up the most others (whether or not they can be started yet).</summary>
    public IReadOnlyList<(Decl Decl, int Blocking, bool Ready)> MostBlocking(int count = 10)
    {
        HashSet<string> ready = [.. NextUp().Select(x => x.Decl.Name)];
        return [.. Declarations.Where(d => d.HasSorry).Select(d => (d, Blocking(d.Name), ready.Contains(d.Name)))
            .OrderByDescending(x => x.Item2).ThenBy(x => x.d.Name, StringComparer.Ordinal).Take(count)];
    }

    /// <summary>
    /// The work that can start now, divided among <paramref name="people"/> so that nobody's share waits on anybody else's and
    /// the shares weigh about the same (by how much each unblocks). Declarations of one file stay together, so each person
    /// works in a few files, not all of them.
    /// </summary>
    public IReadOnlyList<WorkPackage> WorkPackages(int people)
    {
        int n = Math.Max(1, people);
        var groups = NextUp().GroupBy(x => x.Decl.Path, StringComparer.Ordinal)
            .Select(g => (Decls: g.Select(x => x.Decl).ToList(), Weight: g.Sum(x => x.Blocking + 1)))
            .OrderByDescending(g => g.Weight).ThenBy(g => g.Decls[0].Path, StringComparer.Ordinal).ToList();
        var packages = Enumerable.Range(0, n).Select(_ => (Decls: new List<Decl>(), Weight: 0)).ToArray();
        foreach (var group in groups)
        {
            int lightest = 0;
            for (int i = 1; i < n; i++)
            {
                if (packages[i].Weight < packages[lightest].Weight)
                {
                    lightest = i;
                }
            }
            packages[lightest].Decls.AddRange(group.Decls);
            packages[lightest] = (packages[lightest].Decls, packages[lightest].Weight + group.Weight);
        }
        return [.. packages.Where(p => p.Decls.Count > 0).Select(p => new WorkPackage(p.Decls, p.Weight))];
    }

    /// <summary>The <paramref name="count"/> declarations with the most lines of proof (at least <paramref name="minLines"/>), longest first.</summary>
    public IReadOnlyList<Decl> Longest(int count = 10, int minLines = 0) =>
        [.. Declarations.Where(d => d.ProofLines >= minLines).OrderByDescending(d => d.ProofLines).ThenBy(d => d.Name, StringComparer.Ordinal).Take(count)];

    /// <summary>
    /// The declarations of one file in groups that do not use each other at all, in file order: each group could be a file of
    /// its own. Groups smaller than <paramref name="minLines"/> lines are not separated from the first group.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<Decl>> IndependentGroups(int minLines = 0)
    {
        var parent = Declarations.Select((_, i) => i).ToArray();
        int Root(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }
            return x;
        }
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < Declarations.Count; i++)
        {
            index.TryAdd(Declarations[i].Name, i);
        }
        for (int i = 0; i < Declarations.Count; i++)
        {
            foreach (string u in Declarations[i].Uses)
            {
                if (index.TryGetValue(u, out int j))
                {
                    parent[Root(i)] = Root(j);
                }
            }
        }
        List<List<Decl>> groups = [.. Declarations.Select((d, i) => (d, r: Root(i))).GroupBy(x => x.r).Select(g => g.Select(x => x.d).ToList())
            .OrderBy(g => g[0].Line)];
        if (minLines > 0 && groups.Count > 1)
        {
            List<List<Decl>> big = [.. groups.Where(g => g.Sum(d => d.ProofLines) >= minLines)];
            if (big.Count == 0)
            {
                return [Declarations.ToList()];
            }
            foreach (List<Decl> small in groups.Where(g => g.Sum(d => d.ProofLines) < minLines))
            {
                big[0].AddRange(small);
            }
            groups = big;
        }
        return groups;
    }
}
