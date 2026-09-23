using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeanStudio.Lsp;

/// <summary>How to start a Lean language server: the command, its arguments, and the directory it runs in.</summary>
public sealed record LeanServerCommand(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    public override string ToString() => FileName + " " + string.Join(' ', Arguments);
}

public enum LeanServerState
{
    Stopped,
    Starting,
    Running,
    Crashed,
}

/// <summary>
/// One running Lean language server (<c>lake serve</c> or <c>lean --server</c>) and the documents open in it.
/// Everything the editor needs goes through here: document sync, diagnostics, file progress, hover, definitions,
/// completion, and the goal queries, including the interactive goals that carry Lean's own before/after diff of
/// every tactic.
/// </summary>
public sealed class LeanServer : IAsyncDisposable
{
    /// <summary>Lean's RPC error for a session the server has forgotten (it restarted the file worker).</summary>
    public const int RpcNeedsReconnect = -32900;
    /// <summary>The file changed under the request; the answer would have been about text that no longer exists.</summary>
    public const int ContentModified = -32801;

    private readonly LeanServerCommand _command;
    private Process? _process;
    private JsonRpcConnection? _rpc;
    private readonly ConcurrentDictionary<string, int> _versions = new();
    private readonly ConcurrentDictionary<string, Task<string>> _sessions = new();
    private readonly ConcurrentDictionary<string, (int Version, IReadOnlyList<Diagnostic> Diagnostics)> _diagnostics = new();
    private readonly ConcurrentDictionary<string, int> _elaborated = new();
    private readonly object _waitLock = new();
    private readonly List<(string Uri, int Version, TaskCompletionSource Done)> _waiters = [];
    private Timer? _keepAlive;
    private bool _disposed;

    public LeanServer(LeanServerCommand command) => _command = command;

    public LeanServerCommand Command => _command;
    public LeanServerState State { get; private set; } = LeanServerState.Stopped;
    public JsonElement ServerCapabilities { get; private set; }
    public string? ServerVersion { get; private set; }

