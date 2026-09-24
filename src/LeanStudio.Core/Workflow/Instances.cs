using LeanStudio.Core.Processes;
using LeanStudio.Core.Projects;

namespace LeanStudio.Core.Workflow;

/// <summary>An instance of a type class: its name and type, as Lean prints it.</summary>
/// <param name="Name">The instance's declaration.</param>
/// <param name="Type">Its type, such as <c>{α : Type u} → Inhabited (Array α)</c>.</param>
public sealed record ClassInstance(string Name, string Type);

/// <summary>
/// Every instance of a type class that a file's imports declare (Lean ▸ Instances of Class at Cursor), asked of Lean
/// itself: it reads its instance table and keeps those whose conclusion is the class, with their types.
/// </summary>
public static class Instances
{
    /// <summary>The Lean program: <c>{0}</c> is the class as written, which Lean resolves as it would in the file.</summary>
    private const string Query = """

        open Lean Meta in
        #eval show MetaM Unit from do
          let cls ← resolveGlobalConstNoOverload (mkIdent (String.toName "{0}"))
          IO.println s!"class {{cls}}"
          let env ← getEnv
          let mut found : Array (Name × String) := #[]
          for (n, _) in (instanceExtension.getState env).instanceNames do
            let some ci := env.find? n | continue
            let head ← forallTelescopeReducing ci.type fun _ body => pure body.getAppFn.constName?
            if head == some cls then
              found := found.push (n, toString (← ppExpr ci.type))
          for (n, t) in found.qsort (fun a b => a.1.toString < b.1.toString) do
            IO.println s!"instance {{n}} : {{t}}"
        """;

    /// <summary>
    /// The instances of <paramref name="className"/> visible from <paramref name="text"/>'s imports (the file at
    /// <paramref name="sourcePath"/>, saved or not). Returns the class's full name, and the instances by name.
    /// </summary>
    /// <exception cref="InvalidOperationException">Lean couldn't answer: the class is unknown, say, or the imports aren't built.</exception>
    public static async Task<(string Class, IReadOnlyList<ClassInstance> Instances)> OfAsync(
        LeanProject project, string sourcePath, string text, string className, CancellationToken ct = default)
    {
        if (className.Any(c => char.IsWhiteSpace(c) || c == '"' || c == '\\'))
        {
            throw new InvalidOperationException($"{className} isn't a name");
        }
        // Only the imports: the instances the file can see, without elaborating the rest of it.
        string header = string.Join('\n', text.Split('\n').Where(l => l.TrimStart().StartsWith("import ", StringComparison.Ordinal)));
        string mirror = await LeanCli.MirrorAsync(project, sourcePath, "import Lean\n" + header + string.Format(System.Globalization.CultureInfo.InvariantCulture, Query, className), "instances", ct).ConfigureAwait(false);
        ProcessResult r = await LeanCli.RunAsync(project, [mirror], ct).ConfigureAwait(false);
        string? cls = null;
        var list = new List<ClassInstance>();
        foreach (string line in r.Output.Split('\n'))
        {
            string l = line.TrimEnd('\r');
            if (l.StartsWith("class ", StringComparison.Ordinal))
            {
                cls = l[6..];
            }
            else if (l.StartsWith("instance ", StringComparison.Ordinal) && l.IndexOf(" : ", StringComparison.Ordinal) is int colon and > 9)
            {
                list.Add(new ClassInstance(l[9..colon], l[(colon + 3)..]));
            }
        }
        if (cls is null)
        {
            string why = r.Output.Split('\n').FirstOrDefault(x => x.Contains("error", StringComparison.Ordinal))?.Trim() ?? r.Output.Trim();
            throw new InvalidOperationException(why.Length == 0 ? $"Lean couldn't list the instances of {className}" : why);
        }
        return (cls, list);
    }
}
