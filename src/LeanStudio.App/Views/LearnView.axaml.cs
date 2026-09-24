using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LeanStudio.App.Views;

/// <summary>The Learn panel: the tutorial lessons, the theorem gallery and the symbol palette. Its data context is a <see cref="ViewModels.LearnViewModel"/>.</summary>
public sealed partial class LearnView : UserControl
{
    /// <summary>Create the view and load its XAML.</summary>
    public LearnView() => AvaloniaXamlLoader.Load(this);
}