    public event Action<LeanServerState>? StateChanged;
    public event Action<string, IReadOnlyList<Diagnostic>>? DiagnosticsPublished;
    public event Action<string, IReadOnlyList<LeanFileProgressRange>>? FileProgress;
    /// <summary>A line of the server's stderr, or a message it logged.</summary>
    public event Action<string>? Log;
    /// <summary>The server asked the client to re-request semantic tokens and inlay hints: its view of the file moved on.</summary>
    public event Action? RefreshRequested;

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (State is LeanServerState.Running or LeanServerState.Starting)
        {
            return;
        }
        SetState(LeanServerState.Starting);
        var psi = new ProcessStartInfo(_command.FileName)
        {
            WorkingDirectory = _command.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in _command.Arguments)
        {
            psi.ArgumentList.Add(a);
        }
        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {_command}");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetState(LeanServerState.Crashed);
            Log?.Invoke($"could not start {_command}: {e.Message}");
            throw;
        }
        _process = p;
        p.EnableRaisingEvents = true;
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                Log?.Invoke(e.Data);
            }
        };
        p.BeginErrorReadLine();
        p.Exited += (_, _) =>
        {
            if (!_disposed && State != LeanServerState.Stopped)
            {
                Log?.Invoke($"the Lean server exited (code {SafeExitCode(p)})");
                SetState(LeanServerState.Crashed);
            }
        };

        _rpc = new JsonRpcConnection(p.StandardOutput.BaseStream, p.StandardInput.BaseStream);
        _rpc.NotificationReceived += OnNotification;
        _rpc.RequestHandler = OnServerRequest;
        _rpc.Start();

        string rootUri = new Uri(Path.GetFullPath(_command.WorkingDirectory) + Path.DirectorySeparatorChar).AbsoluteUri;
        var init = new JsonObject
        {
            ["processId"] = Environment.ProcessId,
            ["rootUri"] = rootUri,
            ["clientInfo"] = new JsonObject { ["name"] = "Lean Studio" },
            ["capabilities"] = new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["hover"] = new JsonObject { ["contentFormat"] = new JsonArray("markdown", "plaintext") },
                    ["completion"] = new JsonObject
                    {
                        ["completionItem"] = new JsonObject { ["snippetSupport"] = false },
                    },
                    ["publishDiagnostics"] = new JsonObject { ["relatedInformation"] = true },
                    ["documentSymbol"] = new JsonObject { ["hierarchicalDocumentSymbolSupport"] = true },
                },
                ["window"] = new JsonObject { ["workDoneProgress"] = false },
            },
            ["initializationOptions"] = new JsonObject { ["hasWidgets"] = true },
        };
        JsonElement result = await _rpc.RequestAsync("initialize", init, ct).ConfigureAwait(false);
        if (result.ValueKind == JsonValueKind.Object)
        {
            ServerCapabilities = result.TryGetProperty("capabilities", out JsonElement caps) ? caps.Clone() : default;
            if (result.TryGetProperty("serverInfo", out JsonElement info) && info.TryGetProperty("version", out JsonElement v))
            {
                ServerVersion = v.GetString();
            }
        }
        await _rpc.NotifyAsync("initialized", new JsonObject()).ConfigureAwait(false);
        _keepAlive = new Timer(_ => _ = KeepAliveAsync(), null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        SetState(LeanServerState.Running);
    }

    private static string SafeExitCode(Process p)
    {
        try
        {
            return p.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
    }

    private void SetState(LeanServerState s)
    {
        State = s;
        StateChanged?.Invoke(s);
    }

    private JsonRpcConnection Rpc => _rpc ?? throw new InvalidOperationException("the Lean server is not running");

    private void OnNotification(string method, JsonElement p)
    {
        switch (method)
        {
            case "textDocument/publishDiagnostics":
            {
                string uri = p.GetProperty("uri").GetString() ?? "";
                var list = p.GetProperty("diagnostics").As<List<Diagnostic>>() ?? [];
                int version = p.TryGetProperty("version", out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
                _diagnostics[uri] = (version, list);
                DiagnosticsPublished?.Invoke(uri, list);
                break;
            }
            case "$/lean/fileProgress":
            {
                JsonElement doc = p.GetProperty("textDocument");
                string uri = doc.GetProperty("uri").GetString() ?? "";
                var list = p.GetProperty("processing").As<List<LeanFileProgressRange>>() ?? [];
                // Done means nothing left to elaborate: either no ranges, or only the part Lean gave up on.
                if (list.All(r => r.Kind == LeanFileProgressKind.FatalError))
                {
                    int version = doc.TryGetProperty("version", out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
                    _elaborated.AddOrUpdate(uri, version, (_, old) => Math.Max(old, version));
                    ReleaseWaiters(uri);
                }
                FileProgress?.Invoke(uri, list);
                break;
            }
            case "window/logMessage":
            case "window/showMessage":
                Log?.Invoke(p.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "" : "");
                break;
        }
    }

    private Task<JsonNode?> OnServerRequest(string method, JsonElement p)
    {
        if (method is "workspace/semanticTokens/refresh" or "workspace/inlayHint/refresh")
        {
            RefreshRequested?.Invoke();
        }
        return Task.FromResult<JsonNode?>(null);
    }

    // ---- documents ----

    public static string UriOf(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    public static string PathOf(string uri) => new Uri(uri).LocalPath;

    public Task OpenAsync(string uri, string text)
    {
        _versions[uri] = 1;
        _elaborated.TryRemove(uri, out _);
        return Rpc.NotifyAsync("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"] = uri,
                ["languageId"] = "lean4",
                ["version"] = 1,
                ["text"] = text,
            },
        });
    }

    /// <summary>Send the whole new text. Lean re-elaborates from the first changed command either way.</summary>
    public Task ChangeAsync(string uri, string text)
    {
        int version = _versions.AddOrUpdate(uri, 1, (_, v) => v + 1);
        _sessions.TryRemove(uri, out _);
        return Rpc.NotifyAsync("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text }),
        });
    }

    public Task SaveAsync(string uri, string text) =>
        Rpc.NotifyAsync("textDocument/didSave", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["text"] = text,
        });

    public Task CloseAsync(string uri)
    {
        _versions.TryRemove(uri, out _);
        _sessions.TryRemove(uri, out _);
        _elaborated.TryRemove(uri, out _);
        _diagnostics.TryRemove(uri, out _);
        return Rpc.NotifyAsync("textDocument/didClose", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
        });
    }

    /// <summary>Ask the server to re-elaborate a file from scratch, picking up rebuilt imports.</summary>
    public Task RefreshDependenciesAsync(string uri) =>
        Rpc.NotifyAsync("$/lean/refreshFileDependencies", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = _versions.GetValueOrDefault(uri, 1) },
        });

    public bool IsOpen(string uri) => _versions.ContainsKey(uri);

    /// <summary>The version of the text Lean was last sent for a file.</summary>
    public int VersionOf(string uri) => _versions.GetValueOrDefault(uri);

    /// <summary>The newest diagnostics Lean published for a file.</summary>
    public IReadOnlyList<Diagnostic> DiagnosticsOf(string uri) =>
        _diagnostics.TryGetValue(uri, out var d) ? d.Diagnostics : [];

    /// <summary>
    /// Wait until Lean has finished elaborating the text most recently sent for a file, and its diagnostics for
    /// that text have arrived. What a caller that edits and then asks "did it work?" needs: an answer about the
    /// text it sent, not the one before.
    /// </summary>
    public async Task WaitForElaborationAsync(string uri, CancellationToken ct = default)
    {
        int version = VersionOf(uri);
        TaskCompletionSource? done = null;
        lock (_waitLock)
        {
            if (_elaborated.GetValueOrDefault(uri, -1) < version)
            {
                done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((uri, version, done));
            }
        }
        if (done is not null)
        {
            await done.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        // Lean publishes the final diagnostics around the same moment it reports progress done; give them a moment.
        for (int i = 0; i < 40 && (!_diagnostics.TryGetValue(uri, out var d) || d.Version < version); i++)
        {
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        // It publishes as it goes, so a batch for this version can arrive before the last one: wait until they
        // have been still for a moment.
        _diagnostics.TryGetValue(uri, out var last);
        for (int quiet = 0, i = 0; quiet < 3 && i < 40; i++)
        {
            await Task.Delay(50, ct).ConfigureAwait(false);
            _diagnostics.TryGetValue(uri, out var now);
            quiet = ReferenceEquals(now.Diagnostics, last.Diagnostics) ? quiet + 1 : 0;
            last = now;
        }
    }

    private void ReleaseWaiters(string uri)
    {
        int done = _elaborated.GetValueOrDefault(uri, -1);
        lock (_waitLock)
        {
            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                if (_waiters[i].Uri == uri && _waiters[i].Version <= done)
                {
                    _waiters[i].Done.TrySetResult();
                    _waiters.RemoveAt(i);
                }
            }
        }
    }

    private static JsonObject At(string uri, Position pos) => new()
    {
        ["textDocument"] = new JsonObject { ["uri"] = uri },
        ["position"] = new JsonObject { ["line"] = pos.Line, ["character"] = pos.Character },
    };

    // ---- queries ----

    public async Task<PlainGoal?> PlainGoalAsync(string uri, Position pos, CancellationToken ct = default)
    {
        JsonElement r = await Rpc.RequestAsync("$/lean/plainGoal", At(uri, pos), ct).ConfigureAwait(false);
        if (r.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return new PlainGoal(
            r.TryGetProperty("rendered", out JsonElement rendered) ? rendered.GetString() ?? "" : "",
            r.TryGetProperty("goals", out JsonElement goals) ? goals.EnumerateArray().Select(g => g.GetString() ?? "").ToList() : []);
    }

    public async Task<PlainTermGoal?> PlainTermGoalAsync(string uri, Position pos, CancellationToken ct = default)
    {
        JsonElement r = await Rpc.RequestAsync("$/lean/plainTermGoal", At(uri, pos), ct).ConfigureAwait(false);
        return r.As<PlainTermGoal>();
    }

    public async Task<Hover?> HoverAsync(string uri, Position pos, CancellationToken ct = default)
    {
        JsonElement r = await Rpc.RequestAsync("textDocument/hover", At(uri, pos), ct).ConfigureAwait(false);
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("contents", out JsonElement c))
        {
            return null;
        }
        string text = c.ValueKind switch
        {
            JsonValueKind.String => c.GetString() ?? "",
            JsonValueKind.Object => c.TryGetProperty("value", out JsonElement v) ? v.GetString() ?? "" : "",
            JsonValueKind.Array => string.Join("\n\n", c.EnumerateArray().Select(x =>
                x.ValueKind == JsonValueKind.String ? x.GetString() : x.TryGetProperty("value", out JsonElement v) ? v.GetString() : "")),
            _ => "",
        };
        return new Hover(text, r.TryGetProperty("range", out JsonElement range) ? range.As<Range>() : null);
    }

    public async Task<IReadOnlyList<Location>> DefinitionAsync(string uri, Position pos, CancellationToken ct = default)
    {
        JsonElement r = await Rpc.RequestAsync("textDocument/definition", At(uri, pos), ct).ConfigureAwait(false);
        var list = new List<Location>();
        IEnumerable<JsonElement> items = r.ValueKind switch
        {
            JsonValueKind.Array => r.EnumerateArray(),
            JsonValueKind.Object => [r],
            _ => [],
        };
        foreach (JsonElement e in items)
        {
            // Location has uri+range; LocationLink has targetUri+targetSelectionRange.
            if (e.TryGetProperty("targetUri", out JsonElement tu))
            {
                list.Add(new Location(tu.GetString() ?? "", e.GetProperty("targetSelectionRange").As<Range>()));
            }
            else if (e.TryGetProperty("uri", out JsonElement u))
            {
                list.Add(new Location(u.GetString() ?? "", e.GetProperty("range").As<Range>()));
            }
        }
        return list;
    }

    public async Task<IReadOnlyList<CompletionItem>> CompletionAsync(string uri, Position pos, CancellationToken ct = default)
    {
        JsonElement r = await Rpc.RequestAsync("textDocument/completion", At(uri, pos), ct).ConfigureAwait(false);
        JsonElement items = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("items", out JsonElement i) ? i : r;
        if (items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var list = new List<CompletionItem>();
        foreach (JsonElement e in items.EnumerateArray())
        {
            string? doc = null;
            if (e.TryGetProperty("documentation", out JsonElement d))
            {
                doc = d.ValueKind == JsonValueKind.String ? d.GetString() : d.TryGetProperty("value", out JsonElement dv) ? dv.GetString() : null;
            }
            list.Add(new CompletionItem(
                e.GetProperty("label").GetString() ?? "",
                e.TryGetProperty("detail", out JsonElement det) ? det.GetString() : null,
                doc,
                e.TryGetProperty("kind", out JsonElement k) && k.ValueKind == JsonValueKind.Number ? k.GetInt32() : null,
                e.TryGetProperty("insertText", out JsonElement it) ? it.GetString() :
                    e.TryGetProperty("textEdit", out JsonElement te) && te.TryGetProperty("newText", out JsonElement nt) ? nt.GetString() : null,
                e.TryGetProperty("sortText", out JsonElement st) ? st.GetString() : null));
        }
        return list;
    }

    public async Task<IReadOnlyList<DocumentSymbol>> DocumentSymbolsAsync(string uri, CancellationToken ct = default)
    {
        JsonElement r = await Rpc.RequestAsync("textDocument/documentSymbol", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
        }, ct).ConfigureAwait(false);
        return r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().Select(ParseSymbol).ToList() : [];
    }

    private static DocumentSymbol ParseSymbol(JsonElement e) => new(
        e.GetProperty("name").GetString() ?? "",
        e.TryGetProperty("kind", out JsonElement k) ? k.GetInt32() : 0,
        e.GetProperty("range").As<Range>(),
        e.TryGetProperty("selectionRange", out JsonElement sr) ? sr.As<Range>() : e.GetProperty("range").As<Range>(),
        e.TryGetProperty("detail", out JsonElement d) ? d.GetString() : null,
        e.TryGetProperty("children", out JsonElement c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray().Select(ParseSymbol).ToList() : []);

    public async Task<IReadOnlyList<CodeAction>> CodeActionsAsync(string uri, Range range, IReadOnlyList<Diagnostic> diagnostics, CancellationToken ct = default)
    {
        var p = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["range"] = JsonRpcConnection.ToNode(range),
            ["context"] = new JsonObject { ["diagnostics"] = JsonRpcConnection.ToNode(diagnostics) },
        };
        JsonElement r = await Rpc.RequestAsync("textDocument/codeAction", p, ct).ConfigureAwait(false);
        if (r.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var list = new List<CodeAction>();
        foreach (JsonElement a in r.EnumerateArray())
        {
            if (!a.TryGetProperty("title", out JsonElement title))
            {
                continue;
            }
            list.Add(new CodeAction(
                title.GetString() ?? "",
                a.TryGetProperty("kind", out JsonElement k) ? k.GetString() : null,
                a.TryGetProperty("edit", out JsonElement e) ? WorkspaceEdit.Parse(e) : null,
                a.TryGetProperty("isPreferred", out JsonElement pref) && pref.ValueKind == JsonValueKind.True,
                a.Clone()));
        }
        return list;
    }

    /// <summary>Fill in a code action's edit when the server sent it without one (LSP's codeAction/resolve).</summary>
    public async Task<CodeAction> ResolveAsync(CodeAction action, CancellationToken ct = default)
    {
        if (action.Edit is not null)
        {
            return action;
        }
        JsonElement r = await Rpc.RequestAsync("codeAction/resolve", action.Raw, ct).ConfigureAwait(false);
        return action with { Edit = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("edit", out JsonElement e) ? WorkspaceEdit.Parse(e) : WorkspaceEdit.Empty };
    }

    public async Task<IReadOnlyList<Location>> ReferencesAsync(string uri, Position pos, bool includeDeclaration = true, CancellationToken ct = default)
    {
        JsonObject p = At(uri, pos);
        p["context"] = new JsonObject { ["includeDeclaration"] = includeDeclaration };
        JsonElement r = await Rpc.RequestAsync("textDocument/references", p, ct).ConfigureAwait(false);
        return r.ValueKind == JsonValueKind.Array ? r.EnumerateArray().Select(l => l.As<Location>()!).ToList() : [];
    }

    /// <summary>The range of the name that would be renamed, or null when there is nothing renameable there.</summary>
    public async Task<Range?> PrepareRenameAsync(string uri, Position pos, CancellationToken ct = default)
    {
        JsonElement r = await Rpc.RequestAsync("textDocument/prepareRename", At(uri, pos), ct).ConfigureAwait(false);
        if (r.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return r.TryGetProperty("range", out JsonElement inner) ? inner.As<Range>() : r.As<Range>();
    }

    public async Task<WorkspaceEdit> RenameAsync(string uri, Position pos, string newName, CancellationToken ct = default)
    {
        JsonObject p = At(uri, pos);
        p["newName"] = newName;
        JsonElement r = await Rpc.RequestAsync("textDocument/rename", p, ct).ConfigureAwait(false);
        return WorkspaceEdit.Parse(r);
    }

    public async Task<IReadOnlyList<SymbolLocation>> WorkspaceSymbolsAsync(string query, CancellationToken ct = default)
    {
        JsonElement r = await Rpc.RequestAsync("workspace/symbol", new JsonObject { ["query"] = query }, ct).ConfigureAwait(false);
        if (r.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return r.EnumerateArray()
            .Where(s => s.TryGetProperty("location", out _))
            .Select(s => new SymbolLocation(
                s.GetProperty("name").GetString() ?? "",
                s.TryGetProperty("kind", out JsonElement k) ? k.GetInt32() : 0,
                s.GetProperty("location").As<Location>()!,
                s.TryGetProperty("containerName", out JsonElement c) ? c.GetString() : null))
            .ToList();
    }

    public async Task<IReadOnlyList<FoldingRange>> FoldingRangesAsync(string uri, CancellationToken ct = default)
    {
        JsonElement r = await Rpc.RequestAsync("textDocument/foldingRange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
        }, ct).ConfigureAwait(false);
        return r.ValueKind == JsonValueKind.Array
            ? r.EnumerateArray().Select(f => new FoldingRange(
                f.GetProperty("startLine").GetInt32(),
                f.GetProperty("endLine").GetInt32(),
                f.TryGetProperty("kind", out JsonElement k) ? k.GetString() : null)).ToList()
            : [];
    }

    // ---- Lean RPC (interactive goals) ----

    private Task<string> SessionAsync(string uri, CancellationToken ct) =>
        _sessions.GetOrAdd(uri, u => ConnectAsync(u, ct));

    private async Task<string> ConnectAsync(string uri, CancellationToken ct)
    {
        try
        {
            JsonElement r = await Rpc.RequestAsync("$/lean/rpc/connect", new JsonObject { ["uri"] = uri }, ct).ConfigureAwait(false);
            return r.GetProperty("sessionId").GetString() ?? throw new InvalidOperationException("no RPC session");
        }
        catch
        {
            _sessions.TryRemove(uri, out _);
            throw;
        }
    }

    /// <summary>Call a server-side RPC method (an <c>@[server_rpc_method]</c>) at a position in a file.</summary>
    public async Task<JsonElement> RpcCallAsync(string uri, Position pos, string method, JsonNode? parameters, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            string session = await SessionAsync(uri, ct).ConfigureAwait(false);
            var call = At(uri, pos);
            call["sessionId"] = session;
            call["method"] = method;
            call["params"] = parameters?.DeepClone();
            try
            {
                return await Rpc.RequestAsync("$/lean/rpc/call", call, ct).ConfigureAwait(false);
            }
            catch (JsonRpcException e) when (e.Code == RpcNeedsReconnect && attempt == 0)
            {
                _sessions.TryRemove(uri, out _);
            }
        }
    }

    /// <summary>
    /// The goals at a position. With <paramref name="keepReferences"/>, the subterm references in them stay valid
    /// for <see cref="InspectAsync"/> until handed back with <see cref="ReleaseAsync"/>.
    /// </summary>
    public async Task<InteractiveGoals> InteractiveGoalsAsync(string uri, Position pos, CancellationToken ct = default, bool keepReferences = false)
    {
        var p = At(uri, pos);
        JsonElement r = await RpcCallAsync(uri, pos, "Lean.Widget.getInteractiveGoals", p, ct).ConfigureAwait(false);
        InteractiveGoals goals = InteractiveGoals.Parse(r);
        if (!keepReferences)
        {
            await ReleaseAsync(uri, goals.References().ToList()).ConfigureAwait(false);
        }
        return goals;
    }

    /// <summary>
    /// What Lean knows about a subterm of a goal (from its reference): the term written out in full, its type,
    /// and the documentation of its head constant.
    /// </summary>
    public async Task<SubtermInfo?> InspectAsync(string uri, Position pos, string reference, CancellationToken ct = default)
    {
        JsonElement r = await RpcCallAsync(uri, pos, "Lean.Widget.InteractiveDiagnostics.infoToInteractive", new JsonObject { ["p"] = reference }, ct).ConfigureAwait(false);
        if (r.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var info = new SubtermInfo(
            r.TryGetProperty("exprExplicit", out JsonElement e) ? TaggedString.Parse(e).Text : null,
            r.TryGetProperty("type", out JsonElement t) ? TaggedString.Parse(t).Text : null,
            r.TryGetProperty("doc", out JsonElement d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null);
        var refs = new List<string>();
        foreach (string name in new[] { "exprExplicit", "type" })
        {
            if (r.TryGetProperty(name, out JsonElement x))
            {
                refs.AddRange(TaggedString.Parse(x).Spans.Select(s => s.Reference).OfType<string>());
            }
        }
        await ReleaseAsync(uri, refs).ConfigureAwait(false);
        return info;
    }

    public async Task<InteractiveGoals> InteractiveTermGoalAsync(string uri, Position pos, CancellationToken ct = default)
    {
        var p = At(uri, pos);
        JsonElement r = await RpcCallAsync(uri, pos, "Lean.Widget.getInteractiveTermGoal", p, ct).ConfigureAwait(false);
        if (r.ValueKind != JsonValueKind.Object)
        {
            return InteractiveGoals.None;
        }
        // The term goal is a single InteractiveGoal with a range; reuse the goal parser.
        var wrapped = new JsonObject { ["goals"] = new JsonArray(JsonNode.Parse(r.GetRawText())) };
        InteractiveGoals goals = InteractiveGoals.Parse(JsonDocument.Parse(wrapped.ToJsonString()).RootElement);
        await ReleaseAsync(uri, goals.References().ToList()).ConfigureAwait(false);
        return goals;
    }

    public async Task ReleaseAsync(string uri, List<string> refs)
    {
        if (refs.Count == 0 || !_sessions.TryGetValue(uri, out Task<string>? s) || !s.IsCompletedSuccessfully)
        {
            return;
        }
        var arr = new JsonArray(refs.Select(r => (JsonNode)new JsonObject { ["p"] = r }).ToArray());
        try
        {
            await Rpc.NotifyAsync("$/lean/rpc/release", new JsonObject
            {
                ["uri"] = uri,
                ["sessionId"] = s.Result,
                ["refs"] = arr,
            }).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private async Task KeepAliveAsync()
    {
        foreach ((string uri, Task<string> s) in _sessions)
        {
            if (!s.IsCompletedSuccessfully || _rpc is null)
            {
                continue;
            }
            try
            {
                await _rpc.NotifyAsync("$/lean/rpc/keepAlive", new JsonObject { ["uri"] = uri, ["sessionId"] = s.Result }).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        if (_keepAlive is not null)
        {
            await _keepAlive.DisposeAsync().ConfigureAwait(false);
        }
        if (_rpc is not null && State == LeanServerState.Running)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _rpc.RequestAsync("shutdown", null, cts.Token).ConfigureAwait(false);
                await _rpc.NotifyAsync("exit", null).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or JsonRpcException or ObjectDisposedException)
            {
            }
        }
        SetState(LeanServerState.Stopped);
        if (_process is not null)
        {
            try
            {
                if (!_process.WaitForExit(1500))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            _process.Dispose();
        }
        if (_rpc is not null)
        {
            await _rpc.DisposeAsync().ConfigureAwait(false);
        }
    }
}
