using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using RemoteConnectionManager.App.ViewModels;
using RemoteConnectionManager.Core.Services;

namespace RemoteConnectionManager.App.Views;

/// <summary>
/// Password generator shown in a popup under a button. The accent button hands the value to the
/// caller (fill a field, or copy to the clipboard) and closes. Options are remembered in settings.
/// </summary>
public partial class PasswordGeneratorPanel : UserControl
{
    private readonly PasswordGeneratorViewModel _vm;
    private readonly Action<string> _onAction;
    private Popup? _popup;

    private PasswordGeneratorPanel(PasswordGeneratorViewModel vm, Action<string> onAction)
    {
        _vm = vm;
        _onAction = onAction;
        DataContext = vm;
        InitializeComponent();
        vm.PropertyChanged += Vm_PropertyChanged;
        ShowPreview();
        PreviewKeyDown += Panel_PreviewKeyDown;
    }

    /// <summary>Opens the generator under <paramref name="anchor"/>. <paramref name="actionLabel"/> is "Use" or "Copy".</summary>
    public static void Show(FrameworkElement anchor, string actionLabel, Action<string> onAction)
    {
        var settings = ((App)Application.Current).Services.GetRequiredService<AppSettings>();
        var panel = new PasswordGeneratorPanel(new PasswordGeneratorViewModel(settings.PasswordGenerator ?? new PasswordOptions(), actionLabel), onAction);
        var popup = new Popup
        {
            Child = panel,
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            // Closing is handled here rather than by StaysOpen=False, which stops closing on an
            // outside click once a ComboBox inside has opened and closed its own dropdown.
            StaysOpen = true,
        };
        panel._popup = popup;

        var window = Window.GetWindow(anchor);
        MouseButtonEventHandler outsideClick = (_, _) => popup.IsOpen = false;
        EventHandler deactivated = (_, _) => popup.IsOpen = false;
        SizeChangedEventHandler resized = (_, _) => popup.IsOpen = false;
        if (window is not null)
        {
            // The popup is its own window, so a mouse press that reaches the owner is outside it.
            window.PreviewMouseDown += outsideClick;
            window.Deactivated += deactivated;
            window.LocationChanged += deactivated;
            window.SizeChanged += resized;
        }
        popup.Closed += (_, _) =>
        {
            if (window is not null)
            {
                window.PreviewMouseDown -= outsideClick;
                window.Deactivated -= deactivated;
                window.LocationChanged -= deactivated;
                window.SizeChanged -= resized;
            }
            settings.PasswordGenerator = panel._vm.Options;
            panel._vm.PropertyChanged -= panel.Vm_PropertyChanged;
            panel.Preview.Inlines.Clear();
            popup.Child = null;
        };
        popup.Opened += (_, _) => panel.ActionButton.Focus();
        popup.IsOpen = true;
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PasswordGeneratorViewModel.Password)) ShowPreview();
    }

    private void ShowPreview()
    {
        var digit = (System.Windows.Media.Brush)FindResource("Chrome.AccentLight");
        var symbol = (System.Windows.Media.Brush)FindResource("Icon.Website");
        Preview.Inlines.Clear();
        var value = _vm.Password;
        var start = 0;
        // Group runs of the same class so a 100 character value is a handful of Runs, not 100.
        for (var i = 1; i <= value.Length; i++)
        {
            if (i < value.Length && Class(value[i]) == Class(value[start])) continue;
            var run = new Run(value[start..i]);
            switch (Class(value[start]))
            {
                case 1: run.Foreground = digit; break;
                case 2: run.Foreground = symbol; break;
            }
            Preview.Inlines.Add(run);
            start = i;
        }
    }

    private static int Class(char c) => char.IsAsciiDigit(c) ? 1 : char.IsAsciiLetter(c) ? 0 : 2;

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        var value = _vm.Password;
        Close();
        _onAction(value);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Close()
    {
        if (_popup is not null) _popup.IsOpen = false;
    }

    private void Panel_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ItemsControlFromItemContainer(d) is ComboBox) return;
        switch (e.Key)
        {
            case Key.Escape when !IsDropDownOpen():
                Close();
                e.Handled = true;
                break;
            case Key.F5:
                _vm.RegenerateCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private bool IsDropDownOpen() => Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true };

    // Enter in the length box applies it straight away (the binding otherwise waits for focus to leave).
    private void LengthBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        box.SelectAll();
        e.Handled = true;
    }
}
