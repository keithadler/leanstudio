using System.Reflection;

namespace LeanStudio.App.Services;

/// <summary>The files of Lean's infoview page, embedded in the app, by the path the page asks for.</summary>
public static class InfoviewAssets
{
    private static readonly Assembly Self = typeof(InfoviewAssets).Assembly;

    /// <summary>The file at <paramref name="path"/> (e.g. <c>index.html</c>, <c>iv/index.css</c>) and its content type, or null.</summary>
    public static (byte[] Data, string ContentType)? Get(string path)
    {
        if (path.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }
        using Stream? s = Self.GetManifestResourceStream("infoview/" + path.Replace('\\', '/'));
        if (s is null)
        {
            return null;
        }
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        string type = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".ttf" => "font/ttf",
            _ => "application/octet-stream",
        };
        return (ms.ToArray(), type);
    }
}
