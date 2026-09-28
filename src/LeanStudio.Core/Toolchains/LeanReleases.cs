using System.Net.Http.Headers;
using System.Text.Json;
using LeanStudio.Core.Projects;

namespace LeanStudio.Core.Toolchains;

/// <summary>A stable Lean release newer than the one a project pins.</summary>
/// <param name="Pinned">The version the project pins, as written, e.g. <c>v4.34.0</c>.</param>
/// <param name="Latest">The newest stable release's tag, e.g. <c>v4.34.1</c>.</param>
public sealed record NewerLean(string Pinned, string Latest)
{
    /// <summary>The toolchain to install and pin, e.g. <c>leanprover/lean4:v4.34.1</c>.</summary>
    public string Toolchain => LeanReleases.Channel + Latest;
}

/// <summary>
/// Whether a newer stable Lean is out than the one a project pins, and whether it is safe to say so.
/// </summary>
/// <remarks>
/// It only ever speaks about a project that depends on nothing. A project that requires Mathlib, Batteries or
/// anything else has to use the Lean its dependencies were built for, so "a newer Lean is out" would be bad advice
/// there: the way to move such a project forward is to update its dependencies, which bring their toolchain with
/// them. A pinned release candidate, a nightly, or a floating channel such as <c>stable</c> gets no suggestion
/// either; someone who chose those did not choose to follow stable releases.
/// </remarks>
public static class LeanReleases
{
    /// <summary>The official toolchain channel, which a pinned toolchain name starts with.</summary>
    public const string Channel = "leanprover/lean4:";

    /// <summary>The repository whose latest non-prerelease release is Lean's latest stable version.</summary>
    public const string Repository = "leanprover/lean4";

    /// <summary>
    /// The version of a plain stable release, from <c>v4.34.1</c> or <c>leanprover/lean4:v4.34.1</c>. Anything
    /// else (a release candidate, a nightly, <c>stable</c>, another channel) gives <see langword="null"/>.
    /// </summary>
    public static Version? StableVersion(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        string s = name.Trim();
        if (s.Contains(':', StringComparison.Ordinal))
        {
            if (!s.StartsWith(Channel, StringComparison.Ordinal))
            {
                return null;
            }
            s = s[Channel.Length..];
        }
        if (!s.StartsWith('v'))
        {
            return null;
        }
        string[] parts = s[1..].Split('.');
        if (parts.Length != 3)
        {
            return null;
        }
        var n = new int[3];
        for (int i = 0; i < 3; i++)
        {
            // Digits only: "0-rc3" and the like are not a stable release.
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit) || !int.TryParse(parts[i], out n[i]))
            {
                return null;
            }
        }
        return new Version(n[0], n[1], n[2]);
    }

    /// <summary>
    /// A suggestion to move to <paramref name="latestStableTag"/>, or <see langword="null"/> when there is nothing
    /// to suggest: the project depends on something, pins no plain stable release, or is already current.
    /// </summary>
    public static NewerLean? Suggest(string? pinnedToolchain, string? latestStableTag, bool hasDependencies)
    {
        if (hasDependencies || pinnedToolchain is null || !pinnedToolchain.Trim().StartsWith(Channel, StringComparison.Ordinal))
        {
            return null;
        }
        if (StableVersion(pinnedToolchain) is not Version pinned || StableVersion(latestStableTag) is not Version latest
            || latest <= pinned)
        {
            return null;
        }
        return new NewerLean(pinnedToolchain.Trim()[Channel.Length..], latestStableTag!.Trim());
    }

    /// <summary>
    /// Whether the project requires other packages: its <c>lake-manifest.json</c> lists any, or, before the first
    /// build writes one, its lakefile has a <c>require</c>. When in doubt it says yes, since staying quiet is the
    /// safe mistake.
    /// </summary>
    public static bool HasDependencies(LeanProject project)
    {
        // Either source is enough: a lakefile can gain a `require` before the next update rewrites the manifest.
        if (project.Lakefile is string lakefile)
        {
            string text = File.ReadAllText(lakefile);
            bool requires = lakefile.EndsWith(".toml", StringComparison.Ordinal)
                ? text.Contains("[[require]]", StringComparison.Ordinal)
                : text.Split('\n').Any(l => l.TrimStart().StartsWith("require ", StringComparison.Ordinal));
            if (requires)
            {
                return true;
            }
        }
        if (!File.Exists(project.ManifestPath))
        {
            return false;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(project.ManifestPath));
            return !doc.RootElement.TryGetProperty("packages", out JsonElement packages)
                || packages.ValueKind != JsonValueKind.Array
                || packages.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>
    /// The newest stable Lean's tag, e.g. <c>v4.34.1</c>, from GitHub's latest release of <c>leanprover/lean4</c>,
    /// which is never a pre-release. <see langword="null"/> when the answer is not a plain stable tag.
    /// </summary>
    /// <exception cref="HttpRequestException">The request failed.</exception>
    public static async Task<string?> LatestStableTagAsync(HttpClient http, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LeanStudio", "1"));
        using HttpResponseMessage r = await http.SendAsync(request, ct).ConfigureAwait(false);
        r.EnsureSuccessStatusCode();
        using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return LatestStableTag(doc.RootElement);
    }

    /// <summary>The tag of a GitHub release object, when it is a published, stable Lean release.</summary>
    public static string? LatestStableTag(JsonElement release)
    {
        if (release.TryGetProperty("draft", out JsonElement d) && d.ValueKind == JsonValueKind.True
            || release.TryGetProperty("prerelease", out JsonElement p) && p.ValueKind == JsonValueKind.True
            || !release.TryGetProperty("tag_name", out JsonElement t))
        {
            return null;
        }
        string? tag = t.GetString();
        return StableVersion(tag) is null ? null : tag;
    }
}
