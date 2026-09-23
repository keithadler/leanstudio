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
