using Avalonia;
using LeanStudio.Core.Agents;
using LeanStudio.Mcp;

namespace LeanStudio.App;

/// <summary>
/// The entry point: opens the IDE window, or with <c>--mcp</c> runs as an MCP server over standard input and output.
/// </summary>
internal static class Program
{
    /// <summary>
    /// Start Lean Studio. <c>--help</c> and <c>--version</c> print and exit. With <c>--mcp</c> (and optionally
    /// <c>--project DIR</c>) it serves MCP on stdio until standard input closes or Ctrl+C, logging to standard error;
    /// otherwise it opens the window, and the first argument not starting with <c>-</c> is a file or folder to open.
    /// Returns the process exit code.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // Answered here, before any window: from a terminal, `leanstudio --help` must print, not open the IDE.
        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.Out.Write(Usage);
            return 0;
        }
        if (args.Contains("--version"))
        {
            Console.Out.WriteLine("Lean Studio " + Services.Credits.Version);
            return 0;
        }
        // `LeanStudio --mcp [--project DIR]` runs Lean Studio as an MCP server for an AI assistant (Claude Code,
        // Gemini CLI, Codex, Grok…) instead of opening a window. Same binary, so one install serves both.
        if (args.Contains("--mcp"))
        {
            return RunMcp(args);
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>What <c>--help</c> prints.</summary>
    internal const string Usage = """
        Lean Studio, an IDE for Lean 4.

        Usage:
          leanstudio [PATH]                   open the IDE, with a project folder or a .lean file
          leanstudio --new-window [PATH]      open a new window instead of restoring the last session
          leanstudio --mcp [--project DIR]    run the MCP server for AI assistants on standard input and output
          leanstudio --version                print the version
          leanstudio --help                   print this

        """;

    /// <summary>The Avalonia app configuration; also used by the XAML designer.</summary>
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
