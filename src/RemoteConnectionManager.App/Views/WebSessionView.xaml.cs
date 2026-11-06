using System.Windows;
using System.Windows.Controls;
using RemoteConnectionManager.App.ViewModels;

namespace RemoteConnectionManager.App.Views;

public partial class WebSessionView : UserControl
{
    public WebSessionView()
    {
        InitializeComponent();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not WebSessionTabViewModel vm)
        {
            HostSlot.Content = null;
            return;
        }
        // The tab view model owns the WebView2 so the page survives tab switches; take it back from
        // any presenter that still holds it (a UIElement has one parent).
        if (vm.Control.Parent is ContentControl previous && !ReferenceEquals(previous, HostSlot)) previous.Content = null;
        if (!ReferenceEquals(HostSlot.Content, vm.Control)) HostSlot.Content = vm.Control;
    }
}
