using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeanStudio.Core.Ai;

/// <summary>The HTTP plumbing the model clients share: one client, streaming reads, and errors in plain words.</summary>
internal static class AiHttp
{
    /// <summary>
    /// One client for every model. Answers stream for as long as the model writes, so there is no overall timeout;
    /// connecting has a short one, so a server that is not running is noticed at once.
    /// </summary>
    public static HttpClient Shared { get; } = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    public static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    /// <summary>Send a request, reading only the headers, and turn failures into an <see cref="AiException"/>.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, string who, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new AiException($"Could not reach {who}: {e.Message}", e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new AiException($"{who} did not answer in time.", e);
        }
        if (!response.IsSuccessStatusCode)
        {
            string body = "";
            try
            {
                body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
            }
            response.Dispose();
            throw new AiException(Explain(who, response.StatusCode, body));
        }
        return response;
    }

    /// <summary>A failed request's status and error body, as a sentence.</summary>
    public static string Explain(string who, HttpStatusCode status, string body)
    {
        string detail = ErrorMessage(body) ?? (body.Length > 300 ? body[..300] + "…" : body).Trim();
        string what = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"{who} refused the API key",
            HttpStatusCode.NotFound => $"{who} does not have that model (or that address is not an API)",
            HttpStatusCode.TooManyRequests => $"{who} says too many requests: wait a moment and try again",
            _ when (int)status >= 500 => $"{who} had a problem ({(int)status})",
            _ => $"{who} turned the request down ({(int)status})",
        };
        return detail.Length > 0 ? $"{what}: {detail}" : what + ".";
    }

    private static string? ErrorMessage(string body)
    {
        try
        {
            JsonNode? n = JsonNode.Parse(body);
            JsonNode? e = n?["error"];
            return e switch
            {
                JsonValue v => v.ToString(),
                JsonObject o => o["message"]?.ToString(),
                _ => n?["message"]?.ToString(),
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The lines of a streamed body as they arrive.</summary>
    public static async IAsyncEnumerable<string> LinesAsync(HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct)
    {
        await using Stream s = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(s, Encoding.UTF8);
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            }
            catch (IOException e)
            {
                throw new AiException("The connection to the model was lost: " + e.Message, e);
            }
            if (line is null)
            {
                yield break;
            }
            yield return line;
        }
    }

    public static JsonNode? TryParse(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsJson(HttpResponseMessage r) =>
        r.Content.Headers.ContentType?.MediaType is string m && m.Contains("json", StringComparison.OrdinalIgnoreCase)
        && !m.Contains("ndjson", StringComparison.OrdinalIgnoreCase) && !m.Contains("stream", StringComparison.OrdinalIgnoreCase);

    public static string RoleName(ChatRole r) => r switch
    {
        ChatRole.System => "system",
        ChatRole.Assistant => "assistant",
        _ => "user",
    };
}

/// <summary>
/// A model behind the OpenAI chat-completions API (<c>POST …/chat/completions</c>), which nearly every local server
/// speaks: Apple's <c>fm serve</c>, LM Studio, llama.cpp's <c>llama-server</c>, MLX's <c>mlx_lm.server</c>, vLLM, and
/// cloud services such as OpenAI, Gemini, OpenRouter and Groq.
/// </summary>
public sealed class OpenAiCompatibleModel : IChatModel
{
    private readonly HttpClient _http;

    /// <summary>A model at <paramref name="baseUrl"/> (the part before <c>/chat/completions</c>, usually ending <c>/v1</c>).</summary>
    /// <param name="displayName">The name people see.</param>
    /// <param name="baseUrl">Such as <c>http://127.0.0.1:1234/v1</c>.</param>
    /// <param name="model">The model's id on that server.</param>
    /// <param name="location">Where it runs.</param>
    /// <param name="contextTokens">How many tokens it sees at once.</param>
    /// <param name="apiKey">Sent as a bearer token when there is one.</param>
    /// <param name="http">The client to use; a shared one by default.</param>
    public OpenAiCompatibleModel(string displayName, string baseUrl, string model, AiLocation location, int contextTokens, string? apiKey = null, HttpClient? http = null)
    {
        DisplayName = displayName;
        BaseUrl = baseUrl.TrimEnd('/');
        Model = model;
        Location = location;
        ContextTokens = contextTokens;
        ApiKey = apiKey;
        _http = http ?? AiHttp.Shared;
    }

    /// <inheritdoc/>
    public string DisplayName { get; }

    /// <inheritdoc/>
    public AiLocation Location { get; }

    /// <inheritdoc/>
    public int ContextTokens { get; }

    /// <summary>The API's address, without a trailing slash.</summary>
    public string BaseUrl { get; }

    /// <summary>The model's id on the server.</summary>
    public string Model { get; }

    /// <summary>The API key, if the server needs one.</summary>
    public string? ApiKey { get; }

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, ChatOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await SendAsync(messages, options, _plain, ct).ConfigureAwait(false);
        }
        catch (AiException) when (!_plain)
        {
            // Some servers refuse settings they do not know (a length limit, a temperature): ask again with none,
            // and from then on.
            response = await SendAsync(messages, options, true, ct).ConfigureAwait(false);
            _plain = true;
        }
        using HttpResponseMessage _ = response;
        if (AiHttp.IsJson(response))
        {
            // A server that ignores "stream" answers all at once.
            string all = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonNode? n = AiHttp.TryParse(all);
            string? text = n?["choices"]?[0]?["message"]?["content"]?.ToString();
            if (text is null)
            {
                throw new AiException($"{DisplayName} answered in a form Lean Studio does not recognize.");
            }
            yield return text;
            yield break;
        }
        await foreach (string line in AiHttp.LinesAsync(response, ct).ConfigureAwait(false))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }
            string data = line[5..].Trim();
            if (data == "[DONE]")
            {
                yield break;
            }
            JsonNode? n = AiHttp.TryParse(data);
            if (n?["error"] is JsonNode err)
            {
                throw new AiException($"{DisplayName} stopped with an error: {err["message"]?.ToString() ?? err.ToString()}");
            }
            if (n?["choices"]?[0]?["delta"]?["content"]?.ToString() is { Length: > 0 } piece)
            {
                yield return piece;
            }
        }
    }

    private bool _plain;

    private async Task<HttpResponseMessage> SendAsync(IReadOnlyList<ChatMessage> messages, ChatOptions options, bool plain, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = AiHttp.RoleName(m.Role), ["content"] = m.Text }).ToArray()),
            // Apple's fm serve streams unless told otherwise, and OpenAI does not: say which, always.
            ["stream"] = true,
        };
        if (!plain)
        {
            body["max_tokens"] = options.MaxTokens;
            body["temperature"] = options.Temperature;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/chat/completions") { Content = AiHttp.Json(body) };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (ApiKey is { Length: > 0 } key)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        return await AiHttp.SendAsync(_http, request, DisplayName, ct).ConfigureAwait(false);
    }

    /// <summary>The ids of the models a server offers (<c>GET …/models</c>); empty when it cannot say.</summary>
    public static async Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string? apiKey = null, HttpClient? http = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl.TrimEnd('/') + "/models");
        if (apiKey is { Length: > 0 })
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
        try
        {
            using HttpResponseMessage r = await (http ?? AiHttp.Shared).SendAsync(request, ct).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode)
            {
                return [];
            }
            JsonNode? n = AiHttp.TryParse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return n?["data"] is JsonArray a
                ? a.Select(m => m?["id"]?.ToString()).OfType<string>().Where(s => s.Length > 0).ToList()
                : [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }
}

