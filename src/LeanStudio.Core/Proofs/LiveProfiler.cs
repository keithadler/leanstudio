using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeanStudio.Lsp;

namespace LeanStudio.Core.Proofs;

/// <summary>
/// Profiling as you edit: a copy of a file, with Lean's profilers switched on, kept open in the project's running
/// Lean server beside the real one and given each new version of the text. Lean re-checks only from the first
/// changed declaration down, so after the first check an update costs about what the edit does. Each update reads
/// back the cost of every declaration (from the roots of Lean's traces), the tree inside each one (expanded through
/// the server, and remembered for declarations that did not change) and Lean's profiler lines.
/// </summary>
/// <remarks>Not thread-safe: one update at a time, which <see cref="UpdateAsync"/> enforces.</remarks>
public sealed class LiveProfiler : IAsyncDisposable
{
    /// <summary>The line put after the file's header, which switches the profilers on for everything below it.</summary>
    public const string Options = "set_option trace.profiler true set_option trace.profiler.threshold 5 set_option profiler true set_option profiler.threshold 1";

    /// <summary>At most this many trace steps are fetched per declaration; a larger tree is left with its totals only.</summary>
    public const int MaxExpanded = 3000;

    private readonly LeanServer _server;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, ProfileNode> _expanded = new(StringComparer.Ordinal);
    private bool _open;

    // What the last read found, for expanding a declaration later without asking Lean to check the file again.
    private string[] _lines = [];
    private List<(int Line, int Owner, string Key, JsonElement Trace)> _roots = [];
    private List<(int, string)> _steps = [];
    private int _errors;
    private List<string> _refs = [];

    /// <summary>Profile <paramref name="sourcePath"/> in <paramref name="server"/>, which must be the project's running server.</summary>
    public LiveProfiler(LeanServer server, string sourcePath)
    {
        _server = server;
        SourcePath = sourcePath;
        Uri = Scratch.UriFor(sourcePath, "Live");
    }

    /// <summary>The file profiled.</summary>
    public string SourcePath { get; }

    /// <summary>The URI of the copy in the server (never written to disk).</summary>
    public string Uri { get; }

    /// <summary>The server the copy is open in.</summary>
    public LeanServer Server => _server;

