using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using LeanStudio.Core.Agents;
using LeanStudio.Lsp;

namespace LeanStudio.Tests;

/// <summary>The bridge that serves Lean's own infoview to a browser and relays it to the Lean server.</summary>
[Collection(Lean.Collection)]
public sealed class InfoviewBridgeTests
{
    private sealed class Editor : IInfoviewEditor
    {
        public List<string> Calls { get; } = [];
        public Task CopyAsync(string text) { Calls.Add("copy " + text); return Task.CompletedTask; }
        public Task InsertTextAsync(string text, string kind, string? uri, Position? position) { Calls.Add($"insert {kind} {text} {position?.Line}"); return Task.CompletedTask; }
        public Task ApplyEditAsync(WorkspaceEdit edit) { Calls.Add("edit " + edit.Changes.Count); return Task.CompletedTask; }
        public Task ShowDocumentAsync(string uri, Lsp.Range? selection) { Calls.Add($"show {Path.GetFileName(uri)} {selection?.Start.Line}"); return Task.CompletedTask; }
        public Task RestartFileAsync(string uri) { Calls.Add("restart"); return Task.CompletedTask; }
    }

    private sealed class Client(ClientWebSocket socket) : IAsyncDisposable
    {
        private int _next = 1;

        public async Task SendAsync(JsonObject m) =>
            await socket.SendAsync(Encoding.UTF8.GetBytes(m.ToJsonString()), WebSocketMessageType.Text, true, TestContext.Current.CancellationToken);

