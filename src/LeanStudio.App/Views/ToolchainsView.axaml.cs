using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LeanStudio.App.Views;

/// <summary>The Toolchains panel: the Lean toolchains elan has installed, and installing Lean. Its data context is a <see cref="ViewModels.ToolchainsViewModel"/>.</summary>
public sealed partial class ToolchainsView : UserControl
{
    /// <summary>Create the view and load its XAML.</summary>
    public ToolchainsView() => AvaloniaXamlLoader.Load(this);

    private void OnInstallLean(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is ViewModels.MainViewModel main)
        {
            main.InstallLeanCommand.Execute(null);
        }
    }
}