    /// <summary>
    /// The copy of <paramref name="text"/> Lean is given: <see cref="Options"/> on a line of its own after the
    /// header (the imports), and the 0-based line it was put on. Lines from there on are one further down.
    /// </summary>
    public static (string Text, int Inserted) Instrument(string text)
    {
        var lines = text.Split('\n').ToList();
        int header = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].Trim();
            if (t.StartsWith("import ", StringComparison.Ordinal) || t is "prelude" or "module"
                || t.StartsWith("public import ", StringComparison.Ordinal) || t.StartsWith("meta import ", StringComparison.Ordinal)
                || t.StartsWith("private import ", StringComparison.Ordinal))
            {
                header = i;
            }
            else if (t.Length > 0 && !t.StartsWith("--", StringComparison.Ordinal) && !t.StartsWith("/-", StringComparison.Ordinal) && header >= 0)
            {
                break; // the first command after the imports
            }
        }
        lines.Insert(header + 1, Options);
        return (string.Join('\n', lines), header + 1);
    }

    /// <summary>
    /// Give Lean <paramref name="text"/> (the file as it is now), wait until it has checked it, and read back the
    /// profile. The first call opens the copy; later ones send it as an edit.
    /// </summary>
    /// <param name="text">The file's text.</param>
    /// <param name="focusLine">
    /// A 0-based line in the declaration being edited, whose tree is expanded; null expands none (every
    /// declaration still gets its cost).
    /// </param>
    /// <param name="ct">Cancels the wait (the edit has been sent by then; the next update supersedes it).</param>
    public async Task<ProfileReport> UpdateAsync(string text, int? focusLine = null, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            (string instrumented, int inserted) = Instrument(text);
            if (!_open || !_server.IsOpen(Uri))
            {
                if (_server.IsOpen(Uri))
                {
                    await _server.CloseAsync(Uri).ConfigureAwait(false);
                }
                await _server.OpenAsync(Uri, instrumented).ConfigureAwait(false);
                _open = true;
            }
            else
            {
                await _server.ChangeAsync(Uri, instrumented).ConfigureAwait(false);
            }
            await _server.WaitForElaborationAsync(Uri, ct).ConfigureAwait(false);
            await SettleAsync(ct).ConfigureAwait(false);
            return await ReadAsync(text.Split('\n'), inserted, focusLine, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Lean's last diagnostics can trail its "finished" report: wait until they have been still for a moment.</summary>
    private async Task SettleAsync(CancellationToken ct)
    {
        IReadOnlyList<Diagnostic> last = _server.DiagnosticsOf(Uri);
        for (int quiet = 0, i = 0; quiet < 3 && i < 40; i++)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
            IReadOnlyList<Diagnostic> now = _server.DiagnosticsOf(Uri);
            quiet = ReferenceEquals(now, last) ? quiet + 1 : 0;
            last = now;
        }
    }

    private async Task<ProfileReport> ReadAsync(string[] lines, int inserted, int? focusLine, CancellationToken ct)
    {
        int Original(int line) => line > inserted ? line - 1 : line;
        // The references of the last read are for diagnostics Lean has replaced.
        if (_refs.Count > 0)
        {
            await _server.ReleaseAsync(Uri, _refs).ConfigureAwait(false);
            _refs = [];
        }
        var steps = new List<(int, string)>();
        int errors = 0;
        foreach (Diagnostic d in _server.DiagnosticsOf(Uri))
        {
            if (d.Severity == DiagnosticSeverity.Error)
            {
                errors++;
            }
            else if (d.Severity == DiagnosticSeverity.Information && d.Message != "(trace)")
            {
                steps.Add((Original(d.Range.Start.Line), d.Message));
            }
        }
        JsonElement diags = await _server.RpcCallAsync(Uri, new Position(0, 0), "Lean.Widget.getInteractiveDiagnostics",
            new JsonObject { ["lineRange"] = new JsonObject { ["start"] = 0, ["end"] = lines.Length + 2 } }, ct).ConfigureAwait(false);
        var roots = new List<(int Line, int Owner, string Key, JsonElement Trace)>();
        if (diags.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement d in diags.EnumerateArray())
            {
                if (d.TryGetProperty("message", out JsonElement m) && FindTrace(m) is JsonElement t
                    && d.TryGetProperty("range", out JsonElement r) && r.TryGetProperty("start", out JsonElement s) && s.TryGetProperty("line", out JsonElement l)
                    && Original(l.GetInt32()) is int line && line >= 0 && line <= lines.Length)
                {
                    // A declaration that did not change keeps its trace (wherever it has moved to): its root reads
                    // the same, and so does its text.
                    int owner = Profiler.OwnerOf(lines, line);
                    string key = string.Join('\n', lines.Skip(owner).Take(Profiler.NextTopLevel(lines, owner) - owner)) + "\u0001" + Head(t);
                    roots.Add((line, owner, key, t.Clone()));
                }
            }
        }
        (_lines, _roots, _steps, _errors) = (lines, roots, steps, errors);
        var keys = roots.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        foreach (string stale in _expanded.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            _expanded.Remove(stale);
        }
        if (focusLine is int f && f >= 0 && f < lines.Length)
        {
            await ExpandOwnerAsync(Profiler.OwnerOf(lines, f), ct).ConfigureAwait(false);
        }
        return Report();
    }

    /// <summary>
    /// Fetch the trace of the declaration at the 0-based line <paramref name="line"/> (as of the last update), if
    /// it is not fetched yet, and return the profile with it.
    /// </summary>
    public async Task<ProfileReport> ExpandAsync(int line, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ExpandOwnerAsync(Profiler.OwnerOf(_lines, line), ct).ConfigureAwait(false);
            return Report();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ExpandOwnerAsync(int owner, CancellationToken ct)
    {
        foreach ((int _, int o, string key, JsonElement trace) in _roots)
        {
            if (o == owner && !_expanded.ContainsKey(key))
            {
                var budget = new Budget(MaxExpanded);
                (ProfileNode node, bool complete) = await ExpandAsync(trace, _refs, budget, ct).ConfigureAwait(false);
                if (complete)
                {
                    _expanded[key] = node;
                }
            }
        }
    }

    /// <summary>The profile from the last read: each declaration's roots, expanded where they have been.</summary>
    private ProfileReport Report()
    {
        var trees = _roots.Select(r => (r.Line, _expanded.TryGetValue(r.Key, out ProfileNode? n) ? n : HeadOnly(r.Trace))).ToList();
        var partial = _roots.Where(r => !_expanded.ContainsKey(r.Key)).Select(r => r.Owner).ToHashSet();
        IReadOnlyList<DeclarationTiming> declarations = Profiler.Build(trees, _steps, [], _lines, ProfileUnit.Seconds)
            .Select(d => partial.Contains(d.Line) ? d with { TraceComplete = false } : d).ToList();
        var categories = declarations.SelectMany(d => d.Steps).GroupBy(s => s.Category)
            .Select(g => new ProfileCategory(g.Key, g.Sum(s => s.Seconds))).OrderByDescending(c => c.Seconds).ToList();
        return new ProfileReport(declarations, ProfileUnit.Seconds) { Path = SourcePath, Errors = _errors, Categories = categories, Live = true };
    }

    /// <summary>A trace's root on its own, without the steps inside it: its cost, before they are fetched.</summary>
    private static ProfileNode HeadOnly(JsonElement trace) =>
        Profiler.ParseTree(Head(trace)) ?? new ProfileNode("", Head(trace), 0, false, []);

    /// <summary>How many more trace steps may be fetched.</summary>
    private sealed class Budget(int left)
    {
        public bool Take() => left-- > 0;
    }

    /// <summary>The trace node in a message, if the message is a trace: <c>{"tag": [{"trace": …}, …]}</c>.</summary>
    private static JsonElement? FindTrace(JsonElement m)
    {
        if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("tag", out JsonElement tag) && tag.ValueKind == JsonValueKind.Array
            && tag.GetArrayLength() > 0 && tag[0].ValueKind == JsonValueKind.Object && tag[0].TryGetProperty("trace", out JsonElement t))
        {
            return t;
        }
        return null;
    }

    /// <summary>A trace node's first line, in the form the command line prints: <c>[Elab.step] [0.39] ✅️ omega</c>.</summary>
    private static string Head(JsonElement trace)
    {
        string cls = trace.TryGetProperty("cls", out JsonElement c) ? c.GetString() ?? "" : "";
        var sb = new StringBuilder();
        if (trace.TryGetProperty("msg", out JsonElement msg))
        {
            Flatten(msg, sb);
        }
        return $"[{cls}] {sb}";
    }

    /// <summary>The plain text of a piece of Lean's tagged text, leaving out any traces nested in it.</summary>
    private static void Flatten(JsonElement t, StringBuilder sb)
    {
        switch (t.ValueKind)
        {
            case JsonValueKind.Object:
                if (t.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
                {
                    sb.Append(text.GetString());
                }
                else if (t.TryGetProperty("append", out JsonElement parts))
                {
                    foreach (JsonElement p in parts.EnumerateArray())
                    {
                        Flatten(p, sb);
                    }
                }
                else if (t.TryGetProperty("tag", out JsonElement tag) && tag.ValueKind == JsonValueKind.Array && tag.GetArrayLength() == 2)
                {
                    if (tag[0].ValueKind == JsonValueKind.Object && tag[0].TryGetProperty("expr", out JsonElement e))
                    {
                        Flatten(e, sb);
                    }
                    else if (tag[0].ValueKind == JsonValueKind.Object && tag[0].TryGetProperty("trace", out _))
                    {
                        return;
                    }
                    Flatten(tag[1], sb);
                }
                break;
            case JsonValueKind.Array:
                foreach (JsonElement p in t.EnumerateArray())
                {
                    Flatten(p, sb);
                }
                break;
        }
    }

    /// <summary>
    /// A trace node as a <see cref="ProfileNode"/>, its children fetched from the server while
    /// <paramref name="budget"/> allows. Returns whether the whole tree was fetched.
    /// </summary>
    private async Task<(ProfileNode Node, bool Complete)> ExpandAsync(JsonElement trace, List<string> refs, Budget budget, CancellationToken ct)
    {
        ProfileNode head = HeadOnly(trace);
        var children = new List<ProfileNode>();
        bool complete = true;
        IEnumerable<JsonElement> kids = [];
        if (trace.TryGetProperty("children", out JsonElement ch))
        {
            if (ch.TryGetProperty("strict", out JsonElement strict) && strict.ValueKind == JsonValueKind.Array)
            {
                kids = strict.EnumerateArray().ToList();
            }
            else if (ch.TryGetProperty("lazy", out JsonElement lazy))
            {
                if (lazy.TryGetProperty("p", out JsonElement p) && p.ValueKind == JsonValueKind.String)
                {
                    refs.Add(p.GetString()!);
                }
                if (budget.Take())
                {
                    JsonElement got = await _server.RpcCallAsync(Uri, new Position(0, 0), "Lean.Widget.lazyTraceChildrenToInteractive",
                        JsonNode.Parse(lazy.GetRawText()), ct).ConfigureAwait(false);
                    kids = got.ValueKind == JsonValueKind.Array ? got.EnumerateArray().ToList() : [];
                }
                else
                {
                    complete = false;
                }
            }
        }
        foreach (JsonElement k in kids)
        {
            if (FindTrace(k) is JsonElement t)
            {
                if (!budget.Take())
                {
                    complete = false;
                    break;
                }
                (ProfileNode c, bool done) = await ExpandAsync(t, refs, budget, ct).ConfigureAwait(false);
                children.Add(c);
                complete &= done;
            }
        }
        return (head with { Children = children }, complete);
    }

    /// <summary>Close the copy in the server (if it is still running).</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_open && _server.State == LeanServerState.Running && _server.IsOpen(Uri))
            {
                try
                {
                    await _server.CloseAsync(Uri).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or JsonRpcException or ObjectDisposedException)
                {
                    // The server stopped as the copy was being closed: nothing is left to close.
                }
            }
            _open = false;
            _expanded.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }
}
