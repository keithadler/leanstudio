using System.Text;
using System.Text.RegularExpressions;
using LeanStudio.Core.Editing;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Toolchains;

namespace LeanStudio.Core.Workflow;

/// <summary>A Lean declaration implemented in C: <c>@[extern "c_name"] opaque name : type</c>. Lines are 0-based.</summary>
public sealed record ExternBinding(string LeanName, string CName, string Path, int Line, int Column, string Signature);

/// <summary>A C function in the project's C files: where it is, how many parameters it takes, whether it has a body.</summary>
public sealed record CFunctionSite(string Name, string Path, int Line, int Parameters, bool IsDefinition);

/// <summary>Something wrong with a binding, at the Lean declaration.</summary>
public sealed record FfiProblem(string Path, int Line, int Column, string Message, bool IsError);

/// <summary>
/// Lean's C foreign function interface, from the editor's side: which Lean declarations are implemented in C
/// (<c>@[extern "…"]</c>), which C functions the project defines, whether the two agree (the C function exists,
/// and takes as many arguments as Lean passes), and C stubs with the signature Lean expects.
///
/// The calling convention follows Lean's FFI documentation: <c>UInt8</c>, <c>Bool</c>, <c>UInt16</c>,
/// <c>UInt32</c>, <c>Char</c>, <c>UInt64</c>, <c>USize</c>, <c>Float</c>, <c>Float32</c> and the signed
/// fixed-width integers are passed unboxed as the matching C types; everything else is a <c>lean_object*</c>,
/// borrowed when marked <c>@&amp;</c>; an <c>IO</c> function takes one more argument, the world, and returns an
/// <c>IO</c> result.
/// </summary>
public static partial class Ffi
{
    [GeneratedRegex(@"@\[\s*extern\s+(?:(?:c|cpp)\s+)?""(?<c>[A-Za-z_]\w*)""\s*\]")]
    private static partial Regex ExternAttribute();

    [GeneratedRegex(@"@\[\s*extern\b")]
    private static partial Regex AnyExtern();

    [GeneratedRegex(@"^\s*((private|protected|noncomputable|unsafe|partial)\s+)*(opaque|def|axiom|instance|abbrev|theorem)\s+(?<name>[^\s:({\[]+)(?<rest>.*)$")]
    private static partial Regex Declaration();

    // A C function head at the start of a line: return type, name, parameters, then a body or a semicolon.
    [GeneratedRegex(@"^(?!\s*(?:if|for|while|switch|return|else|do)\b)\s*(?:(?:LEAN_EXPORT|static|inline|extern|const|unsigned|signed|struct)\s+)*[A-Za-z_][\w\s\*]*?[\s\*](?<name>[A-Za-z_]\w*)\s*\((?<params>[^;{}]*)\)\s*(?<end>\{|;)?", RegexOptions.Multiline)]
    private static partial Regex CFunctionHead();

