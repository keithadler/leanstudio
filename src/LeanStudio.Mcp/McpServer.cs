using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeanStudio.Mcp;

/// <summary>A tool an MCP client can call: its name, what it is for, the JSON Schema of its arguments, and the code.</summary>
public sealed record McpTool(string Name, string Description, JsonObject InputSchema, Func<JsonObject, CancellationToken, Task<string>> Run);

/// <summary>A tool failed in a way the caller should read (bad arguments, Lean not installed), not a protocol error.</summary>
public sealed class ToolException(string message) : Exception(message);

/// <summary>
/// The Model Context Protocol over stdio: newline-delimited JSON-RPC 2.0. Handles the lifecycle (initialize,
/// ping), tool listing and tool calls; everything Lean-specific lives in the tools. Nothing but protocol messages
/// is ever written to the output stream: logs go to stderr, where MCP clients expect them.
/// </summary>
public sealed class McpServer
{
    public static readonly string[] SupportedProtocolVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];

    private readonly IReadOnlyList<McpTool> _tools;
    private readonly string _name;
    private readonly string _version;
    private readonly string _instructions;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public McpServer(string name, string version, string instructions, IReadOnlyList<McpTool> tools)
    {
        _name = name;
        _version = version;
        _instructions = instructions;
        _tools = tools;
    }

    public IReadOnlyList<McpTool> Tools => _tools;

    /// <summary>Serve until the input ends. Requests run concurrently, so a long build does not block a quick question.</summary>
    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        var running = new List<Task>();
        while (!ct.IsCancellationRequested)
        {
            string? line = await input.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }
            if (line.Trim().Length == 0)
            {
                continue;
            }
            running.RemoveAll(t => t.IsCompleted);
            running.Add(HandleLineAsync(line, output, ct));
        }
        await Task.WhenAll(running).ConfigureAwait(false);
    }

    private async Task HandleLineAsync(string line, TextWriter output, CancellationToken ct)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            await WriteAsync(output, Error(null, -32700, "parse error")).ConfigureAwait(false);
            return;
        }
        if (node is JsonArray batch)
        {
            foreach (JsonNode? item in batch)
            {
                if (item is JsonObject o && await HandleAsync(o, ct).ConfigureAwait(false) is JsonObject r)
                {
                    await WriteAsync(output, r).ConfigureAwait(false);
                }
            }
            return;
        }
        if (node is JsonObject msg && await HandleAsync(msg, ct).ConfigureAwait(false) is JsonObject response)
        {
            await WriteAsync(output, response).ConfigureAwait(false);
        }
    }

    /// <summary>Handle one message; returns the response, or null for notifications and responses.</summary>
    public async Task<JsonObject?> HandleAsync(JsonObject msg, CancellationToken ct = default)
    {
        JsonNode? id = msg["id"]?.DeepClone();
        string? method = msg["method"]?.GetValue<string>();
        if (method is null)
        {
            return null; // a response to something we never send
        }
        bool isNotification = !msg.ContainsKey("id");
        JsonObject parameters = msg["params"] as JsonObject ?? new JsonObject();
        try
        {
            JsonObject? result = method switch
            {
                "initialize" => Initialize(parameters),
                "ping" => new JsonObject(),
                "tools/list" => ListTools(),
                "tools/call" => await CallToolAsync(parameters, ct).ConfigureAwait(false),
                "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                _ when method.StartsWith("notifications/", StringComparison.Ordinal) => null,
                _ => throw new MethodNotFoundException(method),
            };
            if (isNotification || result is null)
            {
                return null;
            }
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (MethodNotFoundException)
        {
            return isNotification ? null : Error(id, -32601, $"method not found: {method}");
        }
        catch (ArgumentException e)
        {
            return isNotification ? null : Error(id, -32602, e.Message);
        }
    }

    private sealed class MethodNotFoundException(string method) : Exception(method);

    private JsonObject Initialize(JsonObject p)
    {
        string requested = p["protocolVersion"]?.GetValue<string>() ?? SupportedProtocolVersions[0];
        string version = SupportedProtocolVersions.Contains(requested) ? requested : SupportedProtocolVersions[0];
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject { ["name"] = _name, ["title"] = "Lean Studio", ["version"] = _version },
            ["instructions"] = _instructions,
        };
    }

    private JsonObject ListTools()
    {
        var arr = new JsonArray();
        foreach (McpTool t in _tools)
        {
            arr.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = t.InputSchema.DeepClone(),
            });
        }
        return new JsonObject { ["tools"] = arr };
    }

    private async Task<JsonObject> CallToolAsync(JsonObject p, CancellationToken ct)
    {
        string name = p["name"]?.GetValue<string>() ?? throw new ArgumentException("tools/call needs a tool name");
        McpTool tool = _tools.FirstOrDefault(t => t.Name == name) ?? throw new ArgumentException($"unknown tool: {name}");
        JsonObject args = p["arguments"] as JsonObject ?? new JsonObject();
        string text;
        bool isError = false;
        try
        {
            text = await tool.Run(args, ct).ConfigureAwait(false);
        }
        catch (ToolException e)
        {
            text = e.Message;
            isError = true;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException
                                  or Lsp.JsonRpcException or Tenet.Kernel.KernelException or Tenet.Olean.OleanFormatException
                                  or KeyNotFoundException or FormatException)
        {
            text = $"{name} failed: {e.Message}";
            isError = true;
        }
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError,
        };
    }

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private async Task WriteAsync(TextWriter output, JsonObject msg)
    {
        string s = msg.ToJsonString(); // one line: the stdio transport forbids embedded newlines
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await output.WriteLineAsync(s).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Run on the process's stdin and stdout, with UTF-8 both ways and nothing buffered.</summary>
    public static async Task RunStdioAsync(McpServer server, CancellationToken ct = default)
    {
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        await server.RunAsync(input, output, ct).ConfigureAwait(false);
    }
}
