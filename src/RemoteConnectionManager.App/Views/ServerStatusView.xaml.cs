using System.Windows.Controls;
using System.Windows.Input;
using RemoteConnectionManager.App.ViewModels;

namespace RemoteConnectionManager.App.Views;

public partial class ServerStatusView : UserControl
{
    public ServerStatusView()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5 && DataContext is ServerStatusTabViewModel vm)
            {
                vm.RefreshCommand.Execute(null);
                e.Handled = true;
            }
        };
    }
}
