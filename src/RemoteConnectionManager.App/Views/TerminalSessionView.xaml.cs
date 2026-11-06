using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using RemoteConnectionManager.App.ViewModels;

namespace RemoteConnectionManager.App.Views;

public partial class TerminalSessionView : UserControl
{
    private TerminalSessionTabViewModel? _vm;

    public TerminalSessionView()
    {
        InitializeComponent();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = e.NewValue as TerminalSessionTabViewModel;
        if (_vm is null)
        {
            HostSlot.Content = null;
            return;
        }

        // A UIElement has one parent; take it back if another presenter still holds it.
        if (_vm.Control.Parent is ContentControl previous && !ReferenceEquals(previous, HostSlot)) previous.Content = null;
        if (!ReferenceEquals(HostSlot.Content, _vm.Control)) HostSlot.Content = _vm.Control;

        _vm.PropertyChanged += OnVmPropertyChanged;
        UpdateBorder();
        ApplySelection();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerminalSessionTabViewModel.IsSelected) or nameof(TerminalSessionTabViewModel.State))
            UpdateBorder();
        if (e.PropertyName == nameof(TerminalSessionTabViewModel.IsSelected))
            ApplySelection();
    }

    private void ApplySelection()
    {
        if (_vm is null) return;
        _vm.Control.SetForeground(_vm.IsSelected);
        if (_vm.IsSelected)
            Dispatcher.BeginInvoke(() => _vm.Control.FocusTerminal(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void UpdateBorder()
    {
        HostBorder.BorderBrush = _vm is { IsRunning: true }
            ? (System.Windows.Media.Brush)FindResource("Chrome.Accent")
            : System.Windows.Media.Brushes.Transparent;
    }
}
