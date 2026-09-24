using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeanStudio.Lsp;

/// <summary>
/// clangd, for the C files of a Lean project (FFI code): diagnostics, hover, completion and go to definition.
/// Lean's own headers are on the include path (as fallback flags, so nothing is written into the project), so
/// <c>#include &lt;lean/lean.h&gt;</c> resolves and clangd knows <c>lean_object</c> and the runtime functions.
/// </summary>
public sealed class CLanguageServer : IAsyncDisposable
{
    private readonly string _clangd;
    private readonly string _root;
    private readonly IReadOnlyList<string> _flags;
    private readonly ConcurrentDictionary<string, int> _versions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<Diagnostic>> _diagnostics = new(StringComparer.Ordinal);
    private Process? _process;
    private JsonRpcConnection? _rpc;

    /// <summary>Prepare clangd for a project. Nothing runs until <see cref="StartAsync"/>.</summary>
    /// <param name="clangd">The clangd executable, as <see cref="Find"/> returns it.</param>
    /// <param name="root">The project folder: clangd's working directory and root URI.</param>
    /// <param name="flags">Compiler flags for files with no compile command (Lean's include path, say), passed as clangd's fallback flags.</param>
    public CLanguageServer(string clangd, string root, IReadOnlyList<string> flags)
    {
        _clangd = clangd;
        _root = root;
        _flags = flags;
    }

