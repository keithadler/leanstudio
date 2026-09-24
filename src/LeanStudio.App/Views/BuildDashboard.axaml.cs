using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LeanStudio.App.Views;

/// <summary>
/// The whole of a long task (a build, the Mathlib cache, Tenet) over the editor: its stage, the bar and the time
/// left, the modules compiling now, the slowest so far, and warnings as they come. It shows by itself when no file
/// is open, and from the progress banner's Details button. Its data context is the <see cref="ViewModels.MainViewModel"/>.
/// </summary>
public sealed partial class BuildDashboard : UserControl
{
    /// <summary>Create the view and load its XAML.</summary>
    public BuildDashboard() => AvaloniaXamlLoader.Load(this);
}
