using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeanStudio.Lsp;

/// <summary>How to start a Lean language server: the command, its arguments, and the directory it runs in.</summary>
/// <param name="FileName">The executable, such as <c>lake</c> or <c>lean</c>, or a full path to one.</param>
/// <param name="Arguments">The arguments, such as <c>serve</c> or <c>--server</c>; each is passed as it is, without shell quoting.</param>
/// <param name="WorkingDirectory">The directory to run in, normally the project root; also sent to the server as its <c>rootUri</c>.</param>
public sealed record LeanServerCommand(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory)
{
    /// <summary>The command line, for messages; arguments are joined with spaces and not quoted.</summary>
    public override string ToString() => FileName + " " + string.Join(' ', Arguments);
}

/// <summary>Where a <see cref="LeanServer"/> is in its life.</summary>
public enum LeanServerState
{
    /// <summary>Not started yet, or shut down by <see cref="LeanServer.DisposeAsync"/>.</summary>
    Stopped,
    /// <summary>The process is starting and the LSP handshake is under way.</summary>
    Starting,
    /// <summary>Initialized and answering requests.</summary>
    Running,
    /// <summary>The process could not be started, or exited without being asked to.</summary>
    Crashed,
}

/// <summary>
/// One running Lean language server (<c>lake serve</c> or <c>lean --server</c>) and the documents open in it.
/// Everything the editor needs goes through here: document sync, diagnostics, file progress, hover, definitions,
/// completion, and the goal queries, including the interactive goals that carry Lean's own before/after diff of
/// every tactic.
/// </summary>
public sealed partial class LeanServer : IAsyncDisposable
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
    private readonly ConcurrentDictionary<string, List<Diagnostic>> _silent = new();
    private readonly ConcurrentDictionary<string, int> _elaborated = new();
    private readonly object _waitLock = new();
    private readonly List<(string Uri, int Version, TaskCompletionSource Done)> _waiters = [];
    private Timer? _keepAlive;
    private bool _disposed;

    /// <summary>Prepare a server. Nothing runs until <see cref="StartAsync"/>.</summary>
    /// <param name="command">How to start the server process.</param>
    public LeanServer(LeanServerCommand command) => _command = command;

    /// <summary>The server process's id while it runs, or null (for checks, and Lean's Processes).</summary>
    public int? ProcessId
    {
        get
        {
            try
            {
                return _process is { HasExited: false } p ? p.Id : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>How the server is started.</summary>
    public LeanServerCommand Command => _command;
    /// <summary>Where the server is in its life; <see cref="StateChanged"/> reports each change.</summary>
    public LeanServerState State { get; private set; } = LeanServerState.Stopped;
    /// <summary>The <c>capabilities</c> from the server's <c>initialize</c> response; undefined until started.</summary>
    public JsonElement ServerCapabilities { get; private set; }
    /// <summary>The Lean version the server reported when it started, or null when it did not say (or has not started).</summary>
    public string? ServerVersion { get; private set; }

    /// <summary>
    /// <see cref="State"/> changed. Raised on whichever thread made the change (a crash is reported from the process's
    /// exit handler), so handlers that touch UI must marshal.
    /// </summary>
    public event Action<LeanServerState>? StateChanged;
    /// <summary>
    /// A file to append every message exchanged with the server to (JSON, with the time and direction), or null for
    /// none. Takes effect when the server starts.
    /// </summary>
    public string? MessageLogPath { get; set; }

    /// <summary>
    /// Lean published the diagnostics for a file (URI, and the full current list, which replaces any before). It publishes
    /// several times while elaborating. Raised on the connection's read thread.
    /// </summary>
    public event Action<string, IReadOnlyList<Diagnostic>>? DiagnosticsPublished;
    /// <summary>
    /// Lean reported which ranges of a file (URI) it has still to elaborate; an empty list means it is done. Raised on the
    /// connection's read thread.
    /// </summary>
    public event Action<string, IReadOnlyList<LeanFileProgressRange>>? FileProgress;
    /// <summary>A line of the server's stderr, or a message it logged.</summary>
    public event Action<string>? Log;
    /// <summary>The server asked the client to re-request semantic tokens and inlay hints: its view of the file moved on.</summary>
    public event Action? RefreshRequested;

    /// <summary>
    /// Start the server process and complete the LSP handshake. Does nothing if it is already running or starting.
    /// Also starts a timer that keeps the RPC sessions alive every ten seconds. If the process dies during the
    /// handshake, the connection's <see cref="IOException"/> propagates.
    /// </summary>
    /// <exception cref="System.ComponentModel.Win32Exception">The executable could not be run; <see cref="State"/> is then <see cref="LeanServerState.Crashed"/>.</exception>
    /// <exception cref="InvalidOperationException">The process could not be started; <see cref="State"/> is then <see cref="LeanServerState.Crashed"/>.</exception>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (State is LeanServerState.Running or LeanServerState.Starting)
        {
            return;
        }
        SetState(LeanServerState.Starting);
        // A remote project's server runs on its machine, over SSH, with paths rewritten both ways.
        RemoteTarget? remote = RemoteTargets.For(_command.WorkingDirectory);
        (string fileName, IReadOnlyList<string> arguments) = remote is null
            ? (_command.FileName, _command.Arguments)
            : remote.Command(_command.FileName, _command.Arguments, _command.WorkingDirectory);
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = Directory.Exists(_command.WorkingDirectory) ? _command.WorkingDirectory : Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in arguments)
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
                Log?.Invoke(remote is null ? e.Data : remote.ToLocal(e.Data));
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
        _rpc.DispatchFailed += e => Log?.Invoke("skipped a message from the Lean server that could not be handled: " + e.Message);
        if (remote is not null)
        {
            _rpc.Outgoing = remote.ToRemote;
            _rpc.Incoming = remote.ToLocal;
        }
        if (MessageLogPath is string logPath)
        {
            // Every message, with the time and its direction: for troubleshooting the server.
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            var log = new StreamWriter(logPath, append: true) { AutoFlush = true };
            var gate = new object();
            _rpc.Traffic = (sent, json) =>
            {
                lock (gate)
                {
                    log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {(sent ? "→" : "←")} {json}");
                }
            };
            _rpc.Closed += _ =>
            {
                lock (gate)
                {
                    log.Dispose();
                }
            };
        }
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
                // Lean's own extensions: its silent "goals accomplished" messages, for the end-of-proof marks.
                ["lean"] = new JsonObject { ["silentDiagnosticSupport"] = true },
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
        ServerNotification?.Invoke(method, p);
        switch (method)
        {
            case "textDocument/publishDiagnostics":
            {
                string uri = p.GetProperty("uri").GetString() ?? "";
                var all = p.GetProperty("diagnostics").As<List<Diagnostic>>() ?? [];
                // Silent ones (Goals accomplished!) aren't messages to show: they are kept apart, for the proof marks.
                var list = all.Where(d => d.IsSilent != true).ToList();
                _silent[uri] = all.Where(d => d.IsSilent == true).ToList();
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

    /// <summary>The <c>file://</c> URI LSP uses for a path (made absolute against the current directory first).</summary>
    public static string UriOf(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    /// <summary>The local file path of a <c>file://</c> URI; the inverse of <see cref="UriOf"/>.</summary>
    public static string PathOf(string uri) => new Uri(uri).LocalPath;

    /// <summary>
    /// Open a document in the server (<c>didOpen</c>) with its text, as version 1. Lean starts elaborating it at once;
    /// diagnostics follow through <see cref="DiagnosticsPublished"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The server has not been started.</exception>
    public Task OpenAsync(string uri, string text)
    {
        _versions[uri] = 1;
        _elaborated.TryRemove(uri, out _);
        return NotifyClientAsync("textDocument/didOpen", new JsonObject
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
        return NotifyClientAsync("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text }),
        });
    }

    /// <summary>Tell the server a document was saved (<c>didSave</c>), with the saved text. It does not write the file.</summary>
    public Task SaveAsync(string uri, string text) =>
        NotifyClientAsync("textDocument/didSave", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["text"] = text,
        });

    /// <summary>Close a document in the server (<c>didClose</c>) and forget its version, diagnostics and RPC session.</summary>
    public Task CloseAsync(string uri)
    {
        _versions.TryRemove(uri, out _);
        _sessions.TryRemove(uri, out _);
        _elaborated.TryRemove(uri, out _);
        _diagnostics.TryRemove(uri, out _);
        return NotifyClientAsync("textDocument/didClose", new JsonObject
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

    /// <summary>Whether the document has been opened with <see cref="OpenAsync"/> and not closed since.</summary>
    public bool IsOpen(string uri) => _versions.ContainsKey(uri);

    /// <summary>The version of the text Lean was last sent for a file; 0 when it is not open.</summary>
    public int VersionOf(string uri) => _versions.GetValueOrDefault(uri);

    /// <summary>
    /// The silent diagnostics Lean last sent for a document, apart from <see cref="DiagnosticsOf"/>: its "Goals
    /// accomplished!" for each finished proof.
    /// </summary>
    public IReadOnlyList<Diagnostic> SilentDiagnosticsOf(string uri) => _silent.TryGetValue(uri, out List<Diagnostic>? l) ? l : [];

    /// <summary>The newest diagnostics Lean published for a file; empty when there are none yet or it is not open.</summary>
    public IReadOnlyList<Diagnostic> DiagnosticsOf(string uri) =>
        _diagnostics.TryGetValue(uri, out var d) ? d.Diagnostics : [];

    /// <summary>
    /// Wait until Lean has finished elaborating the text most recently sent for a file, and its diagnostics for
    /// that text have arrived. What a caller that edits and then asks "did it work?" needs: an answer about the
    /// text it sent, not the one before.
    /// </summary>
    /// <remarks>
    /// Waits indefinitely for elaboration (use <paramref name="ct"/> to bound it), then up to about four more seconds
    /// for the diagnostics to arrive and settle.
    /// </remarks>
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

    internal static JsonObject At(string uri, Position pos) => new()
    {
        ["textDocument"] = new JsonObject { ["uri"] = uri },
        ["position"] = new JsonObject { ["line"] = pos.Line, ["character"] = pos.Character },
    };

    // ---- queries ----

    /// <summary>The tactic goals at a position as plain text (<c>$/lean/plainGoal</c>); null when there is no tactic proof there.</summary>
    /// <param name="uri">The document, which must be open.</param>
    /// <param name="pos">The 0-based position of the caret.</param>
    /// <param name="ct">Cancels the request.</param>
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

    /// <summary>The expected type at a position in a term (<c>$/lean/plainTermGoal</c>); null when there is none.</summary>
    public async Task<PlainTermGoal?> PlainTermGoalAsync(string uri, Position pos, CancellationToken ct = default)
    {
        JsonElement r = await Rpc.RequestAsync("$/lean/plainTermGoal", At(uri, pos), ct).ConfigureAwait(false);
        return r.As<PlainTermGoal>();
    }

    /// <summary>The hover at a 0-based position: usually the type and docstring of the name there; null when there is none.</summary>
    public async Task<Hover?> HoverAsync(string uri, Position pos, CancellationToken ct = default) =>
        ParseHover(await Rpc.RequestAsync("textDocument/hover", At(uri, pos), ct).ConfigureAwait(false));

    /// <summary>Where the name at a 0-based position is defined; empty when Lean does not know. Links are reduced to their target's name range.</summary>
    public async Task<IReadOnlyList<Location>> DefinitionAsync(string uri, Position pos, CancellationToken ct = default) =>
        ParseLocations(await Rpc.RequestAsync("textDocument/definition", At(uri, pos), ct).ConfigureAwait(false));

    /// <summary>The completions at a 0-based position, in the server's order; empty when there are none.</summary>
    public async Task<IReadOnlyList<CompletionItem>> CompletionAsync(string uri, Position pos, CancellationToken ct = default) =>
        ParseCompletions(await Rpc.RequestAsync("textDocument/completion", At(uri, pos), ct).ConfigureAwait(false));

    /// <summary>Reads a <c>textDocument/hover</c> result; null when it has no contents.</summary>
    internal static Hover? ParseHover(JsonElement r)
    {
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

    /// <summary>Reads a <c>textDocument/definition</c> result (locations or location links); empty when there is none.</summary>
    internal static IReadOnlyList<Location> ParseLocations(JsonElement r)
    {
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

    /// <summary>Reads a <c>textDocument/completion</c> result (a list or a completion list); empty when there is none.</summary>
    internal static IReadOnlyList<CompletionItem> ParseCompletions(JsonElement r)
    {
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

    /// <summary>The outline of a document: its declarations and sections, nested.</summary>
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

    /// <summary>
    /// The code actions for a range, such as Lean's "Try this" suggestions. Some come without their edit; fill it in with
    /// <see cref="ResolveAsync"/>.
    /// </summary>
    /// <param name="uri">The document.</param>
    /// <param name="range">The range, usually the caret or selection.</param>
    /// <param name="diagnostics">The diagnostics at that range, sent as the request's context.</param>
    /// <param name="ct">Cancels the request.</param>
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

    /// <summary>Every use of the name at a 0-based position, as far as Lean's reference index for the project goes.</summary>
    /// <param name="uri">The document.</param>
    /// <param name="pos">The position of the name.</param>
    /// <param name="includeDeclaration">Whether the declaration itself is included.</param>
    /// <param name="ct">Cancels the request.</param>
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

    /// <summary>The edits that rename the name at a 0-based position everywhere. Nothing is applied; the caller applies the edit.</summary>
    public async Task<WorkspaceEdit> RenameAsync(string uri, Position pos, string newName, CancellationToken ct = default)
    {
        JsonObject p = At(uri, pos);
        p["newName"] = newName;
        JsonElement r = await Rpc.RequestAsync("textDocument/rename", p, ct).ConfigureAwait(false);
        return WorkspaceEdit.Parse(r);
    }

    /// <summary>Declarations across the workspace whose names match <paramref name="query"/> (fuzzily, as the server matches).</summary>
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

    /// <summary>The spans of lines the editor can fold in a document.</summary>
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
    /// <remarks>
    /// Connects an RPC session for the file on first use and reuses it. If the server has forgotten the session
    /// (<see cref="RpcNeedsReconnect"/>), it reconnects and tries once more.
    /// </remarks>
    /// <param name="uri">The document, which must be open.</param>
    /// <param name="pos">The 0-based position the call is made at.</param>
    /// <param name="method">The method's full name, such as <c>Lean.Widget.getInteractiveGoals</c>.</param>
    /// <param name="parameters">The method's parameters (copied), or null.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="JsonRpcException">The server answered with an error.</exception>
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

    /// <summary>
    /// The expected type at a position in a term, as an interactive goal; <see cref="InteractiveGoals.None"/> when there
    /// is none. Its subterm references are released before it returns.
    /// </summary>
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

    /// <summary>
    /// Hand subterm references back to the server so it can free what they point to. Does nothing when there are none
    /// or the file has no connected RPC session; errors sending the notification are ignored.
    /// </summary>
    /// <param name="uri">The document the references came from.</param>
    /// <param name="refs">The references (<see cref="TaggedSpan.Reference"/> values).</param>
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

    /// <summary>
    /// Shut the server down: ask it politely (<c>shutdown</c>, then <c>exit</c>, waiting up to two seconds), then kill the
    /// process tree if it has not exited within another second and a half. The state becomes <see cref="LeanServerState.Stopped"/>.
    /// Calling it again does nothing.
    /// </summary>
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