    /// <summary>The C and C++ source files of a project, outside build output and packages.</summary>
    public static IReadOnlyList<string> CFiles(string root)
    {
        var list = new List<string>();
        var stack = new Stack<string>([root]);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            string name = System.IO.Path.GetFileName(dir);
            if (dir != root && (name.StartsWith('.') || name is "build" or "node_modules"))
            {
                continue;
            }
            try
            {
                foreach (string d in Directory.EnumerateDirectories(dir))
                {
                    stack.Push(d);
                }
                list.AddRange(Directory.EnumerateFiles(dir).Where(IsCFile));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    public static bool IsCFile(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() is ".c" or ".h" or ".cpp" or ".cc" or ".hpp" or ".cxx";

    /// <summary>The <c>@[extern "…"]</c> declarations in a Lean file.</summary>
    public static IEnumerable<ExternBinding> ExternsIn(string path, IReadOnlyList<string> lines)
    {
        bool inBlock = false;
        for (int i = 0; i < lines.Count; i++)
        {
            string code = Proofs.ProofSteps.StripComments(lines[i], ref inBlock);
            Match a = ExternAttribute().Match(code);
            if (!a.Success)
            {
                continue;
            }
            // The declaration follows the attribute, on the same line or the next ones (past other attributes).
            string rest = code[(a.Index + a.Length)..];
            int line = i;
            if (rest.Trim().Length == 0)
            {
                line = i + 1;
                while (line < lines.Count && (lines[line].Trim().Length == 0 || lines[line].TrimStart().StartsWith("@[", StringComparison.Ordinal)))
                {
                    line++;
                }
                rest = line < lines.Count ? lines[line] : "";
            }
            Match d = Declaration().Match(rest);
            if (!d.Success)
            {
                continue;
            }
            // The signature can run on over indented lines, up to := or the next command.
            var sig = new StringBuilder(d.Groups["rest"].Value);
            for (int j = line + 1; j < lines.Count && lines[j].Length > 0 && char.IsWhiteSpace(lines[j][0]) && !sig.ToString().Contains(":=", StringComparison.Ordinal); j++)
            {
                sig.Append(' ').Append(lines[j].Trim());
            }
            string signature = sig.ToString();
            int assign = signature.IndexOf(":=", StringComparison.Ordinal);
            if (assign >= 0)
            {
                signature = signature[..assign];
            }
            yield return new ExternBinding(d.Groups["name"].Value, a.Groups["c"].Value, path, i, a.Index, Regex.Replace(signature, @"\s+", " ").Trim());
        }
    }

    /// <summary>The functions a C file declares or defines.</summary>
    public static IEnumerable<CFunctionSite> CFunctionsIn(string path, string text)
    {
        bool[] code = CodeMaskC(text);
        foreach (Match m in CFunctionHead().Matches(text))
        {
            int at = m.Groups["name"].Index;
            if (!code[at] || !m.Groups["end"].Success)
            {
                continue;
            }
            string name = m.Groups["name"].Value;
            if (name is "if" or "for" or "while" or "switch" or "return" or "sizeof")
            {
                continue;
            }
            int line = 0;
            for (int k = 0; k < at; k++)
            {
                if (text[k] == '\n')
                {
                    line++;
                }
            }
            yield return new CFunctionSite(name, path, line, CountParameters(m.Groups["params"].Value), m.Groups["end"].Value == "{");
        }
    }

    /// <summary>For C text, whether each character is code (not a comment or string).</summary>
    private static bool[] CodeMaskC(string t)
    {
        var mask = new bool[t.Length];
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i] == '/' && i + 1 < t.Length && t[i + 1] == '/')
            {
                while (i < t.Length && t[i] != '\n')
                {
                    i++;
                }
                continue;
            }
            if (t[i] == '/' && i + 1 < t.Length && t[i + 1] == '*')
            {
                int end = t.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? t.Length : end + 1;
                continue;
            }
            if (t[i] == '"')
            {
                for (i++; i < t.Length && t[i] != '"'; i++)
                {
                    if (t[i] == '\\')
                    {
                        i++;
                    }
                }
                continue;
            }
            mask[i] = true;
        }
        return mask;
    }

    private static int CountParameters(string ps)
    {
        string p = ps.Trim();
        if (p.Length == 0 || p == "void")
        {
            return 0;
        }
        int depth = 0, count = 1;
        foreach (char c in p)
        {
            depth += c is '(' or '[' ? 1 : c is ')' or ']' ? -1 : 0;
            if (c == ',' && depth == 0)
            {
                count++;
            }
        }
        return count;
    }

    // ---- the calling convention ----

    private static readonly Dictionary<string, string> Scalars = new(StringComparer.Ordinal)
    {
        ["UInt8"] = "uint8_t", ["Bool"] = "uint8_t", ["UInt16"] = "uint16_t", ["UInt32"] = "uint32_t", ["Char"] = "uint32_t",
        ["UInt64"] = "uint64_t", ["USize"] = "size_t", ["Float"] = "double", ["Float32"] = "float",
        ["Int8"] = "int8_t", ["Int16"] = "int16_t", ["Int32"] = "int32_t", ["Int64"] = "int64_t", ["ISize"] = "ptrdiff_t",
    };

    /// <summary>A parameter or result of an extern: its Lean type, and whether Lean lends it (<c>@&amp;</c>).</summary>
    public sealed record FfiType(string Lean, bool Borrowed)
    {
        public bool IsScalar => Scalars.ContainsKey(Lean);
        public string C(bool result) => Scalars.TryGetValue(Lean, out string? c) ? c : result ? "lean_obj_res" : Borrowed ? "b_lean_obj_arg" : "lean_obj_arg";
    }

