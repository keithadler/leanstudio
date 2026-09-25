using System.Text.Json.Nodes;

namespace LeanStudio.Core.Ai;

/// <summary>The kinds of model Lean Studio can use, local ones first.</summary>
public static class AiProviders
{
    /// <summary>Choose for me: the best local model that is running, else a cloud one with a key, if allowed.</summary>
    public const string Auto = "auto";
    /// <summary>Apple's on-device model (macOS 27).</summary>
    public const string Apple = "apple";
    /// <summary>Ollama, on port 11434.</summary>
    public const string Ollama = "ollama";
    /// <summary>LM Studio's server, on port 1234.</summary>
    public const string LmStudio = "lmstudio";
    /// <summary>llama.cpp's <c>llama-server</c> or MLX's <c>mlx_lm.server</c>, on port 8080.</summary>
    public const string LlamaCpp = "llamacpp";
    /// <summary>Any OpenAI-compatible API at an address the person gives (local, or a cloud service with a key).</summary>
    public const string Custom = "custom";
    /// <summary>Claude, through Anthropic's API, with the person's key.</summary>
    public const string Anthropic = "anthropic";

    /// <summary>The order <see cref="Auto"/> tries them in: on the device, then local servers, then the cloud.</summary>
    public static IReadOnlyList<string> Preference { get; } = [Apple, Ollama, LmStudio, LlamaCpp, Custom, Anthropic];

    /// <summary>A provider's name for people.</summary>
    public static string Name(string id) => id switch
    {
        Apple => "Apple on-device model",
        Ollama => "Ollama",
        LmStudio => "LM Studio",
        LlamaCpp => "llama.cpp / MLX server",
        Custom => "Custom OpenAI-compatible server",
        Anthropic => "Claude (Anthropic API)",
        _ => "Automatic",
    };
}

/// <summary>What the person chose in AI ▸ Choose a Model, as the app keeps it in its settings.</summary>
public sealed record AiConfig
{
    /// <summary>The provider, one of <see cref="AiProviders"/>; <see cref="AiProviders.Auto"/> picks.</summary>
    public string Provider { get; init; } = AiProviders.Auto;

    /// <summary>The model to use with it; empty picks the best one it has.</summary>
    public string Model { get; init; } = "";

    /// <summary>The address of a custom OpenAI-compatible API, such as <c>http://127.0.0.1:8000/v1</c>.</summary>
    public string CustomUrl { get; init; } = "";

    /// <summary>
    /// Let <see cref="AiProviders.Auto"/> use a cloud model when no local one is running. Off by default: code only
    /// leaves the computer when the person has chosen a cloud model or allowed this.
    /// </summary>
    public bool AllowCloud { get; init; }

    /// <summary>Ollama's address.</summary>
    public string OllamaUrl { get; init; } = "http://127.0.0.1:11434";

    /// <summary>LM Studio's API.</summary>
    public string LmStudioUrl { get; init; } = "http://127.0.0.1:1234/v1";

    /// <summary>llama.cpp's or MLX's API.</summary>
    public string LlamaCppUrl { get; init; } = "http://127.0.0.1:8080/v1";
}

/// <summary>One provider as discovery found it: whether it can be used, what it has, and why not.</summary>
/// <param name="Id">One of <see cref="AiProviders"/>.</param>
/// <param name="Available">It can answer now.</param>
/// <param name="Detail">A few words: its models, or why it cannot be used.</param>
/// <param name="Models">The models it offers, best for Lean first; empty for one with a single model.</param>
/// <param name="Location">Where it runs.</param>
public sealed record AiCandidate(string Id, bool Available, string Detail, IReadOnlyList<string> Models, AiLocation Location)
{
    /// <summary>The provider's name for people.</summary>
    public string Name => AiProviders.Name(Id);

    /// <summary>A line for a list: the name, and what it has or why it cannot be used.</summary>
    public string Label => $"{Name}: {Detail}";
}

/// <summary>
/// Finds the models this computer can use, and makes the one the person chose (or the best one) ready to answer.
/// Local servers are probed at their usual addresses with a short timeout, all at once, so looking takes about a
/// second even when none is running.
/// </summary>
public sealed class AiDiscovery
{
    private readonly SecretStore _secrets;
    private readonly HttpClient _http;

    /// <summary>Discovery that reads API keys from <paramref name="secrets"/>.</summary>
    public AiDiscovery(SecretStore secrets, HttpClient? http = null)
    {
        _secrets = secrets;
        _http = http ?? AiHttp.Shared;
    }

    /// <summary>
    /// Model names that suggest a model is good at Lean, most specific first: Lean provers, then models trained for
    /// maths or code, then strong general models. A model matching an earlier word is preferred.
    /// </summary>
    public static IReadOnlyList<string> LeanAffinity { get; } =
    [
        "prover", "kimina", "goedel", "lean", "deepseek-r1", "qwq", "qwen3-coder", "qwen2.5-coder", "coder", "math",
        "qwen3", "gpt-oss", "deepseek", "qwen2.5", "gemma3", "llama3", "mistral", "phi",
    ];

