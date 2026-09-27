using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeanStudio.Core.Agents;

/// <summary>
/// The line between a running Lean Studio window and an AI assistant's tools: a local pipe only the current user
/// can open. The assistant asks what the person is looking at (file, cursor, selection, goals) or asks the window
/// to show a file; the window answers. One JSON object per line each way, one request per connection.
/// </summary>
public static class StudioBridge
{
    /// <summary>
    /// One name per user, so two people on a machine never reach each other's window: <c>leanstudio-</c> and the user
    /// name, or the value of the <c>LEANSTUDIO_PIPE</c> environment variable when it is set.
    /// </summary>
    public static string PipeName
    {
        get
        {
            // Tests (and anyone running two separate setups) can pick their own pipe.
            if (Environment.GetEnvironmentVariable("LEANSTUDIO_PIPE") is { Length: > 0 } custom)
            {
                return custom;
            }
            string user = new(Environment.UserName.Where(char.IsLetterOrDigit).ToArray());
            return "leanstudio-" + (user.Length == 0 ? "user" : user.ToLowerInvariant());
        }
    }

    /// <summary>
    /// Send one request to the running window and return its answer. Null when no window is running, it did not
    /// answer within <paramref name="timeout"/> (5 seconds by default), or the answer was not a JSON object.
    /// </summary>
    public static async Task<JsonObject?> RequestAsync(JsonObject request, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        try
        {
            await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await writer.WriteLineAsync(request.ToJsonString().AsMemory(), cts.Token).ConfigureAwait(false);
            string? line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
            return line is null ? null : JsonNode.Parse(line) as JsonObject;
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Serve requests until cancelled. Returns immediately with false if another window already owns the pipe:
    /// the first window opened is the one assistants talk to. A socket file left by a window that crashed is
    /// reclaimed (<see cref="RemoveStaleSocket"/>). Requests are handled one at a time on a thread-pool
    /// thread, so <paramref name="handle"/> must marshal to the UI thread itself; an exception it throws is sent back
    /// as <c>{"error": message}</c>.
    /// </summary>
    public static bool TryServe(Func<JsonObject, Task<JsonObject>> handle, CancellationToken ct)
    {
        NamedPipeServerStream first;
        try
        {
            // The first instance claims the name: this fails while another window holds it.
            first = Create(PipeOptions.FirstPipeInstance);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // On macOS and Linux the name is a socket file, and a window that crashed leaves its file behind, which
            // would keep every later window from serving. Reclaim it only when nothing is listening on it.
            if (!RemoveStaleSocket())
            {
                return false;
            }
            try
            {
                first = Create(PipeOptions.FirstPipeInstance);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        _ = Task.Run(() => LoopAsync(first, handle, ct), ct);
        return true;
    }

    /// <summary>
    /// Where .NET puts the pipe's socket on macOS and Linux (<c>CoreFxPipe_</c> and the name, in the temp folder);
    /// null on Windows, whose pipes are not files.
    /// </summary>
    public static string? SocketPath =>
        OperatingSystem.IsWindows() ? null : Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + PipeName);

    /// <summary>
    /// Delete the pipe's socket file if a window that is gone left it behind: one that refuses a connection. A socket
    /// another window is listening on accepts the connection, so it is never touched. True if a stale file was removed.
    /// </summary>
    public static bool RemoveStaleSocket()
    {
        if (SocketPath is not string path || !File.Exists(path))
        {
            return false;
        }
        using (var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            try
            {
                probe.Connect(new UnixDomainSocketEndPoint(path));
                return false;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused)
            {
                // Nobody is listening: the file is stale.
            }
            catch (SocketException)
            {
                return false;
            }
        }
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Two instances at most: the one serving a request, and the next, made before the first is let go, so the
    /// name is never free for another window to take between requests.
    /// </summary>
    private static NamedPipeServerStream Create(PipeOptions extra = PipeOptions.None) =>
        new(PipeName, PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | extra);

    private static async Task LoopAsync(NamedPipeServerStream pipe, Func<JsonObject, Task<JsonObject>> handle, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? next = null;
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                // Hold the name for the next request before this one is answered and closed.
                next = Create();
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                JsonObject response;
                try
                {
                    response = line is not null && JsonNode.Parse(line) is JsonObject req
                        ? await handle(req).ConfigureAwait(false)
                        : new JsonObject { ["error"] = "expected one JSON object" };
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    response = new JsonObject { ["error"] = e.Message };
                }
                await writer.WriteLineAsync(response.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // The client went away mid-request; wait for the next one.
            }
            finally
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                if (ct.IsCancellationRequested && next is not null)
                {
                    await next.DisposeAsync().ConfigureAwait(false);
                }
            }
            if (ct.IsCancellationRequested)
            {
                break;
            }
            try
            {
                pipe = next ?? Create();
            }
            catch (IOException)
            {
                break;
            }
        }
    }
}