    /// <summary>
    /// The explicit parameters and the result of a Lean signature (<c>(x : UInt32) (s : @&amp; String) : IO Unit</c>),
    /// and whether it is an <c>IO</c> action. Implicit and instance arguments are left out.
    /// </summary>
    public static (IReadOnlyList<(string Name, FfiType Type)> Parameters, FfiType Result, bool IsIO) Parse(string signature)
    {
        var ps = new List<(string, FfiType)>();
        int i = 0, depth = 0, colon = -1;
        // Split off the binders before the top-level colon.
        for (; i < signature.Length; i++)
        {
            char c = signature[i];
            depth += c is '(' or '{' or '[' or '⦃' ? 1 : c is ')' or '}' or ']' or '⦄' ? -1 : 0;
            if (c == ':' && depth == 0)
            {
                colon = i;
                break;
            }
        }
        string binders = colon < 0 ? "" : signature[..colon];
        string type = colon < 0 ? signature : signature[(colon + 1)..];
        foreach (Match b in Regex.Matches(binders, @"\((?<names>[^:()]+):(?<type>[^()]*(\([^()]*\)[^()]*)*)\)"))
        {
            FfiType t = TypeOf(b.Groups["type"].Value);
            foreach (string n in b.Groups["names"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                ps.Add((n, t));
            }
        }
        // Arrows in the type are more explicit arguments.
        var parts = SplitArrows(type);
        for (int k = 0; k < parts.Count - 1; k++)
        {
            ps.Add(($"x{ps.Count + 1}", TypeOf(parts[k])));
        }
        string result = parts[^1].Trim();
        bool io = Regex.IsMatch(result, @"^(IO|BaseIO|EIO\s+\S+)\s");
        if (io)
        {
            result = Regex.Replace(result, @"^(IO|BaseIO|EIO\s+\S+)\s+", "");
        }
        return (ps, TypeOf(result), io);
    }

    private static List<string> SplitArrows(string type)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < type.Length; i++)
        {
            char c = type[i];
            depth += c is '(' or '[' or '{' ? 1 : c is ')' or ']' or '}' ? -1 : 0;
            if (depth == 0 && (c == '→' || (c == '-' && i + 1 < type.Length && type[i + 1] == '>')))
            {
                parts.Add(type[start..i]);
                start = i + (c == '→' ? 1 : 2);
            }
        }
        parts.Add(type[start..]);
        return parts;
    }

    private static FfiType TypeOf(string t)
    {
        string s = t.Trim();
        bool borrowed = s.StartsWith("@&", StringComparison.Ordinal);
        if (borrowed)
        {
            s = s[2..].Trim();
        }
        s = s.Trim('(', ')').Trim();
        return new FfiType(s, borrowed);
    }

    /// <summary>How many C arguments Lean passes to an extern: its explicit parameters, and the world for IO.</summary>
    public static int Arity(ExternBinding b)
    {
        var (ps, _, io) = Parse(b.Signature);
        return ps.Count + (io ? 1 : 0);
    }

    /// <summary>A C function with the signature Lean expects for a binding, and a placeholder body.</summary>
    public static string Stub(ExternBinding b)
    {
        var (ps, result, io) = Parse(b.Signature);
        var args = ps.Select(p => $"{p.Type.C(false)} {CName(p.Name)}").ToList();
        if (io)
        {
            args.Add("lean_obj_arg world");
        }
        string ret = io ? "lean_obj_res" : result.C(true);
        var sb = new StringBuilder();
        sb.Append("/* ").Append(b.LeanName).Append(' ').Append(b.Signature).Append(" */\n");
        sb.Append("LEAN_EXPORT ").Append(ret).Append(' ').Append(b.CName).Append('(').Append(args.Count == 0 ? "void" : string.Join(", ", args)).Append(") {\n");
        foreach (var p in ps.Where(p => !p.Type.IsScalar && !p.Type.Borrowed))
        {
            sb.Append("    /* ").Append(CName(p.Name)).Append(" is owned: lean_dec_ref(").Append(CName(p.Name)).Append(") when done with it */\n");
        }
        sb.Append("    /* TODO: implement */\n");
        if (io)
        {
            sb.Append("    return lean_io_result_mk_ok(").Append(Boxed(result, "0")).Append(");\n");
        }
        else
        {
            sb.Append("    return ").Append(result.IsScalar ? "0" : "lean_box(0)").Append(";\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    private static string CName(string leanName) => Regex.Replace(leanName.TrimEnd('\''), @"[^\w]", "_");

    private static string Boxed(FfiType t, string value) => t.Lean switch
    {
        "UInt32" or "Char" => $"lean_box_uint32({value})",
        "UInt64" => $"lean_box_uint64({value})",
        "USize" => $"lean_box_usize({value})",
        "Float" => $"lean_box_float({value})",
        "Float32" => $"lean_box_float32({value})",
        _ => $"lean_box({value})",
    };

    /// <summary>The Lean side of a new binding.</summary>
    public static string LeanDeclaration(string leanName, string cName, string signature) =>
        $"@[extern \"{cName}\"]\nopaque {leanName} {signature.Trim()}\n";

    // ---- checking ----

    /// <summary>
    /// Every binding in the project, every C function, and what does not agree: an extern whose C function
    /// the project does not have (runtime functions, <c>lean_*</c>, are Lean's), or one whose C function takes
    /// a different number of arguments than Lean passes.
    /// </summary>
    public static (IReadOnlyList<ExternBinding> Externs, IReadOnlyList<CFunctionSite> Functions, IReadOnlyList<FfiProblem> Problems) Check(
        string root, Func<string, string?>? openText = null)
    {
        string Read(string f) => openText?.Invoke(f) ?? File.ReadAllText(f);
        var externs = new List<ExternBinding>();
        foreach (string f in ProjectSearch.Files(root).Where(f => f.EndsWith(".lean", StringComparison.OrdinalIgnoreCase)))
        {
            string text;
            try
            {
                text = Read(f);
            }
            catch (IOException)
            {
                continue;
            }
            if (AnyExtern().IsMatch(text))
            {
                externs.AddRange(ExternsIn(f, text.Split('\n')));
            }
        }
        var functions = new List<CFunctionSite>();
        foreach (string f in CFiles(root))
        {
            try
            {
                functions.AddRange(CFunctionsIn(f, Read(f)));
            }
            catch (IOException)
            {
            }
        }
        var byName = functions.GroupBy(f => f.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var problems = new List<FfiProblem>();
        foreach (ExternBinding b in externs)
        {
            if (!byName.TryGetValue(b.CName, out List<CFunctionSite>? sites))
            {
                if (!b.CName.StartsWith("lean_", StringComparison.Ordinal))
                {
                    problems.Add(new FfiProblem(b.Path, b.Line, b.Column,
                        $"No C function {b.CName} in the project's C files" + (functions.Count == 0 ? " (it has none yet)" : "") + ". Lean Studio can write a stub for it.", false));
                }
                continue;
            }
            CFunctionSite site = sites.FirstOrDefault(s => s.IsDefinition) ?? sites[0];
            int arity = Arity(b);
            if (site.Parameters != arity)
            {
                problems.Add(new FfiProblem(b.Path, b.Line, b.Column,
                    $"{b.CName} takes {site.Parameters} argument{(site.Parameters == 1 ? "" : "s")} in C ({System.IO.Path.GetFileName(site.Path)}:{site.Line + 1}), "
                    + $"but Lean passes {arity} for {b.LeanName}" + (Parse(b.Signature).IsIO ? " (its parameters and the IO world)" : "") + ".", true));
            }
        }
        return (externs, functions, problems);
    }

    /// <summary>The C name an <c>@[extern]</c> attribute or declaration on a line binds, for go to definition.</summary>
    public static string? CNameAt(IReadOnlyList<string> lines, int line)
    {
        for (int i = line; i >= 0 && i >= line - 3; i--)
        {
            Match a = ExternAttribute().Match(lines[i]);
            if (a.Success)
            {
                // The attribute's own line, or the declaration it is attached to.
                return i == line || Declaration().IsMatch(lines[line]) || lines[line].Trim().Length == 0 ? a.Groups["c"].Value : null;
            }
            if (i < line && Declaration().IsMatch(lines[i]))
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>The include flags clangd needs for Lean's headers: the toolchain's include directory.</summary>
    public static IReadOnlyList<string> CompileFlags(LeanProject project)
    {
        var flags = new List<string> { "-std=c11" };
        string? tc = project.Toolchain;
        string? dir = tc is not null ? Path.Combine(Elan.ToolchainDirectory(tc), "include") : null;
        if (dir is null || !Directory.Exists(dir))
        {
            // Loose files: the newest installed toolchain.
            string toolchains = Path.Combine(Elan.Home, "toolchains");
            dir = Directory.Exists(toolchains)
                ? Directory.GetDirectories(toolchains).Select(t => Path.Combine(t, "include")).Where(Directory.Exists).OrderDescending(StringComparer.Ordinal).FirstOrDefault()
                : null;
        }
        if (dir is not null)
        {
            flags.Add("-I" + dir);
        }
        return flags;
    }
}
