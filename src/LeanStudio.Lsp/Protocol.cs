using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeanStudio.Lsp;

internal static class LspJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static T? As<T>(this JsonElement e) =>
        e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? default : e.Deserialize<T>(Options);
}

/// <summary>A zero-based line and UTF-16 column, as LSP counts them.</summary>
public readonly record struct Position(int Line, int Character) : IComparable<Position>
{
    public int CompareTo(Position other) => Line != other.Line ? Line.CompareTo(other.Line) : Character.CompareTo(other.Character);
    public static bool operator <(Position a, Position b) => a.CompareTo(b) < 0;
    public static bool operator >(Position a, Position b) => a.CompareTo(b) > 0;
    public static bool operator <=(Position a, Position b) => a.CompareTo(b) <= 0;
    public static bool operator >=(Position a, Position b) => a.CompareTo(b) >= 0;
    public override string ToString() => $"{Line + 1}:{Character + 1}";
}

public readonly record struct Range(Position Start, Position End)
{
    public bool Contains(Position p) => Start <= p && p <= End;
}

public sealed record Location(string Uri, Range Range);

public enum DiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}

public sealed record Diagnostic(Range Range, DiagnosticSeverity Severity, string Message, string? Source = null, Range? FullRange = null)
{
    /// <summary>Lean reports a narrow range for the squiggle and the whole extent in fullRange.</summary>
    public Range Extent => FullRange ?? Range;
}

public enum LeanFileProgressKind
{
    Processing = 1,
    FatalError = 2,
}

public sealed record LeanFileProgressRange(Range Range, LeanFileProgressKind Kind = LeanFileProgressKind.Processing);

public sealed record PlainGoal(string Rendered, IReadOnlyList<string> Goals);

public sealed record PlainTermGoal(string Goal, Range Range);

public sealed record Hover(string Contents, Range? Range);

public sealed record CompletionItem(string Label, string? Detail, string? Documentation, int? Kind, string? InsertText, string? SortText);

public sealed record DocumentSymbol(string Name, int Kind, Range Range, Range SelectionRange, string? Detail, IReadOnlyList<DocumentSymbol> Children);

public sealed record TextEdit(Range Range, string NewText);

/// <summary>Edits to one or more files, as LSP's WorkspaceEdit: file URI to the edits in it.</summary>
public sealed record WorkspaceEdit(IReadOnlyDictionary<string, IReadOnlyList<TextEdit>> Changes)
{
    public static readonly WorkspaceEdit Empty = new(new Dictionary<string, IReadOnlyList<TextEdit>>());

    public bool IsEmpty => Changes.Values.All(e => e.Count == 0);

    /// <summary>Read either form LSP allows: <c>changes</c> (uri → edits) or <c>documentChanges</c> (versioned).</summary>
    public static WorkspaceEdit Parse(System.Text.Json.JsonElement e)
    {
        var map = new Dictionary<string, List<TextEdit>>(StringComparer.Ordinal);
        void Add(string uri, System.Text.Json.JsonElement edits)
        {
            if (!map.TryGetValue(uri, out List<TextEdit>? list))
            {
                map[uri] = list = [];
            }
            foreach (System.Text.Json.JsonElement t in edits.EnumerateArray())
            {
                list.Add(new TextEdit(t.GetProperty("range").As<Range>(), t.GetProperty("newText").GetString() ?? ""));
            }
        }
        if (e.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return Empty;
        }
        if (e.TryGetProperty("changes", out System.Text.Json.JsonElement changes) && changes.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (System.Text.Json.JsonProperty p in changes.EnumerateObject())
            {
                Add(p.Name, p.Value);
            }
        }
        if (e.TryGetProperty("documentChanges", out System.Text.Json.JsonElement docs) && docs.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (System.Text.Json.JsonElement d in docs.EnumerateArray())
            {
                if (d.TryGetProperty("textDocument", out System.Text.Json.JsonElement td) && d.TryGetProperty("edits", out System.Text.Json.JsonElement edits))
                {
                    Add(td.GetProperty("uri").GetString() ?? "", edits);
                }
            }
        }
        return new WorkspaceEdit(map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<TextEdit>)kv.Value, StringComparer.Ordinal));
    }

    /// <summary>Apply edits to a text: all positions refer to the original, so apply from the end backwards.</summary>
    public static string Apply(string text, IEnumerable<TextEdit> edits)
    {
        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lineStarts.Add(i + 1);
            }
        }
        int Offset(Position p)
        {
            if (p.Line >= lineStarts.Count)
            {
                return text.Length;
            }
            int start = lineStarts[p.Line];
            int end = p.Line + 1 < lineStarts.Count ? lineStarts[p.Line + 1] - 1 : text.Length;
            return Math.Min(start + p.Character, end);
        }
        var sb = new System.Text.StringBuilder(text);
        foreach (TextEdit e in edits.OrderByDescending(e => e.Range.Start).ThenByDescending(e => e.Range.End))
        {
            int s = Offset(e.Range.Start), en = Offset(e.Range.End);
            sb.Remove(s, Math.Max(0, en - s)).Insert(s, e.NewText);
        }
        return sb.ToString();
    }
}

/// <summary>A code action (for Lean, mostly "Try this" suggestions). The raw JSON is kept for resolving.</summary>
public sealed record CodeAction(string Title, string? Kind, WorkspaceEdit? Edit, bool IsPreferred, System.Text.Json.JsonElement Raw);

public sealed record SymbolLocation(string Name, int Kind, Location Location, string? Container);

public sealed record FoldingRange(int StartLine, int EndLine, string? Kind);
