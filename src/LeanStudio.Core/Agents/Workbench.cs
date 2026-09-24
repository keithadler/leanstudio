using System.Collections.Concurrent;
using LeanStudio.Core.Projects;
using LeanStudio.Core.Toolchains;
using LeanStudio.Core.Verification;
using LeanStudio.Lsp;

namespace LeanStudio.Core.Agents;

/// <summary>What Lean said about a file once it finished elaborating it.</summary>
/// <param name="Path">The file's full path.</param>
/// <param name="Uri">The <c>file://</c> URI Lean knows it by.</param>
/// <param name="Diagnostics">Every diagnostic Lean published for the text (partial if elaboration timed out).</param>
/// <param name="Text">The exact text that was checked.</param>
public sealed record FileReport(string Path, string Uri, IReadOnlyList<Diagnostic> Diagnostics, string Text)
{
    /// <summary>The number of error diagnostics.</summary>
    public int Errors => Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
    /// <summary>The number of warning diagnostics.</summary>
    public int Warnings => Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
    /// <summary>Whether any diagnostic mentions <c>sorry</c> (as Lean's "declaration uses 'sorry'" warning does).</summary>
    public bool UsesSorry => Diagnostics.Any(d => d.Message.Contains("sorry", StringComparison.Ordinal));
}

/// <summary>
/// Lean for a program rather than a person: one language server per project, started on first use, with files
/// opened, kept in sync with the disk (or with text the caller supplies), and waited on until Lean has finished
/// with exactly that text. The MCP server that AI assistants talk to is a thin layer over this.
/// </summary>
public sealed class Workbench : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ProjectSession> _sessions = new(StringComparer.Ordinal);
    private readonly string? _defaultRoot;

    /// <summary>Create a workbench. No server starts until a file is checked.</summary>
    /// <param name="defaultRoot">
    /// The folder relative paths are resolved against and the project used when no path is given; null for the
    /// current directory.
    /// </param>
    public Workbench(string? defaultRoot = null)
    {
        _defaultRoot = defaultRoot is null ? null : Path.GetFullPath(defaultRoot);
    }

    /// <summary>Progress and server output from every session, one line at a time, raised on whatever thread produced it.</summary>
    public event Action<string>? Log;

    /// <summary>Resolve a path the caller gave (absolute, or relative to the default project) to a full path.</summary>
    public string Resolve(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }
        return Path.GetFullPath(Path.Combine(_defaultRoot ?? Directory.GetCurrentDirectory(), path));
    }

    /// <summary>
    /// The project a file belongs to (the nearest enclosing Lake project), or the default project when no path is
    /// given. A path outside any project gets a project rooted at its folder.
    /// </summary>
    public LeanProject ProjectFor(string? path)
    {
        if (path is null)
        {
            string root = _defaultRoot ?? Directory.GetCurrentDirectory();
            return LeanProject.FindEnclosing(root) ?? new LeanProject(root);
        }
        string full = Resolve(path);
        return LeanProject.FindEnclosing(full) ?? new LeanProject(Directory.Exists(full) ? full : Path.GetDirectoryName(full)!);
    }

    /// <summary>The session for the project <paramref name="path"/> belongs to (see <see cref="ProjectFor"/>), created on first use.</summary>
    public ProjectSession Session(string? path)
    {
        LeanProject p = ProjectFor(path);
        return _sessions.GetOrAdd(p.Root, _ => new ProjectSession(p, s => Log?.Invoke(s)));
    }

    /// <summary>Dispose every session, stopping their Lean servers.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (ProjectSession s in _sessions.Values)
        {
            await s.DisposeAsync().ConfigureAwait(false);
        }
        _sessions.Clear();
    }
}

/// <summary>One project's Lean server and Tenet workspace, as the workbench uses them.</summary>
public sealed class ProjectSession : IAsyncDisposable
{
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, string> _sent = new(StringComparer.Ordinal); // uri -> text Lean has
    private LeanServer? _server;
    private TenetWorkspace? _tenet;

    internal ProjectSession(LeanProject project, Action<string> log)
    {
        Project = project;
        _log = log;
    }

    /// <summary>The project this session serves.</summary>
    public LeanProject Project { get; }

    /// <summary>How long to wait for Lean to finish a file before giving up and reporting what it has so far.</summary>
    public static TimeSpan ElaborationTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The project's Lean server, started (or restarted, if it stopped) when needed. Starting one spawns
    /// <c>lake serve</c> with the project's toolchain, or the newest installed toolchain when the project names none.
    /// </summary>
    /// <exception cref="InvalidOperationException">elan is not installed.</exception>
    public async Task<LeanServer> ServerAsync(CancellationToken ct = default)
    {
        if (_server is { State: LeanServerState.Running } s)
        {
            return s;
        }
        if (!Elan.IsInstalled)
        {
            throw new InvalidOperationException("elan is not installed, so Lean cannot run. Install it from " + Elan.InstallUrl);
        }
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _sent.Clear();
        }
        string? fallback = Project.Toolchain is null
            ? (await Elan.ListAsync(ct).ConfigureAwait(false)).Select(t => t.Name).OrderDescending(StringComparer.Ordinal).FirstOrDefault()
            : null;
        var server = new LeanServer(Project.ServerCommand(fallback));
        server.Log += _log;
        _log($"starting {server.Command} in {Project.Root}");
        await server.StartAsync(ct).ConfigureAwait(false);
        _server = server;
        return server;
    }

    /// <summary>
    /// Make Lean elaborate a file and wait for it. The text is <paramref name="content"/> when given (checked
    /// without being written anywhere), else what is on disk now, so edits made by another program are seen.
    /// After <see cref="ElaborationTimeout"/> it stops waiting and reports the diagnostics so far.
    /// </summary>
    public async Task<FileReport> CheckAsync(string path, string? content = null, CancellationToken ct = default)
    {
        string full = Path.GetFullPath(path);
        string text = content ?? await File.ReadAllTextAsync(full, ct).ConfigureAwait(false);
        string uri = LeanServer.UriOf(full);
        LeanServer server;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            server = await ServerAsync(ct).ConfigureAwait(false);
            if (!_sent.TryGetValue(uri, out string? had) || !server.IsOpen(uri))
            {
                await server.OpenAsync(uri, text).ConfigureAwait(false);
            }
            else if (had != text)
            {
                await server.ChangeAsync(uri, text).ConfigureAwait(false);
            }
            _sent[uri] = text;
        }
        finally
        {
            _lock.Release();
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ElaborationTimeout);
        try
        {
            await server.WaitForElaborationAsync(uri, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log($"Lean did not finish {full} within {ElaborationTimeout}; reporting what it has so far");
        }
        return new FileReport(full, uri, server.DiagnosticsOf(uri), text);
    }

    /// <summary>The text Lean currently has for a file, if it has been checked.</summary>
    public string? SentText(string path) =>
        _sent.TryGetValue(LeanServer.UriOf(Path.GetFullPath(path)), out string? t) ? t : null;

    /// <summary>Open the project's build with Tenet, reopening it if a build has happened since.</summary>
    public TenetWorkspace Tenet()
    {
        _tenet ??= TenetWorkspace.Open(Project);
        return _tenet;
    }

    /// <summary>Forget the Tenet workspace, so the next use reads the fresh build.</summary>
    public void BuildChanged()
    {
        _tenet?.Dispose();
        _tenet = null;
    }

    /// <summary>Close the Tenet workspace and stop the Lean server.</summary>
    public async ValueTask DisposeAsync()
    {
        _tenet?.Dispose();
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(false);
        }
        _lock.Dispose();
    }
}
