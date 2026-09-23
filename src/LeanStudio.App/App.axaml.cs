using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using LeanStudio.App.Services;
using LeanStudio.App.Views;

namespace LeanStudio.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Where crashes are written: the settings folder, crash.log.</summary>
    public static string CrashLog => Path.Combine(Settings.Directory, "crash.log");

    private static void Record(string where, Exception e)
    {
        try
        {
            Directory.CreateDirectory(Settings.Directory);
            File.AppendAllText(CrashLog, $"{DateTime.Now:u} {where}\n{e}\n\n");
        }
        catch (Exception io) when (io is IOException or UnauthorizedAccessException)
        {
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // A bug should cost the operation that hit it, not the session: log it, say so, keep going.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Record("unhandled", (Exception)e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Record("unobserved task", e.Exception);
            e.SetObserved();
        };
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Record("UI", e.Exception);
            e.Handled = true;
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow w })
            {
                w.ViewModel.Log($"Something went wrong ({e.Exception.GetType().Name}: {e.Exception.Message}). Lean Studio kept running; details are in {CrashLog}.");
            }
        };
        Settings settings = Settings.Load();
        RequestedThemeVariant = settings.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(settings);
            desktop.MainWindow = window;
            string? arg = desktop.Args?.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
            window.NewWindow = desktop.Args?.Contains("--new-window") == true;
            if (arg is not null)
            {
                window.OpenOnStartup = Path.GetFullPath(arg);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
