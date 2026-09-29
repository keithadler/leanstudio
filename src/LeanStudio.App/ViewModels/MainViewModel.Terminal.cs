using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LeanStudio.App.Services;

namespace LeanStudio.App.ViewModels;

/// <summary>
/// The integrated terminal in the bottom panel: the person's shell, started in the project's folder the first time
/// the Terminal tab is shown, with <c>lake</c> and <c>lean</c> on its path.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The Terminal panel's index in <see cref="BottomTab"/>.</summary>
    public const int TerminalPanel = 8;

    /// <summary>The shell running in the Terminal panel, or null before one is started (or after it is stopped).</summary>
    [ObservableProperty]
    private TerminalSession? _terminal;

    /// <summary>What the Terminal panel says above the screen: which shell, where, or that it has exited.</summary>
    [ObservableProperty]
    private string _terminalStatus = "";

    partial void OnBottomTabChanged(int value)
    {
        if (value == TerminalPanel && Terminal is null)
        {
            _ = StartTerminalAsync(TerminalFolder());
        }
    }

    private string TerminalFolder() => Project?.Root ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Show the Terminal panel, starting a shell if none is running.</summary>
    [RelayCommand]
    public async Task ShowTerminalAsync()
    {
        BottomTab = TerminalPanel;
        if (Terminal is null or { ExitCode: not null })
        {
            await StartTerminalAsync(TerminalFolder());
        }
    }

    /// <summary>Stop the running shell, if any, and start a new one in the project's folder.</summary>
    [RelayCommand]
    private Task NewTerminalAsync() => StartTerminalAsync(TerminalFolder());

    /// <summary>Stop the shell and everything it runs.</summary>
    [RelayCommand]
    private async Task KillTerminalAsync()
    {
        TerminalSession? old = Terminal;
        Terminal = null;
        if (old is not null)
        {
            await old.DisposeAsync();
            TerminalStatus = "The shell was stopped. New starts another.";
        }
    }

    /// <summary>Show the terminal in <paramref name="folder"/>: <c>cd</c> there in the running shell, or start one there.</summary>
    public async Task OpenTerminalInAsync(string folder)
    {
        BottomTab = TerminalPanel;
        if (Terminal is { ExitCode: null } running)
        {
            running.Run("cd " + Quote(folder));
        }
        else
        {
            await StartTerminalAsync(folder);
        }
    }

    /// <summary>Start a shell in <paramref name="folder"/>, stopping the one running.</summary>
    public async Task<TerminalSession?> StartTerminalAsync(string folder)
    {
        TerminalSession? old = Terminal;
        Terminal = null;
        if (old is not null)
        {
            await old.DisposeAsync();
        }
        try
        {
            TerminalSession s = await TerminalSession.StartAsync(folder, 120, 24);
            s.Exited += code =>
            {
                if (Terminal == s)
                {
                    TerminalStatus = $"The shell exited (code {code}). New starts another.";
                }
            };
            Terminal = s;
            TerminalStatus = $"{Path.GetFileName(s.Shell)} in {folder}";
            return s;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            TerminalStatus = "Could not start a shell: " + e.Message;
            Log("Terminal: " + e.Message);
            return null;
        }
    }

    /// <summary>A path quoted for the shell.</summary>
    private static string Quote(string path) => OperatingSystem.IsWindows()
        ? "\"" + path.Replace("\"", "`\"", StringComparison.Ordinal) + "\""
        : "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
