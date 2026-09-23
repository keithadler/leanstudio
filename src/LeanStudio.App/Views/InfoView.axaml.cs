using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using LeanStudio.App.ViewModels;

namespace LeanStudio.App.Views;

public sealed partial class InfoView : UserControl
{
    public InfoView() => AvaloniaXamlLoader.Load(this);

    private void OnStepTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ProofStepView step } && DataContext is InfoViewModel vm)
        {
            vm.GoToStepCommand.Execute(step);
        }
    }
}
