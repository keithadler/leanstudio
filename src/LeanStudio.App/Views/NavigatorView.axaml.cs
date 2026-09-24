using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LeanStudio.App.Views;

/// <summary>The Navigator panel: browse the declarations the project can see, and search Mathlib in plain English (LeanSearch) or by pattern (Loogle). Its data context is a <see cref="ViewModels.NavigatorViewModel"/>.</summary>
public sealed partial class NavigatorView : UserControl
{
    /// <summary>Create the view and load its XAML.</summary>
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
