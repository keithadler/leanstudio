using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using LeanStudio.App.ViewModels;

namespace LeanStudio.App.Views;

public sealed partial class VerificationView : UserControl
{
    public VerificationView() => AvaloniaXamlLoader.Load(this);

    private void OnVerdictDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: VerdictView v } && TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main)
        {
            main.OpenVerdictCommand.Execute(v);
        }
    }
}
