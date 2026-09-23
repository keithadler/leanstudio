using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LeanStudio.App.Views;

public sealed partial class LearnView : UserControl
{
    public LearnView() => AvaloniaXamlLoader.Load(this);
}
