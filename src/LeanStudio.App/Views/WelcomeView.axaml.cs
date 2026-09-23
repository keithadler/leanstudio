using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LeanStudio.App.Views;

public sealed partial class WelcomeView : UserControl
{
    public WelcomeView() => AvaloniaXamlLoader.Load(this);

    private async void OnXLink(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is TopLevel top)
        {
            await top.Launcher.LaunchUriAsync(new Uri(Services.Credits.XUrl));
        }
    }
}
