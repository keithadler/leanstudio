using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LeanStudio.App.Views;

public sealed partial class NavigatorView : UserControl
{
    public NavigatorView() => AvaloniaXamlLoader.Load(this);
}
