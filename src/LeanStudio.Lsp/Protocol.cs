using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeanStudio.Lsp;

/// <summary>The JSON settings for LSP messages: camelCase names, nulls left out, case-insensitive reads.</summary>
internal static class LspJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Deserialize with <see cref="Options"/>; an undefined or null element gives <c>default</c>.</summary>
    public static T? As<T>(this JsonElement e) =>
        e.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? default : e.Deserialize<T>(Options);
}

/// <summary>A zero-based line and UTF-16 column, as LSP counts them.</summary>
/// <param name="Line">The 0-based line.</param>
/// <param name="Character">The 0-based column, in UTF-16 code units (so a character outside the BMP counts as two).</param>
public readonly record struct Position(int Line, int Character) : IComparable<Position>
{
    /// <inheritdoc/>
    public int CompareTo(Position other) => Line != other.Line ? Line.CompareTo(other.Line) : Character.CompareTo(other.Character);
    /// <summary>Whether <paramref name="a"/> comes before <paramref name="b"/> in the document.</summary>
    public static bool operator <(Position a, Position b) => a.CompareTo(b) < 0;
    /// <summary>Whether <paramref name="a"/> comes after <paramref name="b"/> in the document.</summary>
    public static bool operator >(Position a, Position b) => a.CompareTo(b) > 0;
    /// <summary>Whether <paramref name="a"/> comes before or is the same as <paramref name="b"/>.</summary>
    public static bool operator <=(Position a, Position b) => a.CompareTo(b) <= 0;
    /// <summary>Whether <paramref name="a"/> comes after or is the same as <paramref name="b"/>.</summary>
    public static bool operator >=(Position a, Position b) => a.CompareTo(b) >= 0;
    /// <summary>The position 1-based, as <c>line:column</c>, the way editors and error messages show it.</summary>
    public override string ToString() => $"{Line + 1}:{Character + 1}";
}

/// <summary>A span of text between two positions, as LSP sends it.</summary>
/// <param name="Start">Where the span begins.</param>
/// <param name="End">Where the span ends; LSP treats the end as exclusive.</param>
public readonly record struct Range(Position Start, Position End)
{
    /// <summary>Whether <paramref name="p"/> lies in the range, counting both ends (so a caret just after the text is inside).</summary>
    public bool Contains(Position p) => Start <= p && p <= End;
}

/// <summary>A range in a file, such as the target of a go-to-definition.</summary>
/// <param name="Uri">The file's <c>file://</c> URI.</param>
/// <param name="Range">The range in that file.</param>
public sealed record Location(string Uri, Range Range);

/// <summary>How serious a diagnostic is; the values are LSP's.</summary>
public enum DiagnosticSeverity
{
    /// <summary>An error: the file does not check.</summary>
    Error = 1,
    /// <summary>A warning, such as a declaration that uses <c>sorry</c> or a linter's complaint.</summary>
    Warning = 2,
    /// <summary>Information, such as the output of <c>#eval</c> or <c>#check</c>.</summary>
    Information = 3,
    /// <summary>A hint.</summary>
    Hint = 4,
}

/// <summary>A message Lean published about a file: an error, a warning, or the output of a command.</summary>
/// <param name="Range">The range to underline; for Lean, usually just the start of the offending syntax.</param>
/// <param name="Severity">How serious it is.</param>
/// <param name="Message">The message text, as Lean rendered it.</param>
/// <param name="Source">Who produced it (for Lean, usually <c>Lean 4</c>), or null when not given.</param>
/// <param name="FullRange">Lean's extension: the whole extent of the syntax the message is about, or null when not given.</param>
public sealed record Diagnostic(Range Range, DiagnosticSeverity Severity, string Message, string? Source = null, Range? FullRange = null)
{
    /// <summary>Lean reports a narrow range for the squiggle and the whole extent in fullRange.</summary>
    public Range Extent => FullRange ?? Range;
}

/// <summary>What Lean says about a range it has not finished with, in <c>$/lean/fileProgress</c>.</summary>
public enum LeanFileProgressKind
{
    /// <summary>Still being elaborated.</summary>
    Processing = 1,
    /// <summary>Lean stopped here with a fatal error and will not elaborate it.</summary>
    FatalError = 2,
}

/// <summary>A range of a file Lean is still working on, or gave up on.</summary>
/// <param name="Range">The range not yet done.</param>
/// <param name="Kind">Whether it is still being processed or failed.</param>
public sealed record LeanFileProgressRange(Range Range, LeanFileProgressKind Kind = LeanFileProgressKind.Processing);

/// <summary>The tactic goals at a position as plain text, from <c>$/lean/plainGoal</c>.</summary>
/// <param name="Rendered">All the goals as Markdown, as Lean renders them for a hover.</param>
/// <param name="Goals">Each goal as plain text (hypotheses, then <c>⊢</c> and the target); empty when there are none.</param>
public sealed record PlainGoal(string Rendered, IReadOnlyList<string> Goals);

