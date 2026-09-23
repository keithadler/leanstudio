using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using LeanStudio.App.ViewModels;

namespace LeanStudio.App.Views;

public sealed partial class SourceControlView : UserControl
{
    private bool _loading;

    public SourceControlView() => AvaloniaXamlLoader.Load(this);

    private SourceControlViewModel? Vm => DataContext as SourceControlViewModel;

    private void OnInit(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel main && main.Project is not null)
        {
            Vm?.InitCommand.Execute(main.Project.Root);
        }
    }

    private void OnBranchChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (!_loading && sender is ComboBox { SelectedItem: string branch } && Vm is { } vm && branch != vm.Branch && e.RemovedItems.Count > 0)
        {
            vm.SwitchBranchCommand.Execute(branch);
        }
    }

    private void OnChangeTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ChangeView c })
        {
            Vm?.OpenChangeCommand.Execute(c);
        }
    }

    private void OnChangeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: ChangeView c })
        {
            Vm?.OpenChangedFileCommand.Execute(c);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        _loading = true;
        base.OnDataContextChanged(e);
        _loading = false;
    }
}
