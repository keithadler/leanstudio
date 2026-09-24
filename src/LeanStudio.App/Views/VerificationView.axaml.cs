using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using LeanStudio.App.ViewModels;

namespace LeanStudio.App.Views;

/// <summary>The Verification panel: Tenet's verdict for each declaration and the trail of what it rests on. Its data context is a <see cref="ViewModels.VerificationViewModel"/>.</summary>
public sealed partial class VerificationView : UserControl
{
    /// <summary>Create the view and load its XAML.</summary>
    public VerificationView() => AvaloniaXamlLoader.Load(this);

    private void OnVerdictDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: VerdictView v } && TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main)
        {
            main.OpenVerdictCommand.Execute(v);
        }
    }
}