/// <summary>The expected type at a position in a term, as plain text, from <c>$/lean/plainTermGoal</c>.</summary>
/// <param name="Goal">The expected type, with the local context.</param>
/// <param name="Range">The term it applies to.</param>
public sealed record PlainTermGoal(string Goal, Range Range);

/// <summary>A hover's text.</summary>
/// <param name="Contents">The hover text, usually Markdown (several parts are joined with blank lines).</param>
/// <param name="Range">The range the hover is about, or null when the server did not say.</param>
public sealed record Hover(string Contents, Range? Range);

/// <summary>One completion suggestion.</summary>
/// <param name="Label">What the list shows, and what is inserted when <paramref name="InsertText"/> is null.</param>
/// <param name="Detail">Usually the declaration's type, or null.</param>
/// <param name="Documentation">The docstring as Markdown, or null.</param>
/// <param name="Kind">LSP's <c>CompletionItemKind</c> number, or null when not given.</param>
/// <param name="InsertText">The text to insert (from <c>insertText</c> or the <c>textEdit</c>), or null to insert the label.</param>
/// <param name="SortText">The key the server wants the list sorted by, or null to sort by label.</param>
public sealed record CompletionItem(string Label, string? Detail, string? Documentation, int? Kind, string? InsertText, string? SortText);

/// <summary>A declaration or section in a file's outline, with the ones nested in it.</summary>
/// <param name="Name">The name shown in the outline.</param>
/// <param name="Kind">LSP's <c>SymbolKind</c> number (0 when not given).</param>
/// <param name="Range">The whole extent of the symbol, including its body.</param>
/// <param name="SelectionRange">The part to select when going to it, usually the name.</param>
/// <param name="Detail">Extra text, such as a signature, or null.</param>
/// <param name="Children">The symbols nested inside it; empty when there are none.</param>
public sealed record DocumentSymbol(string Name, int Kind, Range Range, Range SelectionRange, string? Detail, IReadOnlyList<DocumentSymbol> Children);

/// <summary>Replace a range of a file with new text.</summary>
/// <param name="Range">The range to replace, in the text before any edit; an empty range inserts.</param>
/// <param name="NewText">The replacement; empty to delete.</param>
public sealed record TextEdit(Range Range, string NewText);

/// <summary>Edits to one or more files, as LSP's WorkspaceEdit: file URI to the edits in it.</summary>
/// <param name="Changes">The edits, by file URI.</param>
public sealed record WorkspaceEdit(IReadOnlyDictionary<string, IReadOnlyList<TextEdit>> Changes)
{
    /// <summary>An edit that changes nothing.</summary>
    public static readonly WorkspaceEdit Empty = new(new Dictionary<string, IReadOnlyList<TextEdit>>());

    /// <summary>Whether there are no text edits in any file.</summary>
    public bool IsEmpty => Changes.Values.All(e => e.Count == 0);

    /// <summary>Read either form LSP allows: <c>changes</c> (uri → edits) or <c>documentChanges</c> (versioned).</summary>
    /// <remarks>Anything that is not a JSON object gives <see cref="Empty"/>. Versions in <c>documentChanges</c> are ignored.</remarks>
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
    /// <remarks>
    /// Positions past the end of a line clamp to the end of that line, and lines past the end of the text clamp to
    /// the end of the text. Only <c>\n</c> separates lines. Returns the new text; <paramref name="text"/> is unchanged.
    /// </remarks>
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
/// <param name="Title">What the menu shows.</param>
/// <param name="Kind">LSP's code action kind (such as <c>quickfix</c>), or null.</param>
/// <param name="Edit">The edit it makes, or null when the server left it to be filled in by <see cref="LeanServer.ResolveAsync"/>.</param>
/// <param name="IsPreferred">Whether the server marked it as the one to apply.</param>
/// <param name="Raw">The action as the server sent it, passed back to <c>codeAction/resolve</c>.</param>
public sealed record CodeAction(string Title, string? Kind, WorkspaceEdit? Edit, bool IsPreferred, System.Text.Json.JsonElement Raw);

/// <summary>A declaration found by a workspace symbol search.</summary>
/// <param name="Name">The declaration's name.</param>
/// <param name="Kind">LSP's <c>SymbolKind</c> number (0 when not given).</param>
/// <param name="Location">Where it is declared.</param>
/// <param name="Container">The name of what contains it (such as its namespace), or null.</param>
public sealed record SymbolLocation(string Name, int Kind, Location Location, string? Container);

/// <summary>A span of lines the editor can fold.</summary>
/// <param name="StartLine">The first line, 0-based.</param>
/// <param name="EndLine">The last line, 0-based and inclusive.</param>
/// <param name="Kind">LSP's folding kind (<c>comment</c>, <c>imports</c> or <c>region</c>), or null.</param>
public sealed record FoldingRange(int StartLine, int EndLine, string? Kind);
