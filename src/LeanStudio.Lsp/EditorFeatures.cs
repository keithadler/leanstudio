using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeanStudio.Lsp;

/// <summary>A span of text Lean classified (a variable, a function, a type, a sorry…), for semantic highlighting.</summary>
/// <param name="Line">0-based line.</param>
/// <param name="Start">0-based UTF-16 column where it starts.</param>
/// <param name="Length">Length in UTF-16 code units.</param>
/// <param name="Type">The token type from Lean's legend, e.g. <c>variable</c>, <c>function</c>, <c>leanSorryLike</c>.</param>
/// <param name="Modifiers">The modifiers from Lean's legend, e.g. <c>declaration</c>, <c>deprecated</c>.</param>
public sealed record SemanticToken(int Line, int Start, int Length, string Type, IReadOnlyList<string> Modifiers);

/// <summary>A hint Lean shows inside the text without it being there, such as an automatically bound implicit.</summary>
/// <param name="Position">Where it goes.</param>
/// <param name="Label">What it says.</param>
/// <param name="PaddingLeft">Whether to leave a space before it.</param>
/// <param name="PaddingRight">Whether to leave a space after it.</param>
/// <param name="Edits">What inserting it into the text for real would change, when Lean offers that.</param>
public sealed record InlayHint(Position Position, string Label, bool PaddingLeft, bool PaddingRight, IReadOnlyList<TextEdit> Edits);

/// <summary>A declaration in a call hierarchy, as Lean describes it; <see cref="Raw"/> is sent back to ask for its calls.</summary>
/// <param name="Name">The declaration's name.</param>
/// <param name="Detail">Extra detail Lean gives, such as the module.</param>
/// <param name="Uri">The file it is in.</param>
/// <param name="Range">Its whole extent.</param>
/// <param name="SelectionRange">The extent of its name.</param>
/// <param name="Raw">The item exactly as Lean sent it.</param>
public sealed record CallHierarchyItem(string Name, string? Detail, string Uri, Range Range, Range SelectionRange, JsonElement Raw);

/// <summary>One edge of a call hierarchy: the other declaration, and where in the caller the use is.</summary>
/// <param name="Item">The caller (for incoming calls) or the callee (for outgoing calls).</param>
/// <param name="FromRanges">Where the uses are, in the caller.</param>
public sealed record CallSite(CallHierarchyItem Item, IReadOnlyList<Range> FromRanges);

/// <summary>A node of a trace tree (from <c>set_option trace.… true</c>), with its children or a way to fetch them.</summary>
/// <param name="Class">The trace class, e.g. <c>Meta.synthInstance</c>.</param>
/// <param name="Text">The node's message as plain text.</param>
/// <param name="Collapsed">Whether Lean suggests showing it collapsed.</param>
/// <param name="Children">The children, when Lean sent them with the node.</param>
/// <param name="LazyChildren">A reference to fetch the children with <see cref="LeanServer.TraceChildrenAsync"/>, when it did not.</param>
public sealed record TraceNode(string Class, string Text, bool Collapsed, IReadOnlyList<TraceNode> Children, string? LazyChildren)
{
    /// <summary>Whether the node has children, sent or still to fetch.</summary>
    public bool HasChildren => Children.Count > 0 || LazyChildren is not null;
}

/// <summary>A message as the infoview shows it: its text, and the trace trees inside it.</summary>
/// <param name="Range">Where it is.</param>
/// <param name="Severity">Error, warning or information.</param>
/// <param name="Text">The message as plain text, without its traces.</param>
/// <param name="Traces">The trace trees in it, outermost first.</param>
public sealed record InteractiveMessage(Range Range, DiagnosticSeverity Severity, string Text, IReadOnlyList<TraceNode> Traces);

