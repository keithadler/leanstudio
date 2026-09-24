using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeanStudio.Lsp;

/// <summary>An error the other side returned in place of a result.</summary>
/// <param name="code">The JSON-RPC error code.</param>
/// <param name="message">The error message the other side sent.</param>
/// <param name="data">The error's <c>data</c>, or null when it had none.</param>
public sealed class JsonRpcException(int code, string message, JsonElement? data = null) : Exception(message)
{
    /// <summary>
    /// The JSON-RPC error code, such as <see cref="LeanServer.RpcNeedsReconnect"/> or
    /// <see cref="LeanServer.ContentModified"/>; 0 when the error had none.
    /// </summary>
    public int Code { get; } = code;
    /// <summary>The error's <c>data</c> member, or null when it had none.</summary>
    public JsonElement? ErrorData { get; } = data;
}

/// <summary>
/// JSON-RPC 2.0 with LSP's Content-Length framing, over a pair of streams. Requests the other side sends us
/// are answered by <see cref="RequestHandler"/>; when there is none, or it returns null, the answer is a null
/// result. Lean's server sends requests of its own (capability registration, refresh hints) and waits on them,
/// so answering every one matters even when the answer is empty.
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private long _nextId;
    private Task? _readLoop;

    /// <summary>Set up a connection. Nothing is read until <see cref="Start"/>.</summary>
    /// <param name="input">The stream messages arrive on (a server's stdout).</param>
    /// <param name="output">The stream messages are written to (a server's stdin).</param>
    public JsonRpcConnection(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    /// <summary>A notification arrived: method and params (params may be undefined). Raised on the read loop's thread.</summary>
    public event Action<string, JsonElement>? NotificationReceived;

    /// <summary>
    /// The input closed or could not be read; every pending request has been failed. The exception is null for a
    /// clean end of stream or a cancellation.
    /// </summary>
    public event Action<Exception?>? Closed;

    /// <summary>
    /// Every message on the connection as it is sent (<c>true</c>) or received (<c>false</c>), as JSON text: for a
    /// log of the conversation with the server, when troubleshooting it. Called on the sending or reading thread.
    /// </summary>
    public Action<bool, string>? Traffic { get; set; }

    /// <summary>
    /// Answers requests from the other side. Return null for a null result; an exception becomes an error response
    /// (code -32603). Called on a thread-pool thread, possibly for several requests at once.
    /// </summary>
    public Func<string, JsonElement, Task<JsonNode?>>? RequestHandler { get; set; }

    /// <summary>Start reading messages in the background. Calling it again does nothing.</summary>
    public void Start() => _readLoop ??= Task.Run(ReadLoopAsync);

    /// <summary>Send a request and wait for its result.</summary>
    /// <param name="method">The method name.</param>
    /// <param name="parameters">
    /// The params: a <see cref="JsonNode"/> or <see cref="JsonElement"/> is sent as it is (copied), anything else is
    /// serialized with camelCase names; null sends <c>"params": null</c>.
    /// </param>
    /// <param name="ct">Cancelling fails the request with <see cref="OperationCanceledException"/> and sends <c>$/cancelRequest</c>.</param>
    /// <returns>The result, or an undefined element when the response had none. Thread-safe.</returns>
    /// <exception cref="JsonRpcException">The other side answered with an error.</exception>
    /// <exception cref="IOException">The connection closed before the answer came.</exception>
    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken ct = default)
    {
        long id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = ToNode(parameters),
        };
        using CancellationTokenRegistration reg = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out TaskCompletionSource<JsonElement>? t))
            {
                t.TrySetCanceled(ct);
                _ = NotifyAsync("$/cancelRequest", new { id });
            }
        });
        try
        {
            await WriteAsync(msg).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Send a notification, which has no answer. Parameters are converted as for <see cref="RequestAsync"/>.</summary>
    /// <param name="method">The method name.</param>
    /// <param name="parameters">The params, or null for none.</param>
    /// <returns>A task that completes once the message has been written and flushed.</returns>
    public Task NotifyAsync(string method, object? parameters)
    {
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = ToNode(parameters),
        };
        return WriteAsync(msg);
    }

    /// <summary>A value as JSON: nodes are deep-copied, elements re-parsed, anything else serialized with the LSP options.</summary>
    internal static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode n => n.DeepClone(),
        JsonElement e => JsonNode.Parse(e.GetRawText()),
        _ => JsonSerializer.SerializeToNode(value, LspJson.Options),
    };

    private async Task WriteAsync(JsonObject msg)
    {
        string json = msg.ToJsonString();
        Traffic?.Invoke(true, json);
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _writeLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(header, _cts.Token).ConfigureAwait(false);
            await _output.WriteAsync(body, _cts.Token).ConfigureAwait(false);
            await _output.FlushAsync(_cts.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? error = null;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                byte[]? body = await ReadMessageAsync().ConfigureAwait(false);
                if (body is null)
                {
                    break;
                }
                Dispatch(body);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or FormatException)
        {
            error = e is OperationCanceledException ? null : e;
        }
        foreach (long id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out TaskCompletionSource<JsonElement>? t))
            {
                t.TrySetException(new IOException("the language server connection closed", error));
            }
        }
        Closed?.Invoke(error);
    }

    private void Dispatch(byte[] body)
    {
        Traffic?.Invoke(false, Encoding.UTF8.GetString(body));
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException)
        {
            return;
        }
        bool hasMethod = root.TryGetProperty("method", out JsonElement methodEl);
        bool hasId = root.TryGetProperty("id", out JsonElement idEl);
        JsonElement parameters = root.TryGetProperty("params", out JsonElement p) ? p : default;

        if (hasMethod && hasId)
        {
            _ = AnswerAsync(idEl.Clone(), methodEl.GetString() ?? "", parameters);
        }
        else if (hasMethod)
        {
            NotificationReceived?.Invoke(methodEl.GetString() ?? "", parameters);
        }
        else if (hasId && idEl.ValueKind == JsonValueKind.Number && _pending.TryRemove(idEl.GetInt64(), out TaskCompletionSource<JsonElement>? tcs))
        {
            if (root.TryGetProperty("error", out JsonElement err))
            {
                int code = err.TryGetProperty("code", out JsonElement c) ? c.GetInt32() : 0;
                string message = err.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "" : "";
                JsonElement? data = err.TryGetProperty("data", out JsonElement d) ? d.Clone() : null;
                tcs.TrySetException(new JsonRpcException(code, message, data));
            }
            else
            {
                tcs.TrySetResult(root.TryGetProperty("result", out JsonElement r) ? r.Clone() : default);
            }
        }
    }

    private async Task AnswerAsync(JsonElement id, string method, JsonElement parameters)
    {
        JsonNode? result = null;
        try
        {
            if (RequestHandler is not null)
            {
                result = await RequestHandler(method, parameters).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            var err = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(id.GetRawText()),
                ["error"] = new JsonObject { ["code"] = -32603, ["message"] = e.Message },
            };
            await WriteAsync(err).ConfigureAwait(false);
            return;
        }
        var msg = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["result"] = result,
        };
        try
        {
            await WriteAsync(msg).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private async Task<byte[]?> ReadMessageAsync()
    {
        int length = -1;
        while (true)
        {
            string? line = await ReadHeaderLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }
            if (line.Length == 0)
            {
                if (length >= 0)
                {
                    break;
                }
                continue;
            }
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                length = int.Parse(line[(colon + 1)..].Trim(), CultureInfo.InvariantCulture);
            }
        }
        byte[] body = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = await _input.ReadAsync(body.AsMemory(read, length - read), _cts.Token).ConfigureAwait(false);
            if (n == 0)
            {
                return null;
            }
            read += n;
        }
        return body;
    }

    private readonly byte[] _one = new byte[1];

    private async Task<string?> ReadHeaderLineAsync()
    {
        var sb = new StringBuilder();
        while (true)
        {
            int n = await _input.ReadAsync(_one, _cts.Token).ConfigureAwait(false);
            if (n == 0)
            {
                return sb.Length == 0 ? null : sb.ToString();
            }
            char c = (char)_one[0];
            if (c == '\n')
            {
                return sb.ToString().TrimEnd('\r');
            }
            sb.Append(c);
        }
    }

    /// <summary>Stop the read loop (waiting up to two seconds for it) and release the connection's resources. The streams are not closed.</summary>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
        _cts.Dispose();
        _writeLock.Dispose();
    }
}
