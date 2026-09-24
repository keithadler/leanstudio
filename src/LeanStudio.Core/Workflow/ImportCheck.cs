using System.Text.Json;
using System.Text.RegularExpressions;
using LeanStudio.Core.Processes;
using LeanStudio.Core.Projects;

namespace LeanStudio.Core.Workflow;

/// <summary>What the import check says about one import.</summary>
/// <param name="Module">The imported module.</param>
/// <param name="Keep">Whether the file needs it.</param>
/// <param name="Reason">For an import it doesn't need: <c>unused</c>, or <c>implied</c> (another import brings it in).</param>
/// <param name="ImpliedBy">For an implied import, the import that brings it in.</param>
/// <param name="Uses">For an import it needs, a few of the names that only it provides.</param>
public sealed record ImportVerdict(string Module, bool Keep, string? Reason, string? ImpliedBy, IReadOnlyList<string> Uses)
{
    /// <summary>Why it can go, in words; empty for an import that stays.</summary>
    public string Explanation => Keep ? ""
        : Reason == "implied" && ImpliedBy is not null ? $"{Module} is already imported by {ImpliedBy}"
        : $"Nothing in this file uses {Module}";
}

/// <summary>The import check of one file.</summary>
/// <param name="Errors">How many errors Lean reported while elaborating it. With errors, the check may miss uses.</param>
/// <param name="Imports">Each import written in the file, in order.</param>
public sealed record ImportReport(int Errors, IReadOnlyList<ImportVerdict> Imports)
{
    /// <summary>The imports the file doesn't need.</summary>
    public IReadOnlyList<ImportVerdict> Removable => Imports.Where(i => !i.Keep).ToList();
}

/// <summary>
/// Which imports a file needs (Lean ▸ Remove Unused Imports). Lean elaborates the file with its own frontend, and
/// the check collects the modules of every constant the file uses, of every elaborator, tactic and macro its syntax
/// went through, and of every notation it wrote. An import can go when all of that is still reachable through the
/// other imports: because nothing uses it, or because another import brings it in. Works on any file, not only
/// those in Lean's <c>module</c> system that <c>lake shake</c> handles. The file's imports must be built.
/// </summary>
public static class ImportCheck
{
    /// <summary>The Lean program that does the check; it prints a JSON report.</summary>
    public const string Script = """
        import Lean
        open Lean Elab

        /-!
        Lean Studio's import check. Elaborates a file with Lean's own frontend and works out which of its imports it
        needs: the modules of every constant it uses (in its declarations and in what it elaborated), every elaborator,
        tactic and macro its syntax went through, and every notation it wrote. An import is removable when everything
        needed is still reachable through the other imports. Prints JSON.
        -/

        partial def kindsOf (stx : Syntax) (acc : NameSet) : NameSet :=
          match stx with
          | .node _ k args => args.foldl (fun a s => kindsOf s a) (acc.insert k)
          | _ => acc

        partial def collect (t : InfoTree) (acc : NameSet) : NameSet :=
          match t with
          | .context _ t => collect t acc
          | .node i cs =>
            let acc := match i with
              | .ofTermInfo ti => kindsOf ti.stx ((ti.expr.getUsedConstants.foldl (·.insert ·) acc).insert ti.elaborator)
              | .ofTacticInfo ti => kindsOf ti.stx (acc.insert ti.elaborator)
              | .ofCommandInfo ci => kindsOf ci.stx (acc.insert ci.elaborator)
              | .ofMacroExpansionInfo mi => kindsOf mi.stx acc
              | _ => acc
            cs.foldl (fun a c => collect c a) acc
          | .hole _ => acc

        unsafe def main (args : List String) : IO UInt32 := do
          enableInitializersExecution
          let file := args[0]!
          let input ← IO.FS.readFile file
          initSearchPath (← findSysroot)
          let inputCtx := Parser.mkInputContext input file
          let (header, parserState, messages) ← Parser.parseHeader inputCtx
          let (env, messages) ← processHeader header {} messages inputCtx
          let cmdState := { Command.mkState env messages {} with infoState := { enabled := true } }
          let s ← IO.processCommands inputCtx parserState cmdState
          let env := s.commandState.env
          let errors := s.commandState.messages.toList.filter (·.severity == .error) |>.length
          let mut used : NameSet := {}
          for t in s.commandState.infoState.trees do
            used := collect t used
          for (_, ci) in env.constants.map₂.toList do
            used := ci.type.getUsedConstants.foldl (·.insert ·) used
            if let some v := ci.value? then
              used := v.getUsedConstants.foldl (·.insert ·) used
          let names := env.header.moduleNames
          let moduleOf (n : Name) : Option Name := (env.getModuleIdxFor? n).map (names[·.toNat]!)
          -- Which module each needed module is needed for (one constant each, to explain it).
          let mut needed : Std.HashMap Name Name := {}
          for n in used do
            if let some m := moduleOf n then
              unless needed.contains m do needed := needed.insert m n
          -- Transitive imports of each module.
          let mut index : Std.HashMap Name Nat := {}
          for h : i in [0:names.size] do index := index.insert names[i] i
          let rec closure (m : Name) (seen : NameSet) (fuel : Nat) : NameSet :=
            match fuel with
            | 0 => seen
            | fuel + 1 =>
              if seen.contains m then seen else
              let seen := seen.insert m
              match index[m]? with
              | some i => env.header.moduleData[i]!.imports.foldl (fun acc imp => closure imp.module acc fuel) seen
              | none => seen
          let fuel := names.size + 1
          -- The imports written in the file (Init comes on its own unless the file is `prelude`).
          let written : Array Name := env.header.imports.filterMap fun i =>
            if i.module == `Init && !(input.splitOn "import Init").length > 1 then none else some i.module
          let base : NameSet := if env.header.imports.any (·.module == `Init) then closure `Init {} fuel else {}
          let cover (ms : Array Name) : NameSet := ms.foldl (fun acc m => closure m acc fuel) base
          let covers (c : NameSet) : Bool := needed.toList.all (fun (m, _) => c.contains m)
          let mut keep := written
          for m in written.reverse do
            let others := keep.filter (· != m)
            if covers (cover others) then keep := others
          let mut out : Array Json := #[]
          for m in written do
            if keep.contains m then
              let others := keep.filter (· != m)
              let rest := cover others
              let mine := closure m {} fuel
              let because := needed.toList.filter (fun (nm, _) => mine.contains nm && !rest.contains nm) |>.map (·.2) |>.take 3
              out := out.push (Json.mkObj [("module", toJson m), ("keep", true), ("uses", toJson because)])
            else
              let implier := keep.find? (fun k => (closure k {} fuel).contains m)
              out := out.push (Json.mkObj [("module", toJson m), ("keep", false),
                ("reason", toJson (if implier.isSome then "implied" else "unused")), ("by", toJson implier)])
          IO.println (Json.mkObj [("errors", toJson errors), ("imports", Json.arr out)]).compress
          return 0
        """;