public sealed partial class LeanServer
{
    /// <summary>
    /// Lean's classification of every token in a file (<c>textDocument/semanticTokens/full</c>), decoded with the
    /// legend the server announced. Empty if the server has no semantic tokens.
    /// </summary>
    public async Task<IReadOnlyList<SemanticToken>> SemanticTokensAsync(string uri, CancellationToken ct = default)
    {
        if (ServerCapabilities.ValueKind != JsonValueKind.Object
            || !ServerCapabilities.TryGetProperty("semanticTokensProvider", out JsonElement provider)
            || !provider.TryGetProperty("legend", out JsonElement legend))
        {
            return [];
        }
        string[] types = legend.GetProperty("tokenTypes").EnumerateArray().Select(t => t.GetString() ?? "").ToArray();
        string[] modifiers = legend.GetProperty("tokenModifiers").EnumerateArray().Select(t => t.GetString() ?? "").ToArray();
        JsonElement r = await Rpc.RequestAsync("textDocument/semanticTokens/full", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
        }, ct).ConfigureAwait(false);
        return r.ValueKind == JsonValueKind.Object && r.TryGetProperty("data", out JsonElement data)
            ? DecodeSemanticTokens(data.EnumerateArray().Select(x => x.GetInt32()).ToArray(), types, modifiers)
            : [];
    }

    /// <summary>Decode LSP's relative five-number encoding of semantic tokens.</summary>
    public static IReadOnlyList<SemanticToken> DecodeSemanticTokens(int[] data, IReadOnlyList<string> types, IReadOnlyList<string> modifiers)
    {
        var list = new List<SemanticToken>(data.Length / 5);
        int line = 0, start = 0;
        for (int i = 0; i + 4 < data.Length; i += 5)
        {
            line += data[i];
            start = data[i] == 0 ? start + data[i + 1] : data[i + 1];
            string type = data[i + 3] >= 0 && data[i + 3] < types.Count ? types[data[i + 3]] : "";
            var mods = new List<string>();
            for (int b = 0; b < modifiers.Count; b++)
            {
                if ((data[i + 4] & (1 << b)) != 0)
                {
                    mods.Add(modifiers[b]);
                }
            }
            list.Add(new SemanticToken(line, start, data[i + 2], type, mods));
        }
        return list;
    }

    /// <summary>The inlay hints in a range of a file (<c>textDocument/inlayHint</c>).</summary>
    public async Task<IReadOnlyList<InlayHint>> InlayHintsAsync(string uri, Range range, CancellationToken ct = default)
    {
        JsonElement r = await Rpc.RequestAsync("textDocument/inlayHint", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["range"] = JsonSerializer.SerializeToNode(range, LspJson.Options),
        }, ct).ConfigureAwait(false);
        var list = new List<InlayHint>();
        if (r.ValueKind != JsonValueKind.Array)
        {
            return list;
        }
        foreach (JsonElement h in r.EnumerateArray())
        {
            string label = h.GetProperty("label") is { ValueKind: JsonValueKind.String } s
                ? s.GetString() ?? ""
                : string.Concat(h.GetProperty("label").EnumerateArray().Select(p => p.TryGetProperty("value", out JsonElement v) ? v.GetString() : ""));
            var edits = h.TryGetProperty("textEdits", out JsonElement te) && te.ValueKind == JsonValueKind.Array
                ? te.EnumerateArray().Select(e => new TextEdit(e.GetProperty("range").As<Range>(), e.GetProperty("newText").GetString() ?? "")).ToList()
                : [];
            list.Add(new InlayHint(h.GetProperty("position").As<Position>(), label,
                h.TryGetProperty("paddingLeft", out JsonElement pl) && pl.ValueKind == JsonValueKind.True,
                h.TryGetProperty("paddingRight", out JsonElement pr) && pr.ValueKind == JsonValueKind.True,
                edits));
        }
        return list;
    }

    /// <summary>Every place in the file where the symbol at a position occurs (<c>textDocument/documentHighlight</c>).</summary>
    public async Task<IReadOnlyList<Range>> DocumentHighlightsAsync(string uri, Position pos, CancellationToken ct = default)
    {
        JsonElement r = await Rpc.RequestAsync("textDocument/documentHighlight", At(uri, pos), ct).ConfigureAwait(false);
        return r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(h => h.GetProperty("range").As<Range>()).ToList()
            : [];
    }

    /// <summary>The declaration at a position, as the root of a call hierarchy (<c>textDocument/prepareCallHierarchy</c>).</summary>
    public async Task<IReadOnlyList<CallHierarchyItem>> PrepareCallHierarchyAsync(string uri, Position pos, CancellationToken ct = default)
    {
        JsonElement r = await Rpc.RequestAsync("textDocument/prepareCallHierarchy", At(uri, pos), ct).ConfigureAwait(false);
        return r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().Select(ParseCallItem).ToList() : [];
    }

    /// <summary>The declarations that use <paramref name="item"/>, and where (<c>callHierarchy/incomingCalls</c>).</summary>
    public Task<IReadOnlyList<CallSite>> IncomingCallsAsync(CallHierarchyItem item, CancellationToken ct = default) => CallsAsync("callHierarchy/incomingCalls", "from", item, ct);

    /// <summary>The declarations <paramref name="item"/> uses (<c>callHierarchy/outgoingCalls</c>).</summary>
    public Task<IReadOnlyList<CallSite>> OutgoingCallsAsync(CallHierarchyItem item, CancellationToken ct = default) => CallsAsync("callHierarchy/outgoingCalls", "to", item, ct);

    private async Task<IReadOnlyList<CallSite>> CallsAsync(string method, string side, CallHierarchyItem item, CancellationToken ct)
    {
        JsonElement r = await Rpc.RequestAsync(method, new JsonObject { ["item"] = JsonNode.Parse(item.Raw.GetRawText()) }, ct).ConfigureAwait(false);
        if (r.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return r.EnumerateArray().Select(c => new CallSite(
            ParseCallItem(c.GetProperty(side)),
            c.TryGetProperty("fromRanges", out JsonElement fr) ? fr.EnumerateArray().Select(x => x.As<Range>()).ToList() : [])).ToList();
    }

    private static CallHierarchyItem ParseCallItem(JsonElement e) => new(
        e.GetProperty("name").GetString() ?? "",
        e.TryGetProperty("detail", out JsonElement d) ? d.GetString() : null,
        e.GetProperty("uri").GetString() ?? "",
        e.GetProperty("range").As<Range>(),
        e.GetProperty("selectionRange").As<Range>(),
        e.Clone());

    // ---- messages with their traces ----

    /// <summary>
    /// Lean's interactive messages on lines <paramref name="startLine"/> to <paramref name="endLine"/> (0-based,
    /// end exclusive), with their trace trees (<c>Lean.Widget.getInteractiveDiagnostics</c>).
    /// </summary>
    public async Task<IReadOnlyList<InteractiveMessage>> InteractiveMessagesAsync(string uri, int startLine, int endLine, CancellationToken ct = default)
    {
        JsonElement r = await RpcCallAsync(uri, new Position(startLine, 0), "Lean.Widget.getInteractiveDiagnostics",
            new JsonObject { ["lineRange"] = new JsonObject { ["start"] = startLine, ["end"] = endLine } }, ct).ConfigureAwait(false);
        var list = new List<InteractiveMessage>();
        if (r.ValueKind != JsonValueKind.Array)
        {
            return list;
        }
        foreach (JsonElement d in r.EnumerateArray())
        {
            var traces = new List<TraceNode>();
            var sb = new StringBuilder();
            WalkMessage(d.GetProperty("message"), sb, traces);
            list.Add(new InteractiveMessage(
                d.GetProperty("range").As<Range>(),
                d.TryGetProperty("severity", out JsonElement s) && s.ValueKind == JsonValueKind.Number ? (DiagnosticSeverity)s.GetInt32() : DiagnosticSeverity.Information,
                sb.ToString().Trim(),
                traces));
        }
        return list;
    }

    /// <summary>The children of a trace node whose children Lean sends on request.</summary>
    public async Task<IReadOnlyList<TraceNode>> TraceChildrenAsync(string uri, Position pos, string lazyChildren, CancellationToken ct = default)
    {
        JsonElement r = await RpcCallAsync(uri, pos, "Lean.Widget.lazyTraceChildrenToInteractive", new JsonObject { ["p"] = lazyChildren }, ct).ConfigureAwait(false);
        var list = new List<TraceNode>();
        if (r.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in r.EnumerateArray())
            {
                var sb = new StringBuilder();
                WalkMessage(child, sb, list);
            }
        }
        return list;
    }

    /// <summary>Render a <c>TaggedText MsgEmbed</c> as text, collecting the traces in it rather than rendering them.</summary>
    internal static void WalkMessage(JsonElement e, StringBuilder sb, List<TraceNode> traces)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        if (e.TryGetProperty("text", out JsonElement t))
        {
            sb.Append(t.GetString());
        }
        else if (e.TryGetProperty("append", out JsonElement a) && a.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in a.EnumerateArray())
            {
                WalkMessage(child, sb, traces);
            }
        }
        else if (e.TryGetProperty("tag", out JsonElement tag) && tag.ValueKind == JsonValueKind.Array && tag.GetArrayLength() == 2)
        {
            JsonElement embed = tag[0];
            if (embed.TryGetProperty("expr", out JsonElement expr))
            {
                sb.Append(TaggedString.Parse(expr).Text);
            }
            else if (embed.TryGetProperty("goal", out JsonElement goal))
            {
                InteractiveGoals g = InteractiveGoals.Parse(JsonDocument.Parse("{\"goals\":[" + goal.GetRawText() + "]}").RootElement);
                sb.Append(string.Join("\n", g.Goals.Select(x => x.Render())));
            }
            else if (embed.TryGetProperty("trace", out JsonElement trace))
            {
                traces.Add(ParseTrace(trace));
            }
            else if (embed.TryGetProperty("widget", out JsonElement widget) && widget.TryGetProperty("alt", out JsonElement alt))
            {
                WalkMessage(alt, sb, traces);
            }
            WalkMessage(tag[1], sb, traces);
        }
    }

    private static TraceNode ParseTrace(JsonElement t)
    {
        var sb = new StringBuilder();
        var nested = new List<TraceNode>();
        if (t.TryGetProperty("msg", out JsonElement msg))
        {
            WalkMessage(msg, sb, nested);
        }
        var children = new List<TraceNode>(nested);
        string? lazy = null;
        if (t.TryGetProperty("children", out JsonElement ch))
        {
            if (ch.TryGetProperty("strict", out JsonElement strict) && strict.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement c in strict.EnumerateArray())
                {
                    var csb = new StringBuilder();
                    WalkMessage(c, csb, children);
                }
            }
            else if (ch.TryGetProperty("lazy", out JsonElement lz) && lz.TryGetProperty("p", out JsonElement p))
            {
                lazy = p.GetString();
            }
        }
        return new TraceNode(
            t.TryGetProperty("cls", out JsonElement cls) ? cls.GetString() ?? "" : "",
            sb.ToString().Replace('\n', ' ').Trim(),
            t.TryGetProperty("collapsed", out JsonElement col) && col.ValueKind == JsonValueKind.True,
            children,
            lazy);
    }
}
