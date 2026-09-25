using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LeanStudio.Core.Updates;

/// <summary>A newer release than the one running: its version, notes, page, and the download for this computer.</summary>
/// <param name="Version">The release's version, from its tag, with any pre-release suffix dropped.</param>
/// <param name="Tag">The Git tag as written, e.g. <c>v0.2.0</c>.</param>
/// <param name="Name">The release's title, or the tag when it has none.</param>
/// <param name="Notes">The release notes (Markdown); empty when there are none.</param>
/// <param name="PageUrl">The release's page on github.com.</param>
/// <param name="AssetName">The file name of the build for this runtime, or <see langword="null"/> when there is none.</param>
/// <param name="AssetUrl">Where to download that build, or <see langword="null"/> when there is none.</param>
/// <param name="AssetSize">The build's size in bytes as GitHub reports it; <c>0</c> when unknown.</param>
public sealed record UpdateInfo(Version Version, string Tag, string Name, string Notes, string PageUrl, string? AssetName, string? AssetUrl, long AssetSize)
{
    /// <summary>The release has a build for this runtime that <see cref="UpdateChecker.DownloadAsync"/> can fetch.</summary>
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
    /// <summary>The GitHub repository, <c>owner/repo</c>, whose releases are checked by default.</summary>
    public const string DefaultRepository = "keithadler/leanstudio";

    private readonly HttpClient _http;
    private readonly string _repository;

    /// <summary>
    /// A checker for <paramref name="repository"/>'s releases. Without <paramref name="http"/>, a client with a
    /// 30-second timeout is created. A <c>User-Agent</c> (which GitHub's API requires) is added to the client's
    /// default headers when it has none.
    /// </summary>
    public UpdateChecker(HttpClient? http = null, string repository = DefaultRepository)
    {
        _repository = repository;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LeanStudio", CurrentVersion.ToString(3)));
        }
    }

    /// <summary>The running build's version (major, minor, build) from the assembly; <c>0.0.0</c> when it has none.</summary>
    public static Version CurrentVersion =>
        typeof(UpdateChecker).Assembly.GetName().Version is Version v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0, 0);

    /// <summary>The runtime this build is for, as release file names spell it (osx-arm64, win-x64, linux-x64…).</summary>
    public static string CurrentRuntime
    {
        get
        {
            string os = OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsWindows() ? "win" : "linux";
            // The Intel build under Rosetta is offered the Apple silicon build: Rosetta is going away.
            if (Platform.MacPlatform.IsTranslated)
            {
                return "osx-arm64";
            }
            string arch = RuntimeInformation.OSArchitecture switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x64",
                _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            };
            return os + "-" + arch;
        }
    }

    /// <summary>
    /// Parse a tag like v0.2.0 or 0.2.0-beta into its version (major, minor, build; a missing build is <c>0</c>),
    /// ignoring any pre-release or build suffix. <see langword="null"/> when it is not a version.
    /// </summary>
    public static Version? ParseTag(string tag)
    {
        string t = tag.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        if (!Version.TryParse(t, out Version? v))
        {
            return null;
        }
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>
    /// Ask GitHub for the latest release and return it if it is newer than <paramref name="current"/>; null when up
    /// to date, when the repository has no releases, or when the latest is a draft or pre-release.
    /// <paramref name="current"/> and <paramref name="runtime"/> default to <see cref="CurrentVersion"/> and
    /// <see cref="CurrentRuntime"/>.
    /// </summary>
    /// <exception cref="HttpRequestException">The request failed or GitHub answered with an error other than 404.</exception>
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

    /// <summary>
    /// Decide from a release's JSON (GitHub's REST release object) whether it is an update, and which file is for this
    /// runtime: the first asset whose name contains <c>-runtime.</c>. Drafts, pre-releases, unparseable tags and
    /// versions not newer than <paramref name="current"/> give <see langword="null"/>.
    /// </summary>
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

    /// <summary>The person's <c>~/Downloads</c> folder, or their home folder when there is no Downloads folder.</summary>
    public static string DownloadsFolder
    {
        get
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string downloads = Path.Combine(home, "Downloads");
            return Directory.Exists(downloads) ? downloads : home;
        }
    }

    /// <summary>
    /// Download the release file for this computer into <paramref name="folder"/> (by default
    /// <see cref="DownloadsFolder"/>); returns its path. The file is written as <c>name.part</c> and renamed when
    /// complete, replacing any file of the same name. <paramref name="progress"/> gets the fraction done, 0 to 1,
    /// when the size is known.
    /// </summary>
    /// <exception cref="InvalidOperationException">The release has no build for this runtime.</exception>
    /// <exception cref="HttpRequestException">The download failed.</exception>
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
