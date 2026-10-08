using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tenet.Kernel;
using Tenet.Olean;
using TenetName = Tenet.Kernel.Name;

namespace LeanStudio.Core.Verification;

/// <summary>
/// Which of a project's declarations Tenet has already checked, so that verifying again re-checks only what changed.
///
/// Each checking unit (a declaration, an inductive block, a mutual block) gets a key: a SHA-256 of everything the
/// kernel reads from it (its type, its value, every field of an inductive, constructor or recursor) and of the keys
/// of the project's own declarations it uses, so a changed lemma changes the key of everything that rests on it. What
/// the project imports is covered by the scope: Tenet's version, Lean's, and the name, size and time of every imported
/// module's files; when any of those changes, nothing is reused. A unit is skipped only when its key is one that passed
/// before in the same scope, which is the same check, of the same terms, against the same imports. Failures are never
/// remembered, so a rejected declaration is checked again every time.
/// </summary>
public static class CheckCache
{
    private const int Version = 1;

    /// <summary>Where a project's cache is kept.</summary>
    public static string PathFor(string projectRoot) => Path.Combine(projectRoot, ".lake", "leanstudio", "tenet-cache.json");

    /// <summary>The keys that passed, in <paramref name="scope"/>; empty when the file is missing, unreadable or of another scope.</summary>
    public static HashSet<string> Load(string path, string scope)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = doc.RootElement;
            if (root.GetProperty("version").GetInt32() != Version || root.GetProperty("scope").GetString() != scope)
            {
                return [];
            }
            return root.GetProperty("passed").EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Write the keys that passed. A cache that cannot be written is not an error: the next check is simply a full one.</summary>
    public static void Save(string path, string scope, IEnumerable<string> passed)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            using (FileStream f = File.Create(tmp))
            using (var w = new Utf8JsonWriter(f))
            {
                w.WriteStartObject();
                w.WriteNumber("version", Version);
                w.WriteString("scope", scope);
                w.WriteStartArray("passed");
                foreach (string k in passed.Order(StringComparer.Ordinal))
                {
                    w.WriteStringValue(k);
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The scope the keys are valid in: Tenet's version, the Lean version of the build, and every file of every
    /// module that is not the project's own (name, size, modification time).
    /// </summary>
    public static string Scope(string tenetVersion, string leanVersion, IEnumerable<OleanModule> imported)
    {
        var sb = new StringBuilder();
        sb.Append("tenet ").Append(tenetVersion).Append('\n').Append("lean ").Append(leanVersion).Append('\n');
        foreach (string file in imported.SelectMany(m => m.PartPaths).Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(file);
            sb.Append(file).Append('|').Append(info.Exists ? info.Length : -1).Append('|').Append(info.Exists ? info.LastWriteTimeUtc.Ticks : 0).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>
    /// The key of every unit of the project's own modules. <paramref name="ownUnits"/> maps each unit to its
    /// constants; constants of the project used by a unit contribute their unit's key, everything else its name. A
    /// name several of the project's modules declare (an equation lemma Lean made on demand in two modules that do
    /// not import each other, say) contributes the keys of all its declarations, so a change to any of them counts.
    /// </summary>
    public static Dictionary<Replay.Unit, string> UnitKeys(IReadOnlyList<Replay.Unit> ownUnits, CancellationToken ct = default)
    {
        var unitsOf = new Dictionary<TenetName, List<Replay.Unit>>();
        foreach (Replay.Unit u in ownUnits)
        {
            foreach (TenetName n in u.Names)
            {
                if (!unitsOf.TryGetValue(n, out List<Replay.Unit>? list))
                {
                    unitsOf[n] = list = [];
                }
                list.Add(u);
            }
        }
        var hasher = new Hasher();
        var keys = new Dictionary<Replay.Unit, string>(ReferenceEqualityComparer.Instance);
        var inProgress = new HashSet<Replay.Unit>(ReferenceEqualityComparer.Instance);
        string Key(Replay.Unit u)
        {
            if (keys.TryGetValue(u, out string? k))
            {
                return k;
            }
            ct.ThrowIfCancellationRequested();
            if (!inProgress.Add(u))
            {
                // A cycle between units, which Lean does not allow; the checker reports it. Never reuse such a unit.
                return "cycle:" + u.Name;
            }
            var used = new SortedSet<string>(StringComparer.Ordinal);
            var own = new HashSet<TenetName>(u.Names);
            foreach (ConstantInfo c in u.Constants)
            {
                foreach (TenetName d in UsedByKernel(c))
                {
                    if (own.Contains(d))
                    {
                        continue;
                    }
                    if (unitsOf.TryGetValue(d, out List<Replay.Unit>? declared))
                    {
                        foreach (Replay.Unit du in declared)
                        {
                            used.Add("u " + Key(du));
                        }
                    }
                    else
                    {
                        used.Add("n " + d);
                    }
                }
            }
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            void Str(string s)
            {
                byte[] b = Encoding.UTF8.GetBytes(s);
                h.AppendData(BitConverter.GetBytes(b.Length));
                h.AppendData(b);
            }
            Str("unit " + u.Kind);
            foreach (ConstantInfo c in u.Constants.OrderBy(c => c.Name.ToString(), StringComparer.Ordinal))
            {
                h.AppendData(hasher.Constant(c));
            }
            foreach (string d in used)
            {
                Str(d);
            }
            k = Convert.ToHexString(h.GetHashAndReset());
            inProgress.Remove(u);
            keys[u] = k;
            return k;
        }
        foreach (Replay.Unit u in ownUnits)
        {
            Key(u);
        }
        return keys;
    }

    /// <summary>Every constant a declaration's check can look at: those in its type and value, and in a recursor's rules.</summary>
    private static HashSet<TenetName> UsedByKernel(ConstantInfo c)
    {
        HashSet<TenetName> used = Replay.UsedConstants(c);
        void Visit(Expr e) => ExprOps.ForEach(e, (t, _) =>
        {
            if (t is ConstExpr k)
            {
                used.Add(k.Name);
            }
            return true;
        });
        switch (c)
        {
            case OpaqueInfo o:
                Visit(o.OpaqueValue);
                break;
            case RecursorInfo r:
                foreach (RecursorRule rule in r.Rules)
                {
                    used.Add(rule.Ctor);
                    Visit(rule.Rhs);
                }
                break;
            case InductiveInfo i:
                used.UnionWith(i.Ctors);
                used.UnionWith(i.All);
                break;
            case ConstructorInfo k:
                used.Add(k.Induct);
                break;
        }
        return used;
    }

    /// <summary>
    /// A deterministic SHA-256 of a constant and of the terms in it. Expressions are hashed once per node (they are
    /// shared heavily); binder names are left out, since the kernel ignores them.
    /// </summary>
    private sealed class Hasher
    {
        private readonly Dictionary<Expr, byte[]> _exprs = new(ReferenceEqualityComparer.Instance);

        public byte[] Constant(ConstantInfo c)
        {
            using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            void Str(string s)
            {
                byte[] b = Encoding.UTF8.GetBytes(s);
                h.AppendData(BitConverter.GetBytes(b.Length));
                h.AppendData(b);
            }
            void Int(long v) => h.AppendData(BitConverter.GetBytes(v));
            void Names(IEnumerable<TenetName> names)
            {
                var list = names.ToList();
                Int(list.Count);
                foreach (TenetName n in list)
                {
                    Str(n.ToString());
                }
            }
            Str(c.KindName);
            Str(c.Name.ToString());
            Names(c.LevelParams);
            h.AppendData(Of(c.Type));
            Int(c.IsUnsafe ? 1 : 0);
            switch (c)
            {
                case DefinitionInfo d:
                    h.AppendData(Of(d.Value));
                    Str(d.Hints.ToString() ?? "");
                    Str(d.Safety.ToString());
                    Names(d.All);
                    break;
                case TheoremInfo t:
                    h.AppendData(Of(t.Value));
                    Names(t.All);
                    break;
                case OpaqueInfo o:
                    h.AppendData(Of(o.OpaqueValue));
                    Names(o.All);
                    break;
                case QuotInfo q:
                    Str(q.Kind.ToString());
                    break;
                case InductiveInfo i:
                    Int(i.NumParams);
                    Int(i.NumIndices);
                    Names(i.All);
                    Names(i.Ctors);
                    Int(i.NumNested);
                    Int(i.IsRec ? 1 : 0);
                    Int(i.IsReflexive ? 1 : 0);
                    break;
                case ConstructorInfo k:
                    Str(k.Induct.ToString());
                    Int(k.Cidx);
                    Int(k.NumParams);
                    Int(k.NumFields);
                    break;
                case RecursorInfo r:
                    Names(r.All);
                    Int(r.NumParams);
                    Int(r.NumIndices);
                    Int(r.NumMotives);
                    Int(r.NumMinors);
                    Int(r.K ? 1 : 0);
                    Int(r.Rules.Length);
                    foreach (RecursorRule rule in r.Rules)
                    {
                        Str(rule.Ctor.ToString());
                        Int(rule.NumFields);
                        h.AppendData(Of(rule.Rhs));
                    }
                    break;
                case AxiomInfo:
                    break;
                default:
                    // A kind this hasher does not know: make the key unique, so it is never reused.
                    Str(Guid.NewGuid().ToString());
                    break;
            }
            return h.GetHashAndReset();
        }

        /// <summary>The hash of an expression, iteratively (terms can be far deeper than any stack).</summary>
        public byte[] Of(Expr root)
        {
            if (_exprs.TryGetValue(root, out byte[]? done))
            {
                return done;
            }
            var stack = new Stack<(Expr E, bool Ready)>();
            stack.Push((root, false));
            while (stack.TryPop(out var top))
            {
                Expr e = top.E;
                if (_exprs.ContainsKey(e))
                {
                    continue;
                }
                IReadOnlyList<Expr> children = Children(e);
                if (!top.Ready)
                {
                    stack.Push((e, true));
                    foreach (Expr child in children)
                    {
                        if (!_exprs.ContainsKey(child))
                        {
                            stack.Push((child, false));
                        }
                    }
                    continue;
                }
                using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                void Str(string s)
                {
                    byte[] b = Encoding.UTF8.GetBytes(s);
                    h.AppendData(BitConverter.GetBytes(b.Length));
                    h.AppendData(b);
                }
                h.AppendData([(byte)e.Kind]);
                switch (e)
                {
                    case BVarExpr b:
                        h.AppendData(BitConverter.GetBytes(b.Idx));
                        break;
                    case FVarExpr f:
                        h.AppendData(BitConverter.GetBytes(f.Id.Value));
                        break;
                    case SortExpr s:
                        Str(s.Level.ToString());
                        break;
                    case ConstExpr k:
                        Str(k.Name.ToString());
                        h.AppendData(BitConverter.GetBytes(k.Levels.Length));
                        foreach (Level l in k.Levels)
                        {
                            Str(l.ToString());
                        }
                        break;
                    case BindingExpr bind:
                        Str(bind.Info.ToString());
                        break;
                    case LitExpr lit:
                        Str(lit.Value switch
                        {
                            NatLiteral n => "nat " + n.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            StrLiteral s => "str " + s.Value,
                            var other => "lit " + other,
                        });
                        break;
                    case ProjExpr p:
                        Str(p.StructName.ToString());
                        h.AppendData(BitConverter.GetBytes(p.Idx));
                        break;
                }
                foreach (Expr child in children)
                {
                    h.AppendData(_exprs[child]);
                }
                _exprs[e] = h.GetHashAndReset();
            }
            return _exprs[root];
        }

        private static IReadOnlyList<Expr> Children(Expr e) => e switch
        {
            AppExpr a => [a.Fn, a.Arg],
            BindingExpr b => [b.Domain, b.Body],
            LetExpr l => [l.Type, l.Value, l.Body],
            ProjExpr p => [p.Struct],
            _ => [],
        };
    }
}