    /// <summary><paramref name="models"/> ordered best for Lean first (embedding models, which cannot chat, left out).</summary>
    public static IReadOnlyList<string> Rank(IEnumerable<string> models) =>
        models
            .Where(m => !m.Contains("embed", StringComparison.OrdinalIgnoreCase) && !m.Contains("whisper", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .Select((m, i) => (m, i, rank: Affinity(m)))
            .OrderBy(t => t.rank).ThenBy(t => t.i)
            .Select(t => t.m)
            .ToList();

    private static int Affinity(string model)
    {
        string m = model.ToLowerInvariant();
        for (int i = 0; i < LeanAffinity.Count; i++)
        {
            if (m.Contains(LeanAffinity[i], StringComparison.Ordinal))
            {
                return i;
            }
        }
        return LeanAffinity.Count;
    }

    /// <summary>Every provider, local ones first, with whether it can be used now.</summary>
    public async Task<IReadOnlyList<AiCandidate>> FindAsync(AiConfig config, CancellationToken ct = default)
    {
        Task<AiCandidate>[] probes =
        [
            AppleAsync(ct),
            OllamaAsync(config.OllamaUrl, ct),
            OpenAiServerAsync(AiProviders.LmStudio, config.LmStudioUrl, ct),
            OpenAiServerAsync(AiProviders.LlamaCpp, config.LlamaCppUrl, ct),
            CustomAsync(config, ct),
            AnthropicAsync(ct),
        ];
        return await Task.WhenAll(probes).ConfigureAwait(false);
    }

    /// <summary>
    /// The model to use: the chosen provider (and model) when it is available, or with <see cref="AiProviders.Auto"/>
    /// the first available local one, then a cloud one if <see cref="AiConfig.AllowCloud"/>.
    /// </summary>
    /// <returns>The model, or null with the reason none can be used.</returns>
    public async Task<(IChatModel? Model, string Why)> ChooseAsync(AiConfig config, CancellationToken ct = default)
    {
        IReadOnlyList<AiCandidate> found = await FindAsync(config, ct).ConfigureAwait(false);
        AiCandidate? pick;
        if (config.Provider != AiProviders.Auto)
        {
            pick = found.FirstOrDefault(c => c.Id == config.Provider);
            if (pick is not { Available: true })
            {
                return (null, $"{AiProviders.Name(config.Provider)} is not available: {pick?.Detail ?? "unknown provider"}.");
            }
        }
        else
        {
            pick = AiProviders.Preference
                .Select(id => found.First(c => c.Id == id))
                .FirstOrDefault(c => c.Available && (c.Location != AiLocation.Cloud || config.AllowCloud));
            if (pick is null)
            {
                bool cloudWaiting = found.Any(c => c.Available && c.Location == AiLocation.Cloud);
                return (null, "No local model is running. " + (cloudWaiting
                    ? "A cloud model has a key: allow cloud models in AI ▸ Choose a Model to use it."
                    : Suggestion()));
            }
        }
        string model = config.Provider == pick.Id && config.Model.Length > 0 ? config.Model : pick.Models.FirstOrDefault() ?? "";
        IChatModel? m = await CreateAsync(pick.Id, model, config, ct).ConfigureAwait(false);
        return m is null ? (null, $"{pick.Name} could not be started.") : (m, "");
    }

    /// <summary>What to install for local AI on this computer.</summary>
    public static string Suggestion() =>
        OperatingSystem.IsMacOS()
            ? "On macOS 27, turn on Apple Intelligence to use Apple's on-device model; or install Ollama (ollama.com) or LM Studio and download a model."
            : "Install Ollama (ollama.com) or LM Studio and download a model, or add a cloud model's key in AI ▸ Choose a Model.";

    /// <summary>The model <paramref name="model"/> of provider <paramref name="id"/>; null if it needs a key it does not have.</summary>
    public async Task<IChatModel?> CreateAsync(string id, string model, AiConfig config, CancellationToken ct = default)
    {
        switch (id)
        {
            case AiProviders.Apple:
                return await AppleIntelligence.ModelAsync(ct).ConfigureAwait(false);
            case AiProviders.Ollama:
                return new OllamaModel(config.OllamaUrl, model, http: _http);
            case AiProviders.LmStudio:
                return new OpenAiCompatibleModel("LM Studio · " + model, config.LmStudioUrl, model, AiLocation.Local, 8192, http: _http);
            case AiProviders.LlamaCpp:
                return new OpenAiCompatibleModel("llama.cpp · " + (model.Length > 0 ? model : "model"), config.LlamaCppUrl, model.Length > 0 ? model : "default", AiLocation.Local, 8192, http: _http);
            case AiProviders.Custom:
                string? key = await _secrets.GetAsync("custom", ct).ConfigureAwait(false);
                bool cloud = !IsLocalAddress(config.CustomUrl);
                return new OpenAiCompatibleModel((cloud ? "" : "Local · ") + (model.Length > 0 ? model : config.CustomUrl), config.CustomUrl, model.Length > 0 ? model : "default",
                    cloud ? AiLocation.Cloud : AiLocation.Local, cloud ? 128_000 : 8192, key, _http);
            case AiProviders.Anthropic:
                return await _secrets.GetAsync("anthropic", ct).ConfigureAwait(false) is string k ? new AnthropicModel(k, model, http: _http) : null;
            default:
                return null;
        }
    }

    /// <summary>Whether <paramref name="url"/> is on this computer or a private network, so code sent there stays local.</summary>
    public static bool IsLocalAddress(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? u))
        {
            return false;
        }
        if (u.IsLoopback || u.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (System.Net.IPAddress.TryParse(u.Host, out System.Net.IPAddress? ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            byte[] b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] < 128);
        }
        return false;
    }

