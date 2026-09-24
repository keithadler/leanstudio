using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LeanStudio.Lsp;

namespace LeanStudio.Core.Agents;

/// <summary>What the infoview asks the editor to do: the editor side of Lean's <c>EditorApi</c>.</summary>
public interface IInfoviewEditor
{
    /// <summary>Put text on the clipboard.</summary>
    Task CopyAsync(string text);

    /// <summary>Insert text at the cursor (<paramref name="kind"/> "here") or on a new line above it ("above"), or at a given position.</summary>
    Task InsertTextAsync(string text, string kind, string? uri, Position? position);

    /// <summary>Apply an edit (a "Try this" suggestion, say).</summary>
    Task ApplyEditAsync(WorkspaceEdit edit);

    /// <summary>Open a file, and select a range in it if one is given (go to definition from the infoview).</summary>
    Task ShowDocumentAsync(string uri, Lsp.Range? selection);

    /// <summary>Restart the file in Lean (after its imports changed).</summary>
    Task RestartFileAsync(string uri);
}

/// <summary>
/// Lean's own infoview (the <c>@leanprover/infoview</c> React app that VS Code uses), served to a browser tab and
/// connected to Lean Studio's Lean server. It renders everything the infoview renders, ProofWidgets and other user
/// widgets included, and follows the cursor in Lean Studio.
///
/// The page and this bridge talk JSON over a WebSocket: the page's <c>EditorApi</c> calls become requests here
/// (relayed to the Lean server, or to the editor), and the server's notifications, the client's document
/// notifications and cursor moves go back to it. RPC sessions are kept alive from here, as the VS Code extension
/// does. Only this machine can connect (127.0.0.1), and only with the secret token in the page's address.
/// </summary>
public sealed class InfoviewBridge : IAsyncDisposable
{
    private readonly Func<LeanServer?> _server;
    private readonly IInfoviewEditor _editor;
    private readonly Func<string, (byte[] Data, string ContentType)?> _assets;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private readonly ConcurrentDictionary<Page, byte> _pages = new();
    private readonly CancellationTokenSource _stop = new();
    private HttpListener? _listener;
    private LeanServer? _wired;
    private JsonObject? _cursor;

    /// <summary>A bridge that relays to <paramref name="server"/> and serves files from <paramref name="assets"/> (by path).</summary>
    public InfoviewBridge(Func<LeanServer?> server, IInfoviewEditor editor, Func<string, (byte[] Data, string ContentType)?> assets)
    {
        _server = server;
        _editor = editor;
        _assets = assets;
    }

    /// <summary>The page's address, once started.</summary>
    public Uri? Address { get; private set; }

    /// <summary>How many pages are connected.</summary>
    public int Connections => _pages.Count;

    /// <summary>Messages about what the bridge is doing, for the Output panel.</summary>
    public event Action<string>? Log;

    /// <summary>Start serving on a free port on 127.0.0.1 and return the page's address (with its token).</summary>
    public Uri Start(string theme = "dark")
    {
        if (Address is not null)
        {
            return WithTheme(theme);
        }
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        Address = new Uri($"http://127.0.0.1:{port}/?t={_token}");
        _ = Task.Run(AcceptLoopAsync);
        return WithTheme(theme);
    }

