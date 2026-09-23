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

    public override void OnFrameworkInitializationCompleted()
    {
        Settings settings = Settings.Load();
        RequestedThemeVariant = settings.Theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(settings);
            desktop.MainWindow = window;
            string? arg = desktop.Args?.FirstOrDefault();
            if (arg is not null)
            {
                window.OpenOnStartup = Path.GetFullPath(arg);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
