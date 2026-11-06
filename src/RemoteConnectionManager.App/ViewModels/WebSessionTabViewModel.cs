using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using RemoteConnectionManager.App.Services;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;

namespace RemoteConnectionManager.App.ViewModels;

/// <summary>
/// A website opened inside RCM with WebView2 (Edge). Owns the WebView2 control so the page survives
/// switching tabs. Signs in with the Windows account where the site supports it. An untrusted
/// certificate (common on switch and firewall admin pages) stops the page with a bar that offers
/// to continue for this tab only, or to open the page in the browser instead.
/// </summary>
public sealed partial class WebSessionTabViewModel : TabViewModelBase, IDisposable
{
    private static Task<CoreWebView2Environment>? _environment;

    private readonly AppLog _log;
    private readonly HashSet<string> _trustedHosts = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    [ObservableProperty] private string _address;
    [ObservableProperty] private string _statusMessage = "Loading";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _certificateMessage;
    private string? _certificateHost;

    public WebConnection Entry { get; }
    public Uri StartUri { get; }
    public WebView2 Control { get; }

    public override Guid? EntryId => Entry.Id;
    public override bool IsLive => !_disposed;

    public WebSessionTabViewModel(WebConnection entry, Uri uri, AppLog log)
    {
        Entry = entry;
        StartUri = uri;
        _log = log;
        _address = uri.ToString();
        Title = $"{entry.Prefix} {entry.Name}";
        Glyph = "";
        IconBrushKey = "Icon.Website";
        Control = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x1e, 0x1e, 0x1e) };
        _ = InitialiseAsync();
    }

    private static Task<CoreWebView2Environment> Environment() =>
        _environment ??= CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDataDirectory,
            new CoreWebView2EnvironmentOptions { AllowSingleSignOnUsingOSPrimaryAccount = true });

    private async Task InitialiseAsync()
    {
        try
        {
            await Control.EnsureCoreWebView2Async(await Environment());
        }
        catch (Exception ex)
        {
            _environment = null;
            ErrorMessage = ex is WebView2RuntimeNotFoundException
                ? "The Microsoft Edge WebView2 runtime is not installed, so pages cannot open inside RCM. Use Open in browser."
                : $"The page view could not start: {ex.Message}";
            StatusMessage = "Not available";
            _log.Error($"[{Entry.Name}] WebView2 failed to start", ex);
            return;
        }
        if (_disposed) return;

        var core = Control.CoreWebView2;
        core.Settings.IsStatusBarEnabled = true;
        core.NavigationStarting += (_, e) =>
        {
            ErrorMessage = null;
            StatusMessage = "Loading";
            Address = e.Uri;
        };
        core.NavigationCompleted += (_, e) =>
        {
            Address = core.Source;
            StatusMessage = e.IsSuccess ? core.DocumentTitle : $"Failed: {e.WebErrorStatus}";
            BackCommand.NotifyCanExecuteChanged();
            ForwardCommand.NotifyCanExecuteChanged();
        };
        core.DocumentTitleChanged += (_, _) => StatusMessage = core.DocumentTitle;
        core.ServerCertificateErrorDetected += OnCertificateError;
        // Links that would open a new window stay in this tab.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) && target.Scheme is "http" or "https") core.Navigate(target.ToString());
        };
        core.Navigate(StartUri.ToString());
    }

    private void OnCertificateError(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
    {
        var host = Uri.TryCreate(e.RequestUri, UriKind.Absolute, out var uri) ? uri.Host : e.RequestUri;
        if (_trustedHosts.Contains(host))
        {
            e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
            return;
        }
        e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        _certificateHost = host;
        CertificateMessage = $"{host} has a certificate this PC does not trust ({e.ErrorStatus}). Usual for a switch or firewall's own admin page.";
        _log.Warn($"[{Entry.Name}] certificate not trusted for {host}: {e.ErrorStatus}");
    }

    [RelayCommand]
    private void ContinueOnce()
    {
        if (_certificateHost is null || Control.CoreWebView2 is null) return;
        _trustedHosts.Add(_certificateHost);
        _log.Info($"[{Entry.Name}] continuing past the certificate warning for {_certificateHost} in this tab");
        CertificateMessage = null;
        Control.CoreWebView2.Navigate(Address);
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => Control.CoreWebView2?.GoBack();
    private bool CanGoBack() => Control.CoreWebView2?.CanGoBack == true;

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void Forward() => Control.CoreWebView2?.GoForward();
    private bool CanGoForward() => Control.CoreWebView2?.CanGoForward == true;

    [RelayCommand]
    private void Reload()
    {
        if (Control.CoreWebView2 is null) _ = InitialiseAsync();
        else Control.CoreWebView2.Reload();
    }

    [RelayCommand]
    private void Home() => Control.CoreWebView2?.Navigate(StartUri.ToString());

    [RelayCommand]
    private void OpenInBrowser() => OpenExternally(Uri.TryCreate(Address, UriKind.Absolute, out var u) ? u : StartUri, _log);

    [RelayCommand]
    private void CopyAddress()
    {
        try { System.Windows.Clipboard.SetText(Address); } catch { }
    }

    /// <summary>Opens an http or https address in the Windows default browser.</summary>
    public static void OpenExternally(Uri uri, AppLog log)
    {
        if (uri.Scheme is not ("http" or "https")) return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            log.Error($"Could not open {uri} in the default browser", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Control.Dispose(); } catch { }
    }
}
