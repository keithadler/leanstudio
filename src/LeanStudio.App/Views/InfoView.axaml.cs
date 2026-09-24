using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using LeanStudio.App.ViewModels;

namespace LeanStudio.App.Views;

/// <summary>The infoview: the tactic state at the caret, messages, proof steps and proof search results. Its data context is an <see cref="ViewModels.InfoViewModel"/>.</summary>
public sealed partial class InfoView : UserControl
{
    /// <summary>Create the view and load its XAML.</summary>
    public InfoView() => AvaloniaXamlLoader.Load(this);

    private void OnStepTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ProofStepView step } && DataContext is InfoViewModel vm)
        {
            vm.GoToStepCommand.Execute(step);
        }
    }
}
