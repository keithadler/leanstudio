using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LeanStudio.App.Views;

public sealed partial class WelcomeView : UserControl
{
    public WelcomeView() => AvaloniaXamlLoader.Load(this);
}