        public async Task<JsonObject> ReceiveAsync(Func<JsonObject, bool> want)
        {
            var buffer = new byte[1 << 20];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(Lean.Patience);
            while (true)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await socket.ReceiveAsync(buffer, timeout.Token);
                    ms.Write(buffer, 0, r.Count);
                }
                while (!r.EndOfMessage);
                if (JsonNode.Parse(ms.ToArray()) is JsonObject o && want(o))
                {
                    return o;
                }
            }
        }

        public async Task<JsonObject> CallAsync(string op, JsonObject fields)
        {
            int id = _next++;
            fields["id"] = id;
            fields["op"] = op;
            await SendAsync(fields);
            return await ReceiveAsync(o => o["id"]?.GetValue<int>() == id);
        }

        public async ValueTask DisposeAsync()
        {
            socket.Abort();
            socket.Dispose();
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ServesTheInfoviewAndRelaysItToLean()
    {
        Lean.RequireLean();
        var ct = TestContext.Current.CancellationToken;
        string dir = Lean.Sample("Demo");
        await using var server = new LeanServer(new LeanServerCommand(Lean.Executable!, ["--server"], dir));
        await server.StartAsync(ct);
        string uri = LeanServer.UriOf(Path.Combine(dir, "Bridge.lean"));
        const string text = "theorem demo (p q : Prop) (hp : p) (hq : q) : p ∧ q := by\n  constructor\n  · exact hp\n  · exact hq\n";
        await server.OpenAsync(uri, text);
        await server.WaitForElaborationAsync(uri, ct).WaitAsync(Lean.Patience, ct);

        var editor = new Editor();
        await using var bridge = new InfoviewBridge(() => server, editor,
            path => path == "index.html" ? (Encoding.UTF8.GetBytes("<html>infoview</html>"), "text/html") : null);
        Uri page = bridge.Start();
        bridge.CursorMoved(uri, new Position(2, 4));

        // The page needs the token; the files do not.
        using var http = new HttpClient();
        Assert.Equal("<html>infoview</html>", await http.GetStringAsync(page, ct));
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync(new Uri(page.GetLeftPart(UriPartial.Path)), ct)).StatusCode);
        var stranger = new ClientWebSocket();
        await Assert.ThrowsAsync<WebSocketException>(() => stranger.ConnectAsync(new Uri($"ws://127.0.0.1:{page.Port}/ws?t=wrong"), ct));

        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{page.Port}");
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{page.Port}/ws?t={System.Web.HttpUtility.ParseQueryString(page.Query)["t"]}"), ct);
        await using var client = new Client(socket);

        // Ready: the page hears which server it has and where the cursor is.
        await client.SendAsync(new JsonObject { ["op"] = "ready" });
        JsonObject restarted = await client.ReceiveAsync(o => o["op"]?.GetValue<string>() == "serverRestarted");
        Assert.NotNull(restarted["result"]!["capabilities"]!["experimental"]!["rpcProvider"]);
        JsonObject init = await client.ReceiveAsync(o => o["op"]?.GetValue<string>() == "initialize");
        Assert.Equal(2, init["loc"]!["range"]!["start"]!["line"]!.GetValue<int>());

        // An RPC session, and the interactive goals through it, as the infoview asks for them.
        JsonObject session = await client.CallAsync("createRpcSession", new JsonObject { ["uri"] = uri });
        string sessionId = session["result"]!.GetValue<string>();
        JsonObject goals = await client.CallAsync("request", new JsonObject
        {
            ["uri"] = uri,
            ["method"] = "$/lean/rpc/call",
            ["params"] = new JsonObject
            {
                ["textDocument"] = new JsonObject { ["uri"] = uri },
                ["position"] = new JsonObject { ["line"] = 2, ["character"] = 4 },
                ["sessionId"] = sessionId,
                ["method"] = "Lean.Widget.getInteractiveGoals",
                ["params"] = new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = uri },
                    ["position"] = new JsonObject { ["line"] = 2, ["character"] = 4 },
                },
            },
        });
        Assert.Contains("hp", goals["result"]!.ToJsonString(), StringComparison.Ordinal);

        // A failing request comes back as the server's error.
        JsonObject bad = await client.CallAsync("request", new JsonObject { ["uri"] = uri, ["method"] = "no/such/method", ["params"] = new JsonObject() });
        Assert.NotNull(bad["error"]!["code"]);

        // Notifications the page subscribes to are relayed: the server's diagnostics, and this client's edits.
        await client.SendAsync(new JsonObject { ["op"] = "subscribeServer", ["method"] = "textDocument/publishDiagnostics" });
        await client.SendAsync(new JsonObject { ["op"] = "subscribeClient", ["method"] = "textDocument/didChange" });
        await Task.Delay(200, ct);
        await server.ChangeAsync(uri, text + "\ntheorem later : 1 = 2 := by sorry\n");
        JsonObject changed = await client.ReceiveAsync(o => o["op"]?.GetValue<string>() == "clientNotification");
        Assert.Equal("textDocument/didChange", changed["method"]!.GetValue<string>());
        JsonObject diags = await client.ReceiveAsync(o => o["op"]?.GetValue<string>() == "serverNotification"
            && o["params"]!.ToJsonString().Contains("sorry", StringComparison.Ordinal));
        Assert.Equal("textDocument/publishDiagnostics", diags["method"]!.GetValue<string>());

        // The cursor moving in the editor reaches the page.
        bridge.CursorMoved(uri, new Position(3, 4));
        JsonObject cursor = await client.ReceiveAsync(o => o["op"]?.GetValue<string>() == "cursor");
        Assert.Equal(3, cursor["loc"]!["range"]!["start"]!["line"]!.GetValue<int>());

        // What the infoview asks of the editor reaches it.
        await client.SendAsync(new JsonObject
        {
            ["op"] = "insertText", ["text"] = "exact hq", ["kind"] = "above",
            ["pos"] = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri }, ["position"] = new JsonObject { ["line"] = 3, ["character"] = 2 } },
        });
        await client.SendAsync(new JsonObject { ["op"] = "showDocument", ["show"] = new JsonObject { ["uri"] = uri, ["selection"] = new JsonObject { ["start"] = new JsonObject { ["line"] = 1, ["character"] = 0 }, ["end"] = new JsonObject { ["line"] = 1, ["character"] = 3 } } } });
        await client.SendAsync(new JsonObject { ["op"] = "copy", ["text"] = "⊢ p" });
        for (int i = 0; i < 50 && editor.Calls.Count < 3; i++)
        {
            await Task.Delay(50, ct);
        }
        Assert.Contains("insert above exact hq 3", editor.Calls);
        Assert.Contains("show Bridge.lean 1", editor.Calls);
        Assert.Contains("copy ⊢ p", editor.Calls);
        Assert.Equal(1, bridge.Connections);
    }
}
