using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LeanStudio.App.Views;

public sealed partial class ToolchainsView : UserControl
{
    public ToolchainsView() => AvaloniaXamlLoader.Load(this);

    private void OnInstallLean(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is ViewModels.MainViewModel main)
        {
            main.InstallLeanCommand.Execute(null);
        }
    }
}
