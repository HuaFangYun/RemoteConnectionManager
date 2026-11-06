using System.Windows;
using Microsoft.Win32;
using RemoteConnectionManager.App.ViewModels;

namespace RemoteConnectionManager.App.Views;

public partial class VaultItemEditorWindow : Window
{
    public VaultItemEditorWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is VaultItemEditorViewModel { NameEditable: false, ShowValue: true })
            {
                ValueBox.Focus();
            }
            else
            {
                NameBox.Focus();
                NameBox.SelectAll();
            }
        };
        // Do not leave the typed password in the control once the window is gone.
        Closed += (_, _) => PfxPassword.Clear();
    }

    private VaultItemEditorViewModel? Vm => DataContext as VaultItemEditorViewModel;

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose a certificate to import", Filter = "PFX certificate (*.pfx;*.p12)|*.pfx;*.p12|All files|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true || Vm is not { } vm) return;
        vm.ImportFile = dialog.FileName;
        if (string.IsNullOrWhiteSpace(vm.Name))
            vm.Name = SafeName(System.IO.Path.GetFileNameWithoutExtension(dialog.FileName));
    }

    private void Generate_Click(object sender, RoutedEventArgs e) =>
        PasswordGeneratorPanel.Show(GenerateButton, "Use", value =>
        {
            if (Vm is not { } vm) return;
            vm.Value = value;
            if (string.IsNullOrWhiteSpace(vm.ContentType)) vm.ContentType = "password";
            ValueBox.Focus();
            ValueBox.CaretIndex = ValueBox.Text.Length;
        });

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (!vm.Validate()) return;
        if (vm.ShowImport) vm.ImportPassword = PfxPassword.Password;
        DialogResult = true;
    }

    private static string SafeName(string text)
    {
        var chars = text.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-').ToArray();
        return new string(chars).Trim('-');
    }
}
