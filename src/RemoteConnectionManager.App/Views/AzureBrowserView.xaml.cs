using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RemoteConnectionManager.App.ViewModels;

namespace RemoteConnectionManager.App.Views;

public partial class AzureBrowserView : UserControl
{
    public AzureBrowserView()
    {
        InitializeComponent();
    }

    private AzureBrowserTabViewModel? Vm => DataContext as AzureBrowserTabViewModel;

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (Vm is { } vm) vm.SelectedNode = e.NewValue as AzureNodeViewModel;
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a double click on a vault row opens it; on a group it just expands.
        if (Vm is not { } vm || vm.SelectedNode?.Vault is null) return;
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != Tree; d = VisualTreeHelper.GetParent(d))
        {
            if (d is TreeViewItem { DataContext: AzureNodeViewModel { IsVault: true } })
            {
                vm.OpenSelectedCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm is not { } vm) return;
        vm.OpenNamedCommand.Execute(null);
        e.Handled = true;
    }
}