/// <summary>A model served by Ollama, through its own API so the context window can be set (its default is small).</summary>
public sealed class OllamaModel : IChatModel
{
    private readonly HttpClient _http;

    /// <summary>The Ollama model <paramref name="model"/> at <paramref name="baseUrl"/> (such as <c>http://127.0.0.1:11434</c>).</summary>
    public OllamaModel(string baseUrl, string model, int contextTokens = 8192, HttpClient? http = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        Model = model;
        ContextTokens = contextTokens;
        _http = http ?? AiHttp.Shared;
    }

    /// <summary>Ollama's address.</summary>
    public string BaseUrl { get; }

    /// <summary>The model's name in Ollama, such as <c>qwen3:8b</c>.</summary>
    public string Model { get; }

    /// <inheritdoc/>
    public string DisplayName => "Ollama · " + Model;

    /// <inheritdoc/>
    public AiLocation Location => AiLocation.Local;

    /// <inheritdoc/>
    public int ContextTokens { get; }

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, ChatOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = AiHttp.RoleName(m.Role), ["content"] = m.Text }).ToArray()),
            ["stream"] = true,
            ["options"] = new JsonObject
            {
                ["num_ctx"] = ContextTokens,
                ["num_predict"] = options.MaxTokens,
                ["temperature"] = options.Temperature,
            },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/api/chat") { Content = AiHttp.Json(body) };
        using HttpResponseMessage response = await AiHttp.SendAsync(_http, request, "Ollama", ct).ConfigureAwait(false);
        await foreach (string line in AiHttp.LinesAsync(response, ct).ConfigureAwait(false))
        {
            if (line.Length == 0 || AiHttp.TryParse(line) is not JsonNode n)
            {
                continue;
            }
            if (n["error"] is JsonNode err)
            {
                throw new AiException("Ollama stopped with an error: " + err);
            }
            if (n["message"]?["content"]?.ToString() is { Length: > 0 } piece)
            {
                yield return piece;
            }
            if (n["done"]?.GetValueKind() == JsonValueKind.True)
            {
                yield break;
            }
        }
    }
}

