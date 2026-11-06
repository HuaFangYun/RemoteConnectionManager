using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RemoteConnectionManager.App.Services;
using RemoteConnectionManager.App.ViewModels;
using RemoteConnectionManager.App.Views;
using RemoteConnectionManager.Core.Services;
using RemoteConnectionManager.KeyVault;
using RemoteConnectionManager.Plugins;
using RemoteConnectionManager.Plugins.OnePassword;
using RemoteConnectionManager.Plugins.ServerChecks;

namespace RemoteConnectionManager.App;

public partial class App : Application
{
    private IHost? _host;

    public IServiceProvider Services => _host!.Services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        var log = new AppLog();
        InstallGlobalExceptionHandlers(log);

        var settingsStore = new AppSettingsStore();
        var settings = settingsStore.Load();

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton(log);
                services.AddSingleton(settingsStore);
                services.AddSingleton(settings);
                services.AddSingleton(StorageBackend.Create(settings, settingsStore, log));
                services.AddSingleton<IDialogService, DialogService>();
                // The broker needs an owner window for its account picker; the shell is it.
                services.AddSingleton(new AzureAuth(
                    () => Current.MainWindow is { } w ? new System.Windows.Interop.WindowInteropHelper(w).Handle : IntPtr.Zero,
                    AppPaths.AzureAuthRecordFile));
                services.AddSingleton<VaultCatalog>();

                // In-box plugins. Each is registered as itself and as IRcmPlugin; the registry knows
                // which ones the user switched off (read live from settings, so Settings applies at once).
                services.AddSingleton<KeyVaultCredentialProvider>();
                services.AddSingleton(new OnePasswordCredentialProvider(new OnePasswordCli(() => settings.OnePasswordCliPath)));
                services.AddSingleton<CimServerInspector>();
                services.AddSingleton<IRcmPlugin>(sp => sp.GetRequiredService<KeyVaultCredentialProvider>());
                services.AddSingleton<IRcmPlugin>(sp => sp.GetRequiredService<OnePasswordCredentialProvider>());
                services.AddSingleton<IRcmPlugin>(sp => sp.GetRequiredService<CimServerInspector>());
                services.AddSingleton(sp => new PluginRegistry(sp.GetServices<IRcmPlugin>(), () => settings.DisabledPlugins));

                services.AddSingleton<ClipboardGuard>();
                services.AddSingleton<RdpCredentialService>();
                services.AddSingleton<SessionLogService>();
                services.AddSingleton<ISessionWindows, SessionWindowService>();
                services.AddSingleton<ShellViewModel>();
                services.AddSingleton<ShellWindow>();
            })
            .Build();

        var where = settings.Storage == RemoteConnectionManager.Core.Storage.StorageKind.SqlServer ? $"SQL Server {settings.Sql.Server}/{settings.Sql.Database}" : settings.EffectiveConnectionsFile;
        log.Info($"RemoteConnectionManager {typeof(App).Assembly.GetName().Version} starting. Connections: {where}");

        Services.GetRequiredService<SessionLogService>().PruneInBackground();

        var window = Services.GetRequiredService<ShellWindow>();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Services.GetService<ShellViewModel>()?.Shutdown();
        _host?.Dispose();
        base.OnExit(e);
    }

    private void InstallGlobalExceptionHandlers(AppLog log)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            log.Error("Unhandled UI exception", args.Exception);
            MessageBox.Show(args.Exception.Message, "RemoteConnectionManager", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }
}