    /// <summary>clangd on the PATH, or Xcode's on macOS; null when there is none.</summary>
    public static string? Find()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string name in OperatingSystem.IsWindows() ? new[] { "clangd.exe" } : ["clangd"])
            {
                string f = Path.Combine(dir, name);
                if (File.Exists(f))
                {
                    return f;
                }
            }
        }
        foreach (string f in new[] { "/usr/bin/clangd", "/opt/homebrew/opt/llvm/bin/clangd", "/usr/local/opt/llvm/bin/clangd",
                                     "/Library/Developer/CommandLineTools/usr/bin/clangd" })
        {
            if (File.Exists(f))
            {
                return f;
            }
        }
        if (OperatingSystem.IsMacOS() && File.Exists("/usr/bin/xcrun"))
        {
            // Xcode's own, which xcrun knows where to find.
            try
            {
                using var p = Process.Start(new ProcessStartInfo("/usr/bin/xcrun", ["--find", "clangd"]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                string? path = p?.StandardOutput.ReadToEnd().Trim();
                p?.WaitForExit(5000);
                if (p is { ExitCode: 0 } && File.Exists(path))
                {
                    return path;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }
        return null;
    }

    /// <summary>clangd has been started and has not exited.</summary>
    public bool IsRunning => _process is { HasExited: false } && _rpc is not null;

    /// <summary>clangd published diagnostics for a document (its URI, and the full list). Raised on a background thread.</summary>
    public event Action<string, IReadOnlyList<Diagnostic>>? DiagnosticsPublished;

    /// <summary>
    /// Start clangd in the project folder and complete the LSP handshake, with the fallback flags as initialization
    /// options. Throws <see cref="InvalidOperationException"/> when the process cannot be started.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(_clangd)
        {
            WorkingDirectory = _root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--log=error");
        psi.ArgumentList.Add("--header-insertion=never");
        _process = Process.Start(psi) ?? throw new InvalidOperationException("could not start clangd");
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();
        _rpc = new JsonRpcConnection(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream);
        _rpc.NotificationReceived += (method, p) =>
        {
            if (method == "textDocument/publishDiagnostics")
            {
                string uri = p.GetProperty("uri").GetString() ?? "";
                var list = p.GetProperty("diagnostics").As<List<Diagnostic>>() ?? [];
                _diagnostics[uri] = list;
                DiagnosticsPublished?.Invoke(uri, list);
            }
        };
        _rpc.RequestHandler = (_, _) => Task.FromResult<JsonNode?>(null);
        _rpc.Start();
        var flags = new JsonArray();
        foreach (string f in _flags)
        {
            flags.Add(f);
        }
        await _rpc.RequestAsync("initialize", new JsonObject
        {
            ["processId"] = Environment.ProcessId,
            ["rootUri"] = new Uri(Path.GetFullPath(_root) + Path.DirectorySeparatorChar).AbsoluteUri,
            ["capabilities"] = new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["hover"] = new JsonObject { ["contentFormat"] = new JsonArray("markdown", "plaintext") },
                    ["completion"] = new JsonObject { ["completionItem"] = new JsonObject { ["snippetSupport"] = false } },
                    ["publishDiagnostics"] = new JsonObject(),
                },
            },
            ["initializationOptions"] = new JsonObject { ["fallbackFlags"] = flags },
        }, ct).ConfigureAwait(false);
        await _rpc.NotifyAsync("initialized", new JsonObject()).ConfigureAwait(false);
    }

    private JsonRpcConnection Rpc => _rpc ?? throw new InvalidOperationException("clangd is not running");

    /// <summary>The document is open in clangd.</summary>
    public bool IsOpen(string uri) => _versions.ContainsKey(uri);

    /// <summary>Open a document at version 1: C++ for <c>.cpp</c> and <c>.cc</c>, C otherwise.</summary>
    public Task OpenAsync(string uri, string text)
    {
        _versions[uri] = 1;
        return Rpc.NotifyAsync("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = uri.EndsWith(".cpp", StringComparison.Ordinal) || uri.EndsWith(".cc", StringComparison.Ordinal) ? "cpp" : "c", ["version"] = 1, ["text"] = text },
        });
    }

    /// <summary>Send the document's whole new text as its next version.</summary>
    public Task ChangeAsync(string uri, string text)
    {
        int v = _versions.AddOrUpdate(uri, 1, (_, x) => x + 1);
        return Rpc.NotifyAsync("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = v },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text }),
        });
    }

    /// <summary>Close a document and forget its diagnostics.</summary>
    public Task CloseAsync(string uri)
    {
        _versions.TryRemove(uri, out _);
        _diagnostics.TryRemove(uri, out _);
        return Rpc.NotifyAsync("textDocument/didClose", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } });
    }

    /// <summary>The last diagnostics clangd published for a document; empty when there are none yet.</summary>
    public IReadOnlyList<Diagnostic> DiagnosticsOf(string uri) => _diagnostics.TryGetValue(uri, out var d) ? d : [];

    /// <summary>The hover at a 0-based position; null when there is none.</summary>
    public async Task<Hover?> HoverAsync(string uri, Position pos, CancellationToken ct = default) =>
        LeanServer.ParseHover(await Rpc.RequestAsync("textDocument/hover", LeanServer.At(uri, pos), ct).ConfigureAwait(false));

    /// <summary>Where the symbol at a 0-based position is defined; empty when clangd does not know.</summary>
    public async Task<IReadOnlyList<Location>> DefinitionAsync(string uri, Position pos, CancellationToken ct = default) =>
        LeanServer.ParseLocations(await Rpc.RequestAsync("textDocument/definition", LeanServer.At(uri, pos), ct).ConfigureAwait(false));

    /// <summary>The completions at a 0-based position; empty when there are none.</summary>
    public async Task<IReadOnlyList<CompletionItem>> CompletionAsync(string uri, Position pos, CancellationToken ct = default) =>
        LeanServer.ParseCompletions(await Rpc.RequestAsync("textDocument/completion", LeanServer.At(uri, pos), ct).ConfigureAwait(false));

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_rpc is not null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _rpc.RequestAsync("shutdown", null, cts.Token).ConfigureAwait(false);
                await _rpc.NotifyAsync("exit", null).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or JsonRpcException or ObjectDisposedException)
            {
            }
            await _rpc.DisposeAsync().ConfigureAwait(false);
        }
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        }
        _process?.Dispose();
    }
}
