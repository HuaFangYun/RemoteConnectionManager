using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RemoteConnectionManager.App.ViewModels;

namespace RemoteConnectionManager.App.Views;

public partial class ShellWindow : Window
{
    private readonly ShellViewModel _vm;

    public ShellWindow(ShellViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Bring detached sessions home while this window can still host them.
        _vm.DockAllSessions();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.PersistSettings();
        base.OnClosed(e);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.G && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            GeneratePassword_Click(this, e);
            e.Handled = true;
        }
        else if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    // ----- tree -----

    private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) =>
        _vm.SelectedNode = e.NewValue as TreeNodeViewModel;

    // A WPF TreeView does not select on right click, so the context menu would act on whatever was
    // selected before (or nothing). Select the item under the mouse first, like Explorer does.
    private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var d = e.OriginalSource as DependencyObject;
        while (d is not null and not TreeViewItem)
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        if (d is TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedNode?.Entry is { } entry && e.OriginalSource is DependencyObject source && IsInsideHeader(source))
        {
            _vm.OpenEntry(entry);
            e.Handled = true;
        }
    }

    private void Tree_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when _vm.SelectedNode?.Entry is { } entry:
                _vm.OpenEntry(entry);
                e.Handled = true;
                break;
            case Key.Delete when _vm.DeleteCommand.CanExecute(null):
                _vm.DeleteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F2 when _vm.EditCommand.CanExecute(null):
                _vm.EditCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Double clicks on the expander or empty tree space should not connect.</summary>
    private static bool IsInsideHeader(DependencyObject source)
    {
        for (var d = source; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ToggleButton) return false;
            if (d is ContentPresenter { Name: "PART_Header" }) return true;
            if (d is TreeViewItem) return false;
        }
        return false;
    }

    // ----- command bar -----

    private void DropDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void Rail_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        switch (Rail.SelectedIndex)
        {
            case 0:
                if (!_vm.NavigationVisible) _vm.NavigationVisible = true;
                break;
            case 1:
                _vm.SelectedTab = _vm.Dashboard;
                break;
            case 2:
                _vm.FilterText = _vm.FilterText == "favourite" ? string.Empty : "favourite";
                break;
        }
    }

    private void AzureAccount_Click(object sender, MouseButtonEventArgs e) => _vm.OpenAzureBrowserCommand.Execute(null);

    private void GeneratePassword_Click(object sender, RoutedEventArgs e) =>
        PasswordGeneratorPanel.Show(GeneratePasswordButton, "Copy", _vm.CopyGeneratedPassword);

    private void QuickBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.QuickConnectCommand.Execute(null);
            e.Handled = true;
        }
    }
}
