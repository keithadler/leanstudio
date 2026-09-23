using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LeanStudio.Core.Updates;

/// <summary>A newer release than the one running: its version, notes, page, and the download for this computer.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Name, string Notes, string PageUrl, string? AssetName, string? AssetUrl, long AssetSize)
{
    public bool HasDownload => AssetUrl is not null;
}

/// <summary>
/// Checks GitHub Releases for a newer Lean Studio. Releases are tagged <c>vX.Y.Z</c>, and each carries one build
/// per platform named <c>LeanStudio-X.Y.Z-&lt;runtime&gt;.zip|.tar.gz</c>, as the CI release job makes them.
/// Nothing is installed without the person asking: the check only reports, and the download goes to their
/// Downloads folder to open like any other.
/// </summary>
public sealed class UpdateChecker
{
    public const string DefaultRepository = "keithadler/leanstudio";

    private readonly HttpClient _http;
    private readonly string _repository;

    public UpdateChecker(HttpClient? http = null, string repository = DefaultRepository)
    {
        _repository = repository;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LeanStudio", CurrentVersion.ToString(3)));
        }
    }

    public static Version CurrentVersion =>
        typeof(UpdateChecker).Assembly.GetName().Version is Version v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>The runtime this build is for, as release file names spell it (osx-arm64, win-x64, linux-x64…).</summary>
    public static string CurrentRuntime
    {
        get
        {
            string os = OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win" : "linux";
            string arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            };
            return os + "-" + arch;
        }
    }

    /// <summary>Parse a tag like v0.2.0 or 0.2.0-beta into its version, ignoring any pre-release suffix.</summary>
    public static Version? ParseTag(string tag)
    {
        string t = tag.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (!Version.TryParse(t, out Version? v))
        {
            return null;
        }
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>The newest release if it is newer than <paramref name="current"/>; null when up to date.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version? current = null, string? runtime = null, CancellationToken ct = default)
    {
        using HttpResponseMessage r = await _http.GetAsync($"https://api.github.com/repos/{_repository}/releases/latest", ct).ConfigureAwait(false);
        if (r.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null; // no releases yet
        }
        r.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return Evaluate(doc.RootElement, current ?? CurrentVersion, runtime ?? CurrentRuntime);
    }

    /// <summary>Decide from a release's JSON whether it is an update, and which file is for this runtime.</summary>
    public static UpdateInfo? Evaluate(JsonElement release, Version current, string runtime)
    {
        if (release.TryGetProperty("draft", out JsonElement d) && d.ValueKind == JsonValueKind.True
            || release.TryGetProperty("prerelease", out JsonElement p) && p.ValueKind == JsonValueKind.True)
        {
            return null;
        }
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (ParseTag(tag) is not Version latest || latest <= current)
        {
            return null;
        }
        string? assetName = null, assetUrl = null;
        long size = 0;
        if (release.TryGetProperty("assets", out JsonElement assets))
        {
            foreach (JsonElement a in assets.EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (name.Contains("-" + runtime + ".", StringComparison.OrdinalIgnoreCase))
                {
                    assetName = name;
                    assetUrl = a.GetProperty("browser_download_url").GetString();
                    size = a.TryGetProperty("size", out JsonElement s) ? s.GetInt64() : 0;
                    break;
                }
            }
        }
        return new UpdateInfo(
            latest,
            tag,
            release.TryGetProperty("name", out JsonElement n) && n.GetString() is { Length: > 0 } nm ? nm : tag,
            release.TryGetProperty("body", out JsonElement b) ? b.GetString() ?? "" : "",
            release.TryGetProperty("html_url", out JsonElement h) ? h.GetString() ?? "" : $"https://github.com/{DefaultRepository}/releases",
            assetName,
            assetUrl,
            size);
    }

    public static string DownloadsFolder
    {
        get
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string downloads = Path.Combine(home, "Downloads");
            return Directory.Exists(downloads) ? downloads : home;
        }
    }

    /// <summary>Download the release file for this computer into the Downloads folder; returns its path.</summary>
    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, string? folder = null, CancellationToken ct = default)
    {
        if (update.AssetUrl is null || update.AssetName is null)
        {
            throw new InvalidOperationException("this release has no build for " + CurrentRuntime);
        }
        string target = Path.Combine(folder ?? DownloadsFolder, update.AssetName);
        string partial = target + ".part";
        using (HttpResponseMessage r = await _http.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            r.EnsureSuccessStatusCode();
            long total = r.Content.Headers.ContentLength ?? update.AssetSize;
            await using Stream src = await r.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using FileStream dst = File.Create(partial);
            byte[] buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total > 0)
                {
                    progress?.Report((double)done / total);
                }
            }
        }
        File.Move(partial, target, overwrite: true);
        return target;
    }
}
