using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using LeanStudio.App.Services;
using LeanStudio.App.Views;

namespace LeanStudio.App;

/// <summary>
/// The Avalonia application: loads the theme from the settings, opens the main window with the file named on the
/// command line (or handed over by Finder or <c>open</c>), names icon-and-text controls for accessibility, and logs
/// unhandled exceptions to <see cref="CrashLog"/> instead of letting them end the session.
/// </summary>
public sealed partial class App : Application
{
    /// <inheritdoc/>
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Where crashes are written: the settings folder, crash.log.</summary>
    public static string CrashLog => Path.Combine(Settings.Directory, "crash.log");

    /// <summary>Append <paramref name="e"/> to <see cref="CrashLog"/>, saying where it happened.</summary>
    internal static void Record(string where, Exception e)
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

    /// <inheritdoc/>
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
            string? arg = desktop.Args?.FirstOrDefault(a => !a.StartsWith('-'));
            window.NewWindow = desktop.Args?.Contains("--new-window") == true;
            if (arg is not null)
            {
                window.OpenOnStartup = Path.GetFullPath(arg);
            }
            // A quit asked for by AppleScript or the Dock is answered by Lean Studio itself, so it reports success.
            window.Opened += (_, _) => MacQuitEvent.Install(() => desktop.TryShutdown());
            // Finder, `open` and the Dock hand files over as an event, not as arguments: at launch and while running.
            if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e is FileActivatedEventArgs files)
                    {
                        foreach (IStorageItem item in files.Files)
                        {
                            if (item.TryGetLocalPath() is string path)
                            {
                                _ = window.OpenFromSystemAsync(path);
                            }
                        }
                    }
                };
            }
        }
        NameControlsForAccessibility();
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Give every button and menu item that shows an icon beside its text an accessible name: without one, screen
    /// readers (and the tools that drive the app through accessibility) are given the type of its content,
    /// "Avalonia.Controls.StackPanel". The name is the control's visible text, or its tooltip when it has none;
    /// one set in XAML is kept.
    /// </summary>
    private static void NameControlsForAccessibility()
    {
        Control.LoadedEvent.AddClassHandler<Button>((b, _) => NameFrom(b, b.Content));
        Control.LoadedEvent.AddClassHandler<MenuItem>((m, _) => NameFrom(m, m.Header));
    }

    private static void NameFrom(Control control, object? content)
    {
        // A string is read out as it is, and it may change; only content that is itself a control needs a name.
        if (content is not Control inner || !string.IsNullOrEmpty(AutomationProperties.GetName(control)))
        {
            return;
        }
        string? text = (inner is TextBlock single ? [single] : inner.GetLogicalDescendants().OfType<TextBlock>())
            .Select(t => t.Text)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
            ?? ToolTip.GetTip(control) as string;
        if (!string.IsNullOrWhiteSpace(text))
        {
            AutomationProperties.SetName(control, text.Trim());
        }
    }
}
