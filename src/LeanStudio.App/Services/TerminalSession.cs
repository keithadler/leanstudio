using System.Collections.Concurrent;
using System.Text;
using Avalonia.Threading;
using LeanStudio.Core.Toolchains;
using Porta.Pty;
using XTerm;
using XTerm.Options;

namespace LeanStudio.App.Services;

/// <summary>
/// A shell in the app: a pseudo-terminal running the person's shell (zsh, bash, PowerShell) in a folder, and a
/// terminal emulator (xterm's, in .NET) keeping its screen. What the shell prints is fed to the emulator on the UI
/// thread; what the person types is sent to the shell. elan's <c>bin</c> is on the path, so <c>lake</c> and
/// <c>lean</c> work as they do in the app.
/// </summary>
public sealed class TerminalSession : IAsyncDisposable
{
    private readonly IPtyConnection _pty;
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private int _posted;

    private TerminalSession(IPtyConnection pty, Terminal term, string shell, string folder)
    {
        _pty = pty;
        Term = term;
        Shell = shell;
        Folder = folder;
        Term.DataReceived += (_, e) => Input(e.Data); // the emulator's own replies (cursor reports, device attributes)
        _pty.ProcessExited += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            ExitCode = _pty.ExitCode;
            Exited?.Invoke(_pty.ExitCode);
        });
        _ = Task.Run(ReadAsync);
    }

    /// <summary>The emulator: the screen, the scrollback and the cursor.</summary>
    public Terminal Term { get; }

    /// <summary>The shell's path.</summary>
    public string Shell { get; }

    /// <summary>The folder it started in.</summary>
    public string Folder { get; }

    /// <summary>The shell's process id.</summary>
    public int Pid => _pty.Pid;

    /// <summary>The shell's exit code once it has exited, else null.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>The screen changed (on the UI thread, at most once per batch of output).</summary>
    public event Action? Changed;

    /// <summary>The shell exited, with its code (on the UI thread).</summary>
    public event Action<int>? Exited;

    /// <summary>What the screen shows now, one line per row, for checks and for copying it all.</summary>
    public string ScreenText => string.Join('\n', Term.GetVisibleLines()).TrimEnd();

    /// <summary>The shell to run: <c>$SHELL</c> (or zsh on macOS, bash on Linux), PowerShell on Windows.</summary>
    public static string DefaultShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return Elan.FindExecutable("pwsh") ?? "powershell.exe";
        }
        string? shell = Environment.GetEnvironmentVariable("SHELL");
        return shell is { Length: > 0 } && File.Exists(shell) ? shell
            : File.Exists("/bin/zsh") && OperatingSystem.IsMacOS() ? "/bin/zsh"
            : File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
    }

    /// <summary>Start <paramref name="shell"/> (the default one when null) in <paramref name="folder"/>, <paramref name="cols"/> by <paramref name="rows"/>.</summary>
    public static async Task<TerminalSession> StartAsync(string folder, int cols, int rows, string? shell = null, CancellationToken ct = default)
    {
        shell ??= DefaultShell();
        cols = Math.Max(10, cols);
        rows = Math.Max(2, rows);
        var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "", OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        env["TERM"] = "xterm-256color";
        env["COLORTERM"] = "truecolor";
        env["TERM_PROGRAM"] = "LeanStudio";
        // An app started from the Dock or the Start menu may not have elan on its path; the terminal should.
        string elanBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".elan", "bin");
        string pathKey = env.Keys.FirstOrDefault(k => string.Equals(k, "PATH", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
        string path = env.GetValueOrDefault(pathKey, "");
        if (Directory.Exists(elanBin) && !path.Split(Path.PathSeparator).Contains(elanBin))
        {
            env[pathKey] = elanBin + Path.PathSeparator + path;
        }
        IPtyConnection pty = await PtyProvider.SpawnAsync(new PtyOptions
        {
            Name = "Lean Studio",
            Cols = cols,
            Rows = rows,
            Cwd = folder,
            App = shell,
            CommandLine = [],
            Environment = env,
        }, ct).ConfigureAwait(false);
        var term = new Terminal(new TerminalOptions { Cols = cols, Rows = rows, Scrollback = 5000 });
        return new TerminalSession(pty, term, shell, folder);
    }

    /// <summary>Send text to the shell, as if typed.</summary>
    public void Input(string data)
    {
        if (ExitCode is not null || data.Length == 0)
        {
            return;
        }
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(data);
            _pty.WriterStream.Write(bytes, 0, bytes.Length);
            _pty.WriterStream.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The shell has gone; its exit is reported separately.
        }
    }

    /// <summary>Run a command line in the shell (typed, then Enter).</summary>
    public void Run(string command) => Input(command + (OperatingSystem.IsWindows() ? "\r\n" : "\r"));

    /// <summary>The view changed size: tell the emulator and the shell.</summary>
    public void Resize(int cols, int rows)
    {
        cols = Math.Max(10, cols);
        rows = Math.Max(2, rows);
        if (cols == Term.Cols && rows == Term.Rows)
        {
            return;
        }
        Term.Resize(cols, rows);
        try
        {
            _pty.Resize(cols, rows);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
        Changed?.Invoke();
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[16384];
        Decoder utf8 = Encoding.UTF8.GetDecoder();
        var chars = new char[buffer.Length + 4];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await _pty.ReaderStream.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (n <= 0)
                {
                    break;
                }
                // A character split across two reads is held by the decoder until its end arrives.
                int count = utf8.GetChars(buffer, 0, n, chars, 0);
                _pending.Enqueue(new string(chars, 0, count));
                // One post per burst of output, however many reads it took.
                if (Interlocked.Exchange(ref _posted, 1) == 0)
                {
                    Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private void Flush()
    {
        Interlocked.Exchange(ref _posted, 0);
        var sb = new StringBuilder();
        while (_pending.TryDequeue(out string? s))
        {
            sb.Append(s);
        }
        if (sb.Length > 0)
        {
            Term.Write(sb.ToString());
            Changed?.Invoke();
        }
    }

    /// <summary>Stop the shell (and what it runs) and the reading.</summary>
    public ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (ExitCode is null)
            {
                _pty.Kill();
            }
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
        }
        _pty.Dispose();
        Term.Dispose();
        return ValueTask.CompletedTask;
    }
}
