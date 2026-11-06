using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using RemoteConnectionManager.App.Services;
using RemoteConnectionManager.App.ViewModels;

namespace RemoteConnectionManager.App.Views;

/// <summary>
/// A session moved out of its tab. Windowed, or borderless over one whole display for full
/// screen. In full screen a bar slides in when the pointer touches the top edge of that display;
/// it is a separate topmost window because nothing can be drawn over the RDP surface (airspace).
/// Closing the window puts the session back in its tab; it does not disconnect.
/// </summary>
public partial class DetachedSessionWindow : Window
{
    private readonly RdpSessionTabViewModel _vm;
    private readonly DispatcherTimer _edgeTimer;
    private FullScreenBar? _bar;
    private System.Windows.Rect? _restoreBounds;
    private WindowState _restoreState = WindowState.Normal;
    private bool _isFullScreen;

    public DetachedSessionWindow(RdpSessionTabViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        _edgeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => CheckTopEdge(), Dispatcher);
        _edgeTimer.Stop();
        Activated += (_, _) => Dispatcher.BeginInvoke(() => _vm.Control.FocusSession(), DispatcherPriority.Input);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    /// <summary>Matches the window to the session's placement: windowed, or full screen on its chosen display.</summary>
    public void ApplyPlacement()
    {
        if (_vm.IsFullScreen && _vm.FullScreenMonitor is { } monitor) EnterFullScreen(monitor);
        else LeaveFullScreen();
    }

    private void EnterFullScreen(MonitorChoice monitor)
    {
        monitor = Monitors.Refresh(monitor) ?? monitor;
        if (!_isFullScreen)
        {
            _restoreState = WindowState;
            _restoreBounds = WindowState == WindowState.Normal ? new System.Windows.Rect(Left, Top, Width, Height) : RestoreBounds;
        }
        _isFullScreen = true;

        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        var bounds = ToDips(monitor);
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;

        _bar ??= new FullScreenBar(_vm);
        _bar.Monitor = monitor;
        _bar.Hide();
        _edgeTimer.Start();
        Activate();
    }

    private void LeaveFullScreen()
    {
        if (!_isFullScreen) return;
        _isFullScreen = false;
        _edgeTimer.Stop();
        _bar?.Hide();

        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        if (_restoreBounds is { } r)
        {
            Left = r.Left;
            Top = r.Top;
            Width = r.Width;
            Height = r.Height;
        }
        WindowState = _restoreState;
        DarkTitleBar.Apply(this);
        Activate();
    }

    /// <summary>Shows the bar while the pointer is at the very top of the full screen display.</summary>
    private void CheckTopEdge()
    {
        if (_bar?.Monitor is not { } monitor) return;
        var (x, y) = Monitors.CursorPosition();
        if (!_bar.IsVisible)
        {
            if (monitor.Contains(x, y) && y <= monitor.Top + 1 && IsActive) _bar.ShowAtTop();
        }
        else if (!_bar.IsBusy && (!monitor.Contains(x, y) || y > monitor.Top + _bar.HeightInPixels + 40))
        {
            _bar.Hide();
        }
    }

    /// <summary>Display bounds in this window's DIPs (one scale for a system DPI aware app).</summary>
    private System.Windows.Rect ToDips(MonitorChoice monitor)
    {
        var source = PresentationSource.FromVisual(this) ?? HwndSource.FromHwnd(new WindowInteropHelper(this).EnsureHandle());
        var m = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var topLeft = m.Transform(new System.Windows.Point(monitor.Left, monitor.Top));
        var size = m.Transform(new System.Windows.Point(monitor.Width, monitor.Height));
        return new System.Windows.Rect(topLeft.X, topLeft.Y, size.X, size.Y);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _edgeTimer.Stop();
        // Let go of the control before this window's HWND goes, or Windows would destroy it with us.
        View.Release();
        _bar?.Close();
        _bar = null;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _vm.OnWindowClosed();
    }
}