/// <summary>A Claude model through Anthropic's Messages API. The prompt, with the code in it, goes to Anthropic.</summary>
public sealed class AnthropicModel : IChatModel
{
    /// <summary>The model used when none is chosen.</summary>
    public const string DefaultModel = "claude-sonnet-5";

    /// <summary>The API's address.</summary>
    public const string DefaultBaseUrl = "https://api.anthropic.com";

    private readonly HttpClient _http;
    private readonly string _apiKey;

    /// <summary>The Claude model <paramref name="model"/>, with <paramref name="apiKey"/>.</summary>
    public AnthropicModel(string apiKey, string? model = null, string? baseUrl = null, HttpClient? http = null)
    {
        _apiKey = apiKey;
        Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        BaseUrl = (baseUrl ?? DefaultBaseUrl).TrimEnd('/');
        _http = http ?? AiHttp.Shared;
    }

    /// <summary>The model's id, such as <c>claude-sonnet-5</c>.</summary>
    public string Model { get; }

    /// <summary>The API's address, without a trailing slash.</summary>
    public string BaseUrl { get; }

    /// <inheritdoc/>
    public string DisplayName => "Claude · " + Model;

    /// <inheritdoc/>
    public AiLocation Location => AiLocation.Cloud;

    /// <inheritdoc/>
    public int ContextTokens => 200_000;

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, ChatOptions options, [EnumeratorCancellation] CancellationToken ct = default)
    {
        string system = string.Join("\n\n", messages.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        var turns = new JsonArray();
        foreach (ChatMessage m in messages.Where(m => m.Role != ChatRole.System))
        {
            turns.Add(new JsonObject { ["role"] = AiHttp.RoleName(m.Role), ["content"] = m.Text });
        }
        var body = new JsonObject
        {
            ["model"] = Model,
            ["max_tokens"] = options.MaxTokens,
            ["stream"] = true,
            ["messages"] = turns,
        };
        if (system.Length > 0)
        {
            body["system"] = system;
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/v1/messages") { Content = AiHttp.Json(body) };
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        using HttpResponseMessage response = await AiHttp.SendAsync(_http, request, "Anthropic", ct).ConfigureAwait(false);
        await foreach (string line in AiHttp.LinesAsync(response, ct).ConfigureAwait(false))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal) || AiHttp.TryParse(line[5..].Trim()) is not JsonNode n)
            {
                continue;
            }
            switch (n["type"]?.ToString())
            {
                case "content_block_delta" when (n["delta"]?["type"]?.ToString() == "text_delta"):
                    if (n["delta"]?["text"]?.ToString() is { Length: > 0 } piece)
                    {
                        yield return piece;
                    }
                    break;
                case "error":
                    throw new AiException("Anthropic stopped with an error: " + (n["error"]?["message"]?.ToString() ?? n.ToString()));
                case "message_stop":
                    yield break;
            }
        }
    }
}
