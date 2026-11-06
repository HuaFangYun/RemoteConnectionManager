using System.Windows;
using RemoteConnectionManager.App.ViewModels;

namespace RemoteConnectionManager.App.Views;

public partial class EntryEditorWindow : Window
{
    public EntryEditorWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is EntryEditorViewModel vm && !vm.Validate()) return;
        DialogResult = true;
    }
}
