using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LeanStudio.App.Views;

/// <summary>The welcome screen: new project, open folder or file, the tutorial, recent projects, tips and credits. Its data context is the <see cref="ViewModels.MainViewModel"/>.</summary>
public sealed partial class WelcomeView : UserControl
{
    /// <summary>Create the view and load its XAML.</summary>
    public WelcomeView() => AvaloniaXamlLoader.Load(this);

    private async void OnXLink(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is TopLevel top)
        {
            await top.Launcher.LaunchUriAsync(new Uri(Services.Credits.XUrl));
        }
    }
}