    private Uri WithTheme(string theme) => new(Address + "&theme=" + Uri.EscapeDataString(theme));

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested && _listener is { IsListening: true } l)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await l.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            bool authorised = ctx.Request.QueryString["t"] == _token;
            if (path == "/ws")
            {
                string? origin = ctx.Request.Headers["Origin"];
                if (!ctx.Request.IsWebSocketRequest || !authorised || (origin is not null && origin != $"http://127.0.0.1:{Address!.Port}"))
                {
                    ctx.Response.StatusCode = 403;
                    ctx.Response.Close();
                    return;
                }
                HttpListenerWebSocketContext ws = await ctx.AcceptWebSocketAsync(null).ConfigureAwait(false);
                await ServeAsync(ws.WebSocket).ConfigureAwait(false);
                return;
            }
            if (path == "/" && !authorised)
            {
                ctx.Response.StatusCode = 403;
                ctx.Response.Close();
                return;
            }
            if (_assets(path == "/" ? "index.html" : path.TrimStart('/')) is (byte[] data, string type))
            {
                ctx.Response.ContentType = type;
                ctx.Response.Headers["Cache-Control"] = "no-store";
                await ctx.Response.OutputStream.WriteAsync(data).ConfigureAwait(false);
                ctx.Response.Close();
                return;
            }
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
        catch (Exception e) when (e is HttpListenerException or IOException or WebSocketException or ObjectDisposedException)
        {
        }
    }

    // ---- one connected page ----

    private sealed class Page(WebSocket socket)
    {
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public ConcurrentDictionary<string, byte> ServerSubscriptions { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, byte> ClientSubscriptions { get; } = new(StringComparer.Ordinal);
        public ConcurrentDictionary<string, (string Uri, Timer KeepAlive)> Sessions { get; } = new(StringComparer.Ordinal);
    }

    private async Task ServeAsync(WebSocket socket)
    {
        var page = new Page(socket);
        _pages[page] = 0;
        WireServer();
        Log?.Invoke("Infoview: a page connected.");
        var buffer = new byte[1 << 16];
        try
        {
            while (socket.State == WebSocketState.Open && !_stop.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await socket.ReceiveAsync(buffer, _stop.Token).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    message.Write(buffer, 0, r.Count);
                }
                while (!r.EndOfMessage);
                if (JsonNode.Parse(message.ToArray()) is JsonObject m)
                {
                    _ = HandleMessageAsync(page, m);
                }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or JsonException or ObjectDisposedException)
        {
        }
        finally
        {
            _pages.TryRemove(page, out _);
            foreach ((string _, (string _, Timer timer)) in page.Sessions)
            {
                await timer.DisposeAsync().ConfigureAwait(false);
            }
            Log?.Invoke("Infoview: a page disconnected.");
        }
    }

    private async Task HandleMessageAsync(Page page, JsonObject m)
    {
        string op = m["op"]?.GetValue<string>() ?? "";
        JsonNode? id = m["id"]?.DeepClone();
        try
        {
            JsonNode? result = await DispatchAsync(page, op, m).ConfigureAwait(false);
            if (id is not null)
            {
                await SendAsync(page, new JsonObject { ["id"] = id, ["result"] = result }).ConfigureAwait(false);
            }
        }
        catch (JsonRpcException e)
        {
            if (id is not null)
            {
                var error = new JsonObject { ["code"] = e.Code, ["message"] = e.Message };
                if (e.ErrorData is JsonElement data)
                {
                    error["data"] = JsonNode.Parse(data.GetRawText());
                }
                await SendAsync(page, new JsonObject { ["id"] = id, ["error"] = error }).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or OperationCanceledException or JsonException or KeyNotFoundException or FormatException)
        {
            if (id is not null)
            {
                await SendAsync(page, new JsonObject { ["id"] = id, ["error"] = new JsonObject { ["code"] = -32603, ["message"] = e.Message } }).ConfigureAwait(false);
            }
        }
    }

    private LeanServer Server => _server() is { State: LeanServerState.Running } s ? s : throw new InvalidOperationException("Lean is not running");

    private async Task<JsonNode?> DispatchAsync(Page page, string op, JsonObject m)
    {
        switch (op)
        {
            case "ready":
                await SendAsync(page, new JsonObject { ["op"] = "serverRestarted", ["result"] = InitializeResult() }).ConfigureAwait(false);
                if (_cursor is not null)
                {
                    await SendAsync(page, new JsonObject { ["op"] = "initialize", ["loc"] = _cursor.DeepClone() }).ConfigureAwait(false);
                }
                return null;
            case "request":
            {
                JsonElement r = await Server.RequestRawAsync(m["method"]!.GetValue<string>(), m["params"]?.DeepClone(), _stop.Token).ConfigureAwait(false);
                return r.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(r.GetRawText());
            }
            case "notify":
                await Server.NotifyRawAsync(m["method"]!.GetValue<string>(), m["params"]?.DeepClone()).ConfigureAwait(false);
                return null;
            case "createRpcSession":
            {
                string uri = m["uri"]!.GetValue<string>();
                LeanServer server = Server;
                JsonElement r = await server.RequestRawAsync("$/lean/rpc/connect", new JsonObject { ["uri"] = uri }, _stop.Token).ConfigureAwait(false);
                string session = r.GetProperty("sessionId").ToString();
                var timer = new Timer(_ =>
                {
                    try
                    {
                        _ = server.NotifyRawAsync("$/lean/rpc/keepAlive", new JsonObject { ["uri"] = uri, ["sessionId"] = session });
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
                page.Sessions[session] = (uri, timer);
                return session;
            }
            case "closeRpcSession":
                if (page.Sessions.TryRemove(m["sessionId"]!.GetValue<string>(), out var s))
                {
                    await s.KeepAlive.DisposeAsync().ConfigureAwait(false);
                }
                return null;
            case "subscribeServer":
                page.ServerSubscriptions[m["method"]!.GetValue<string>()] = 0;
                return null;
            case "unsubscribeServer":
                page.ServerSubscriptions.TryRemove(m["method"]!.GetValue<string>(), out _);
                return null;
            case "subscribeClient":
                page.ClientSubscriptions[m["method"]!.GetValue<string>()] = 0;
                return null;
            case "unsubscribeClient":
                page.ClientSubscriptions.TryRemove(m["method"]!.GetValue<string>(), out _);
                return null;
            case "copy":
                await _editor.CopyAsync(m["text"]!.GetValue<string>()).ConfigureAwait(false);
                return null;
            case "insertText":
            {
                JsonNode? pos = m["pos"];
                await _editor.InsertTextAsync(m["text"]!.GetValue<string>(), m["kind"]?.GetValue<string>() ?? "here",
                    pos?["textDocument"]?["uri"]?.GetValue<string>(),
                    pos?["position"] is JsonObject p ? new Position(p["line"]!.GetValue<int>(), p["character"]!.GetValue<int>()) : null).ConfigureAwait(false);
                return null;
            }
            case "applyEdit":
                await _editor.ApplyEditAsync(WorkspaceEdit.Parse(JsonDocument.Parse(m["edit"]!.ToJsonString()).RootElement)).ConfigureAwait(false);
                return null;
            case "showDocument":
            {
                JsonNode show = m["show"]!;
                Lsp.Range? sel = show["selection"] is JsonObject r ? JsonSerializer.Deserialize<Lsp.Range>(r.ToJsonString(), new JsonSerializerOptions(JsonSerializerDefaults.Web)) : null;
                await _editor.ShowDocumentAsync(show["uri"]!.GetValue<string>(), sel).ConfigureAwait(false);
                return null;
            }
            case "restartFile":
                await _editor.RestartFileAsync(m["uri"]!.GetValue<string>()).ConfigureAwait(false);
                return null;
            case "saveConfig":
                return null;
            default:
                throw new InvalidOperationException("unknown operation " + op);
        }
    }

    private JsonObject InitializeResult()
    {
        LeanServer? s = _server();
        JsonNode? caps = s is null || s.ServerCapabilities.ValueKind != JsonValueKind.Object ? new JsonObject() : JsonNode.Parse(s.ServerCapabilities.GetRawText());
        return new JsonObject { ["capabilities"] = caps, ["serverInfo"] = new JsonObject { ["name"] = "Lean 4 Server", ["version"] = s?.ServerVersion } };
    }

    /// <summary>Follow the server's notifications and the client's document notifications, once per server.</summary>
    private void WireServer()
    {
        LeanServer? s = _server();
        if (s is null || ReferenceEquals(s, _wired))
        {
            return;
        }
        _wired = s;
        s.ServerNotification += (method, p) => Broadcast(page => page.ServerSubscriptions.ContainsKey(method),
            () => new JsonObject { ["op"] = "serverNotification", ["method"] = method, ["params"] = JsonNode.Parse(p.GetRawText()) });
        s.ClientNotification += (method, p) => Broadcast(page => page.ClientSubscriptions.ContainsKey(method),
            () => new JsonObject { ["op"] = "clientNotification", ["method"] = method, ["params"] = p.DeepClone() });
    }

    /// <summary>Tell connected pages that the Lean server was (re)started, so they reconnect their sessions.</summary>
    public void ServerRestarted()
    {
        WireServer();
        Broadcast(_ => true, () => new JsonObject { ["op"] = "serverRestarted", ["result"] = InitializeResult() });
    }

    /// <summary>Lean Studio's theme changed: connected pages restyle to match (<paramref name="theme"/> is "light" or "dark").</summary>
    public void SetTheme(string theme) => Broadcast(_ => true, () => new JsonObject { ["op"] = "theme", ["theme"] = theme });

    /// <summary>The cursor moved in Lean Studio: the infoview shows the state there.</summary>
    public void CursorMoved(string uri, Position pos)
    {
        var loc = new JsonObject
        {
            ["uri"] = uri,
            ["range"] = new JsonObject
            {
                ["start"] = new JsonObject { ["line"] = pos.Line, ["character"] = pos.Character },
                ["end"] = new JsonObject { ["line"] = pos.Line, ["character"] = pos.Character },
            },
        };
        _cursor = loc;
        Broadcast(_ => true, () => new JsonObject { ["op"] = "cursor", ["loc"] = loc.DeepClone() });
    }

    private void Broadcast(Func<Page, bool> wants, Func<JsonObject> message)
    {
        foreach (Page page in _pages.Keys)
        {
            if (wants(page))
            {
                _ = SendAsync(page, message());
            }
        }
    }

    private static async Task SendAsync(Page page, JsonObject message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await page.SendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (page.Socket.State == WebSocketState.Open)
            {
                await page.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is WebSocketException or ObjectDisposedException or IOException)
        {
        }
        finally
        {
            page.SendLock.Release();
        }
    }

    /// <summary>Stop serving and close every page's connection.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (Page page in _pages.Keys)
        {
            foreach ((string _, (string _, Timer timer)) in page.Sessions)
            {
                await timer.DisposeAsync().ConfigureAwait(false);
            }
            page.Socket.Abort();
        }
        _listener?.Close();
        _stop.Dispose();
    }
}