    /// <summary>Check the imports of <paramref name="text"/>, the contents of <paramref name="sourcePath"/> (saved or not).</summary>
    /// <exception cref="InvalidOperationException">Lean couldn't run the check (the imports aren't built, say).</exception>
    public static async Task<ImportReport> RunAsync(LeanProject project, string sourcePath, string text, CancellationToken ct = default)
    {
        string mirror = await LeanCli.MirrorAsync(project, sourcePath, text, "imports", ct).ConfigureAwait(false);
        string script = Path.Combine(LeanCli.MirrorRoot(project, "imports"), "LeanStudioImportCheck.lean");
        await File.WriteAllTextAsync(script, Script, ct).ConfigureAwait(false);
        ProcessResult r = await LeanCli.RunAsync(project, ["--run", script, mirror], ct).ConfigureAwait(false);
        string? json = r.Output.Split('\n').LastOrDefault(l => l.StartsWith("{\"errors\"", StringComparison.Ordinal));
        if (json is null)
        {
            string why = r.Output.Trim();
            throw new InvalidOperationException(string.IsNullOrEmpty(why) ? "Lean couldn't check the imports" : why);
        }
        return Parse(json);
    }

    /// <summary>Read the check's JSON report.</summary>
    public static ImportReport Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        var list = new List<ImportVerdict>();
        foreach (JsonElement i in root.GetProperty("imports").EnumerateArray())
        {
            static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            list.Add(new ImportVerdict(
                Str(i, "module") ?? "",
                i.GetProperty("keep").GetBoolean(),
                Str(i, "reason"),
                Str(i, "by"),
                i.TryGetProperty("uses", out JsonElement u) && u.ValueKind == JsonValueKind.Array ? u.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : []));
        }
        return new ImportReport(root.GetProperty("errors").GetInt32(), list);
    }

    private static readonly Regex ImportLine = new(@"^\s*(?:(?:public|private|meta)\s+)*import\s+(?:all\s+)?(?<m>[^\s-]+)\s*(?:--.*)?$", RegexOptions.Compiled);

    /// <summary>The 0-based line of each <c>import</c> of a module in <paramref name="text"/>.</summary>
    public static IReadOnlyDictionary<string, int> ImportLines(string text)
    {
        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        string[] all = text.Split('\n');
        for (int i = 0; i < all.Length; i++)
        {
            Match m = ImportLine.Match(all[i].TrimEnd('\r'));
            if (m.Success)
            {
                lines.TryAdd(m.Groups["m"].Value, i);
            }
        }
        return lines;
    }

    /// <summary><paramref name="text"/> without the import lines of <paramref name="modules"/>.</summary>
    public static string Remove(string text, IEnumerable<string> modules)
    {
        var drop = new HashSet<string>(modules, StringComparer.Ordinal);
        IReadOnlyDictionary<string, int> at = ImportLines(text);
        var lines = new HashSet<int>(drop.Where(at.ContainsKey).Select(m => at[m]));
        return string.Join('\n', text.Split('\n').Where((_, i) => !lines.Contains(i)));
    }
}
