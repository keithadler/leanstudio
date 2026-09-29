using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace LeanStudio.Lsp;

/// <summary>How Lean's tools reach the machine a <see cref="RemoteTarget"/> names.</summary>
public enum RemoteKind
{
    /// <summary>Over SSH, to another machine; its folder is mounted here (sshfs, a network drive).</summary>
    Ssh,

    /// <summary>In a running container (a dev container): <c>docker exec</c>, the project folder mounted into it.</summary>
    Container,

    /// <summary>In a WSL distribution on this Windows machine: <c>wsl.exe</c>, its files reached as <c>\\wsl.localhost\…</c>.</summary>
    Wsl,
}

/// <summary>
/// A project whose Lean runs somewhere else: on another machine over SSH, in a container, or in WSL. Its files are
/// reached through a local folder, and Lean, Lake and elan run there. Paths are rewritten both ways: what goes there
/// names its folder, and what comes back names the local one, so the editor, Problems, go-to-definition and build
/// output all see local paths.
/// </summary>
/// <param name="Host">
/// Where: the SSH destination (<c>user@host</c>, or a <c>Host</c> from ~/.ssh/config), the container's name or id,
/// or the WSL distribution's name, depending on <see cref="Kind"/>.
/// </param>
/// <param name="RemoteRoot">The project's folder on the remote machine (a POSIX path).</param>
/// <param name="LocalRoot">Where that folder is mounted here.</param>
public sealed partial record RemoteTarget(string Host, string RemoteRoot, string LocalRoot)
{
    /// <summary>How the tools reach <see cref="Host"/>: SSH (the default), a container, or WSL.</summary>
    public RemoteKind Kind { get; init; } = RemoteKind.Ssh;

    /// <summary>The docker program, for a container (by default <c>docker</c> on the PATH).</summary>
    public string Docker { get; init; } = "docker";

    /// <summary>The WSL program, for a WSL distribution.</summary>
    public string WslProgram { get; init; } = "wsl.exe";

    /// <summary>Where Lean runs, in words: <c>me@box</c>, <c>the dev container 1a2b3c</c>, <c>WSL (Ubuntu)</c>.</summary>
    public string Describe() => Kind switch
    {
        RemoteKind.Container => "the container " + (Host.Length > 12 ? Host[..12] : Host),
        RemoteKind.Wsl => $"WSL ({Host})",
        _ => Host,
    };

    /// <summary>The ssh program (by default <c>ssh</c> on the PATH).</summary>
    public string Ssh { get; init; } = "ssh";

    /// <summary>
    /// ssh's options, before the host. By default it never asks for a password (keys or an agent must be set up, as
    /// for any tool that runs ssh in the background), and keeps the connection alive.
    /// </summary>
    public IReadOnlyList<string> SshOptions { get; init; } = ["-o", "BatchMode=yes", "-o", "ServerAliveInterval=30"];

    private static readonly HashSet<string> LeanTools = new(StringComparer.OrdinalIgnoreCase) { "lake", "lean", "leanc", "elan", "leanmake", "cache" };

    private string Local => Path.TrimEndingDirectorySeparator(Path.GetFullPath(LocalRoot));

    private string Remote => RemoteRoot.Length > 1 ? RemoteRoot.TrimEnd('/') : RemoteRoot;

    /// <summary>Whether a program is one of Lean's (<c>lake</c>, <c>lean</c>, <c>elan</c>…), which run on the remote machine.</summary>
    /// <param name="program">A program name or path.</param>
    public static bool IsLeanTool(string program) => LeanTools.Contains(ToolName(program));

