using Avalonia;
using LeanStudio.Core.Agents;
using LeanStudio.Mcp;

namespace LeanStudio.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // `LeanStudio --mcp [--project DIR]` runs Lean Studio as an MCP server for an AI assistant (Claude Code,
        // Gemini CLI, Codex, Grok…) instead of opening a window. Same binary, so one install serves both.
        if (args.Contains("--mcp"))
        {
            return RunMcp(args);
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static int RunMcp(string[] args)
    {
        int i = Array.IndexOf(args, "--project");
        string? project = i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var bench = new Workbench(project);
        bench.Log += line => Console.Error.WriteLine("[leanstudio] " + line);
        McpServer server = LeanTools.Create(bench, Services.Credits.Version);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        try
        {
            McpServer.RunStdioAsync(server, cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            bench.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        return 0;
    }
}
