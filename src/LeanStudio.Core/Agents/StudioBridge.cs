using System.IO.Pipes;
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
    /// the first window opened is the one assistants talk to. Requests are handled one at a time on a thread-pool
    /// thread, so <paramref name="handle"/> must marshal to the UI thread itself; an exception it throws is sent back
    /// as <c>{"error": message}</c>.
    /// </summary>
    public static bool TryServe(Func<JsonObject, Task<JsonObject>> handle, CancellationToken ct)
    {
        NamedPipeServerStream first;
        try
        {
            first = Create();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        _ = Task.Run(() => LoopAsync(first, handle, ct), ct);
        return true;
    }

    private static NamedPipeServerStream Create() =>
        new(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static async Task LoopAsync(NamedPipeServerStream pipe, Func<JsonObject, Task<JsonObject>> handle, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
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
            }
            if (ct.IsCancellationRequested)
            {
                break;
            }
            try
            {
                pipe = Create();
            }
            catch (IOException)
            {
                break;
            }
        }
    }
}