    private static async Task<AiCandidate> AppleAsync(CancellationToken ct)
    {
        (bool ready, string detail) = await AppleIntelligence.CheckAsync(ct).ConfigureAwait(false);
        return new AiCandidate(AiProviders.Apple, ready, ready ? "on this Mac, private, 4K context" : detail, [], AiLocation.OnDevice);
    }

    private async Task<AiCandidate> OllamaAsync(string url, CancellationToken ct)
    {
        JsonNode? tags = await GetJsonAsync(url.TrimEnd('/') + "/api/tags", ct).ConfigureAwait(false);
        if (tags?["models"] is not JsonArray a)
        {
            return new AiCandidate(AiProviders.Ollama, false, "not running", [], AiLocation.Local);
        }
        IReadOnlyList<string> models = Rank(a.Select(m => m?["name"]?.ToString()).OfType<string>());
        return models.Count == 0
            ? new AiCandidate(AiProviders.Ollama, false, "running, but no model downloaded (try `ollama pull qwen3`)", [], AiLocation.Local)
            : new AiCandidate(AiProviders.Ollama, true, Count(models), models, AiLocation.Local);
    }

    private async Task<AiCandidate> OpenAiServerAsync(string id, string url, CancellationToken ct)
    {
        JsonNode? list = await GetJsonAsync(url.TrimEnd('/') + "/models", ct).ConfigureAwait(false);
        if (list?["data"] is not JsonArray a)
        {
            return new AiCandidate(id, false, "not running", [], AiLocation.Local);
        }
        IReadOnlyList<string> models = Rank(a.Select(m => m?["id"]?.ToString()).OfType<string>());
        return models.Count == 0 && id == AiProviders.LmStudio
            ? new AiCandidate(id, false, "running, but no model loaded", [], AiLocation.Local)
            : new AiCandidate(id, true, models.Count == 0 ? "running" : Count(models), models, AiLocation.Local);
    }

    private async Task<AiCandidate> CustomAsync(AiConfig config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.CustomUrl))
        {
            return new AiCandidate(AiProviders.Custom, false, "no address set", [], AiLocation.Local);
        }
        AiLocation where = IsLocalAddress(config.CustomUrl) ? AiLocation.Local : AiLocation.Cloud;
        string? key = await _secrets.GetAsync("custom", ct).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        IReadOnlyList<string> models;
        try
        {
            models = Rank(await OpenAiCompatibleModel.ListModelsAsync(config.CustomUrl, key, _http, cts.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            models = [];
        }
        if (models.Count == 0 && config.Model.Length == 0)
        {
            return new AiCandidate(AiProviders.Custom, false, "did not answer at " + config.CustomUrl, [], where);
        }
        return new AiCandidate(AiProviders.Custom, true, models.Count > 0 ? Count(models) : config.Model, models, where);
    }

    private async Task<AiCandidate> AnthropicAsync(CancellationToken ct)
    {
        bool has = await _secrets.GetAsync("anthropic", ct).ConfigureAwait(false) is not null;
        return new AiCandidate(AiProviders.Anthropic, has, has ? "key set; sends your code to Anthropic" : "no API key", has ? [AnthropicModel.DefaultModel, "claude-opus-5-5", "claude-haiku-4-5-20251001"] : [], AiLocation.Cloud);
    }

    private static string Count(IReadOnlyList<string> models) =>
        models.Count == 1 ? models[0] : $"{models.Count} models, {models[0]} first";

    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(1500));
        try
        {
            using HttpResponseMessage r = await _http.GetAsync(url, cts.Token).ConfigureAwait(false);
            return r.IsSuccessStatusCode ? AiHttp.TryParse(await r.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false)) : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
