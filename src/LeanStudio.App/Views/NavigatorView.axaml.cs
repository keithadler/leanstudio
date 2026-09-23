using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LeanStudio.App.Views;

public sealed partial class NavigatorView : UserControl
{
    public NavigatorView() => AvaloniaXamlLoader.Load(this);

    private void OnMeaningKey(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter && DataContext is ViewModels.NavigatorViewModel vm)
        {
            vm.SearchMeaningCommand.Execute(null);
        }
    }

    private void OnLoogleKey(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.Enter && DataContext is ViewModels.NavigatorViewModel vm)
        {
            vm.SearchLoogleCommand.Execute(null);
        }
    }
}
