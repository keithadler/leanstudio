using System.Text.Json;

namespace LeanStudio.Core.Projects;

/// <summary>An import that goes against the project's layers.</summary>
/// <param name="Module">The module with the import.</param>
/// <param name="File">Its file.</param>
/// <param name="Line">0-based line of the import.</param>
/// <param name="Imported">The module it imports.</param>
/// <param name="FromLayer">The layer the importing module is in (0 is the lowest).</param>
/// <param name="ToLayer">The higher layer the imported module is in.</param>
public sealed record LayerViolation(string Module, string File, int Line, string Imported, int FromLayer, int ToLayer);

/// <summary>
/// A formalization of a great theorem is built in layers: general facts at the bottom, the hard new mathematics above, the
/// final proof on top. Nothing in a lower layer should import from a higher one, or the bottom stops being general and the
/// project becomes one tangle. The layers are written down once (<c>.leanstudio/layers.json</c>, lowest first, each a list of module
/// prefixes) and checked against the imports.
/// </summary>
public sealed class LayerRules
{
    private readonly IReadOnlyList<IReadOnlyList<string>> _layers;

    /// <summary>The layers, lowest first, each a list of module names or prefixes (<c>FLT.Basic</c> covers <c>FLT.Basic.Group</c>).</summary>
    public IReadOnlyList<IReadOnlyList<string>> Layers => _layers;

    /// <summary>Layers of <paramref name="layers"/>, lowest first.</summary>
    public LayerRules(IReadOnlyList<IReadOnlyList<string>> layers) => _layers = layers;

    /// <summary>Where the rules of the project at <paramref name="root"/> are kept.</summary>
    public static string FileFor(string root) => Path.Combine(root, ".leanstudio", "layers.json");

    /// <summary>
    /// The rules in <paramref name="json"/> (<c>{"layers": [["A.Basic"], ["A.Main"]]}</c>), or null when it is not that.
    /// </summary>
    public static LayerRules? Parse(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("layers", out JsonElement layers) || layers.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var result = new List<IReadOnlyList<string>>();
            foreach (JsonElement layer in layers.EnumerateArray())
            {
                if (layer.ValueKind == JsonValueKind.String)
                {
                    result.Add([layer.GetString()!]);
                }
                else if (layer.ValueKind == JsonValueKind.Array && layer.EnumerateArray().All(e => e.ValueKind == JsonValueKind.String))
                {
                    result.Add([.. layer.EnumerateArray().Select(e => e.GetString()!)]);
                }
                else
                {
                    return null;
                }
            }
            return new LayerRules(result);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The layer of <paramref name="module"/> (0 is the lowest), or -1 when no layer names it. The most specific prefix wins.</summary>
    public int LayerOf(string module)
    {
        int best = -1, bestLength = -1;
        for (int i = 0; i < _layers.Count; i++)
        {
            foreach (string prefix in _layers[i])
            {
                if ((module == prefix || module.StartsWith(prefix + ".", StringComparison.Ordinal)) && prefix.Length > bestLength)
                {
                    best = i;
                    bestLength = prefix.Length;
                }
            }
        }
        return best;
    }

    /// <summary>The imports in <paramref name="edges"/> that reach from a layer up into a higher one.</summary>
    public IReadOnlyList<LayerViolation> Check(IEnumerable<ImportEdge> edges)
    {
        var found = new List<LayerViolation>();
        foreach (ImportEdge e in edges)
        {
            int from = LayerOf(e.Module), to = LayerOf(e.Imported);
            if (from >= 0 && to > from)
            {
                found.Add(new LayerViolation(e.Module, e.File, e.Line, e.Imported, from, to));
            }
        }
        return [.. found.OrderBy(v => v.Module, StringComparer.Ordinal).ThenBy(v => v.Line)];
    }
}