    // The program's name without its folder or .exe, whichever machine's path it is.
    private static string ToolName(string program)
    {
        string name = program[(program.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>Whether a local path is inside the mounted project.</summary>
    /// <param name="localPath">A local path, or null.</param>
    public bool Covers(string? localPath)
    {
        if (string.IsNullOrEmpty(localPath))
        {
            return false;
        }
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localPath));
        StringComparison cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.Equals(Local, cmp) || full.StartsWith(Local + Path.DirectorySeparatorChar, cmp);
    }

    /// <summary>A local path inside the project as the remote machine names it; other paths are returned as they are.</summary>
    /// <param name="localPath">The local path.</param>
    public string ToRemotePath(string localPath)
    {
        if (!Covers(localPath))
        {
            return localPath;
        }
        string rel = Path.GetRelativePath(Local, Path.GetFullPath(localPath));
        return rel == "." ? Remote : Remote.TrimEnd('/') + "/" + rel.Replace('\\', '/');
    }

    /// <summary>The <c>file://</c> URI of the project's folder here.</summary>
    public string LocalUri => new Uri(Local + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/');

    /// <summary>The <c>file://</c> URI of the project's folder on the remote machine.</summary>
    public string RemoteUri => "file://" + string.Join('/', Remote.Split('/').Select(Uri.EscapeDataString));

    /// <summary>Text going to the remote machine (a message to Lean, an argument) with local paths and URIs made remote.</summary>
    /// <param name="text">The text.</param>
    public string ToRemote(string text)
    {
        text = Swap(text, LocalUri, RemoteUri);
        text = Swap(text, Local, Remote);
        // A Windows path inside the project also changes its separators.
        return Path.DirectorySeparatorChar == '\\' ? WindowsTail().Replace(text, m => m.Value.Replace('\\', '/')) : text;
    }

    /// <summary>Text from the remote machine (Lean's messages, build output) with its paths and URIs made local.</summary>
    /// <param name="text">The text.</param>
    public string ToLocal(string text)
    {
        text = Swap(text, RemoteUri, LocalUri);
        return Swap(text, Remote, Local);
    }

    // Only whole path prefixes: /a/Proofs becomes /b/Proofs, and /a/Proofs2 is left alone.
    private static string Swap(string text, string from, string to) =>
        from.Length == 0 || !text.Contains(from, StringComparison.Ordinal)
            ? text
            : Regex.Replace(text, Regex.Escape(from) + @"(?=$|[/\\""'\s:),\]}])", to.Replace("$", "$$", StringComparison.Ordinal));

    [GeneratedRegex(@"(?<=file://[^""\s]*)\\")]
    private static partial Regex WindowsTail();

    /// <summary>A word for a POSIX shell, in single quotes when it needs them.</summary>
    /// <param name="s">The word.</param>
    public static string Quote(string s) =>
        s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or '=' or '+' or ':' or ',' or '@')
            ? s
            : "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// The ssh command line that runs <paramref name="program"/> (by name, found through elan on the remote
    /// machine) in the remote folder matching <paramref name="localDirectory"/>, with its arguments made remote.
    /// </summary>
    /// <param name="program">The program, by name or local path.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="localDirectory">The local folder it would run in, or null for the project's root.</param>
    public (string FileName, IReadOnlyList<string> Arguments) Command(string program, IEnumerable<string> arguments, string? localDirectory)
    {
        string dir = localDirectory is not null && Covers(localDirectory) ? ToRemotePath(localDirectory) : Remote;
        var sb = new StringBuilder();
        sb.Append("cd ").Append(Quote(dir)).Append(" && PATH=\"$HOME/.elan/bin:$PATH\" exec ").Append(Quote(ToolName(program)));
        foreach (string a in arguments)
        {
            // A local path in the project is mapped whole (a Windows path's separators too); other text is searched for paths.
            sb.Append(' ').Append(Quote(Path.IsPathRooted(a) && Covers(a) ? ToRemotePath(a) : ToRemote(a)));
        }
        return Kind switch
        {
            // -i keeps stdin open: Lean's server talks over it.
            RemoteKind.Container => (Docker, ["exec", "-i", Host, "sh", "-c", sb.ToString()]),
            RemoteKind.Wsl => (WslProgram, ["-d", Host, "-e", "sh", "-c", sb.ToString()]),
            _ => (Ssh, [.. SshOptions, Host, sb.ToString()]),
        };
    }

    /// <summary>
    /// For a folder in a WSL distribution seen from Windows (<c>\\wsl.localhost\Ubuntu\home\me\proj</c>, or
    /// <c>\\wsl$\Ubuntu\…</c>), the target that runs Lean inside that distribution; null for any other folder.
    /// </summary>
    /// <param name="localRoot">The folder, as Windows names it.</param>
    public static RemoteTarget? ForWslPath(string localRoot)
    {
        Match m = WslPath().Match(localRoot);
        if (!m.Success)
        {
            return null;
        }
        string rest = m.Groups["rest"].Value.Replace('\\', '/').TrimEnd('/');
        return new RemoteTarget(m.Groups["distro"].Value, rest.Length == 0 ? "/" : "/" + rest.TrimStart('/'), localRoot) { Kind = RemoteKind.Wsl };
    }

    [GeneratedRegex(@"^[\\/]{2}(?:wsl\$|wsl\.localhost)[\\/](?<distro>[^\\/]+)(?<rest>(?:[\\/].*)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex WslPath();

    /// <summary>
    /// Read <c>user@host:/path</c> (scp's notation) into a host and a remote folder; null when it isn't one.
    /// </summary>
    /// <param name="text">The text typed.</param>
    public static (string Host, string RemoteRoot)? ParseDestination(string text)
    {
        Match m = Destination().Match(text.Trim());
        return m.Success ? (m.Groups["host"].Value, m.Groups["path"].Value) : null;
    }

    [GeneratedRegex(@"^(?<host>[^\s:/]+):(?<path>/\S*|~\S*)$")]
    private static partial Regex Destination();
}

/// <summary>
/// The remote projects in use, by local folder: Lean's server and <c>ProcessRunner</c> look a working folder up
/// here, and run Lean's tools over SSH when it is inside one.
/// </summary>
public static class RemoteTargets
{
    private static readonly ConcurrentDictionary<string, RemoteTarget> Targets = new(StringComparer.Ordinal);

    /// <summary>Use <paramref name="target"/> for everything under its local folder (replacing any before).</summary>
    /// <param name="target">The remote project.</param>
    public static void Register(RemoteTarget target) => Targets[Path.GetFullPath(target.LocalRoot)] = target;

    /// <summary>Stop using a remote project.</summary>
    /// <param name="localRoot">Its local folder.</param>
    public static void Unregister(string localRoot) => Targets.TryRemove(Path.GetFullPath(localRoot), out _);

    /// <summary>The remote project a local path is in, or null.</summary>
    /// <param name="localPath">A local file or folder, or null.</param>
    public static RemoteTarget? For(string? localPath) =>
        localPath is null ? null : Targets.Values.Where(t => t.Covers(localPath)).MaxBy(t => t.LocalRoot.Length);
}
