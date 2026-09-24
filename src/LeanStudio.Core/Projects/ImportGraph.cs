namespace LeanStudio.Core.Projects;

/// <summary>An import of one module by another: where it is written.</summary>
/// <param name="Module">The module whose file has the import.</param>
/// <param name="File">That file.</param>
/// <param name="Line">The 0-based line of the <c>import</c>.</param>
/// <param name="Imported">The module it imports.</param>
public sealed record ImportEdge(string Module, string File, int Line, string Imported);

/// <summary>
/// Which of a project's modules import which, read from the sources (so it is current without a build): what a
/// module imports, what imports it, and how many modules Lake rebuilds when it changes.
/// </summary>
public sealed class ImportGraph
{
    private readonly Dictionary<string, List<ImportEdge>> _imports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ImportEdge>> _importedBy = new(StringComparer.Ordinal);

    /// <summary>The project's modules and their files.</summary>
    public IReadOnlyDictionary<string, string> Files { get; }

    private ImportGraph(Dictionary<string, string> files)
    {
        Files = files;
    }

    /// <summary>Read every source file of <paramref name="project"/>.</summary>
    public static ImportGraph Build(LeanProject project)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string f in project.SourceFiles())
        {
            if (project.ModuleNameOf(f) is string m)
            {
                files.TryAdd(m, f);
            }
        }
        var g = new ImportGraph(files);
        foreach ((string module, string file) in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }
            foreach ((string imported, int line) in Workflow.ImportCheck.ImportLines(text))
            {
                var edge = new ImportEdge(module, file, line, imported);
                g.Add(g._imports, module, edge);
                g.Add(g._importedBy, imported, edge);
            }
        }
        return g;
    }

    private void Add(Dictionary<string, List<ImportEdge>> map, string key, ImportEdge edge)
    {
        if (!map.TryGetValue(key, out List<ImportEdge>? l))
        {
            map[key] = l = [];
        }
        l.Add(edge);
    }

    /// <summary>What <paramref name="module"/> imports directly, in the order written.</summary>
    public IReadOnlyList<ImportEdge> ImportsOf(string module) => _imports.TryGetValue(module, out List<ImportEdge>? l) ? l : [];

    /// <summary>The project's modules that import <paramref name="module"/> directly, by name.</summary>
    public IReadOnlyList<ImportEdge> ImportedBy(string module) =>
        _importedBy.TryGetValue(module, out List<ImportEdge>? l) ? l.OrderBy(e => e.Module, StringComparer.Ordinal).ToList() : [];

    /// <summary>
    /// Every project module that imports <paramref name="module"/>, directly or through others: what Lake rebuilds
    /// when it changes. Sorted.
    /// </summary>
    public IReadOnlyList<string> Dependents(string module)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var todo = new Stack<string>([module]);
        while (todo.TryPop(out string? m))
        {
            foreach (ImportEdge e in _importedBy.TryGetValue(m, out List<ImportEdge>? l) ? l : [])
            {
                if (seen.Add(e.Module))
                {
                    todo.Push(e.Module);
                }
            }
        }
        seen.Remove(module);
        return seen.Order(StringComparer.Ordinal).ToList();
    }
}
