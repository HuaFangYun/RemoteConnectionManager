using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using RemoteConnectionManager.Core.Models;
using RemoteConnectionManager.Core.Services;
using RemoteConnectionManager.Core.Storage;
using RemoteConnectionManager.Data.SqlServer;
using RemoteConnectionManager.Plugins;

namespace RemoteConnectionManager.App.Views;

/// <summary>One plugin in the Settings list. Status is filled in after the window opens.</summary>
public sealed partial class PluginRow : ObservableObject
{
    public IRcmPlugin Plugin { get; }
    public string Name => Plugin.Name;
    public string Description => Plugin.Description;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _status = "Checking...";
    /// <summary>Null while checking.</summary>
    [ObservableProperty] private bool? _ready;

    public PluginRow(IRcmPlugin plugin, bool enabled)
    {
        Plugin = plugin;
        _enabled = enabled;
    }
}

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly List<PluginRow> _rows;
    private readonly CancellationTokenSource _statusCts = new();

    public SettingsWindow(AppSettings settings, PluginRegistry plugins)
    {
        _settings = settings;
        InitializeComponent();
        PathBox.Text = settings.ConnectionsFilePath ?? string.Empty;
        ServerBox.Text = settings.Sql.Server;
        DatabaseBox.Text = settings.Sql.Database;
        AuthBox.SelectedIndex = (int)settings.Sql.Authentication;
        TrustCertBox.IsChecked = settings.Sql.TrustServerCertificate;
        PollSecondsBox.Text = settings.Sql.PollSeconds.ToString();
        StorageBox.SelectedIndex = settings.Storage == StorageKind.SqlServer ? 1 : 0;
        UpdateStoragePanels();
        DefaultText.Text = $"Default: {AppPaths.DefaultConnectionsFile}";
        OpPathBox.Text = settings.OnePasswordCliPath ?? string.Empty;
        var logs = settings.SessionLogs;
        LogEnabledBox.IsChecked = logs.Enabled;
        LogRedactBox.IsChecked = logs.Redact;
        LogRawBox.IsChecked = logs.KeepRawCopy;
        LogFolderBox.Text = logs.Folder ?? string.Empty;
        LogKeepDaysBox.Text = logs.KeepDays.ToString();
        LogDefaultText.Text = $"Blank = {AppPaths.SessionLogDirectory}. Logs go in a folder per month.";

        _rows = plugins.All.Select(p => new PluginRow(p, plugins.IsEnabled(p))).ToList();
        PluginList.ItemsSource = _rows;
        Loaded += async (_, _) => await LoadStatusesAsync();
        Closed += (_, _) => _statusCts.Cancel();
    }

    private async Task LoadStatusesAsync()
    {
        await Task.WhenAll(_rows.Select(async row =>
        {
            try
            {
                var status = await row.Plugin.GetStatusAsync(_statusCts.Token);
                row.Status = status.Text;
                row.Ready = status.Ready;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                row.Status = $"Status check failed: {ex.Message}";
                row.Ready = false;
            }
        }));
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Choose the connections file",
            Filter = "JSON (*.json)|*.json",
            FileName = "connections.json",
            OverwritePrompt = false,
            CheckFileExists = false,
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(PathBox.Text)) ? Path.GetDirectoryName(PathBox.Text) : AppPaths.RoamingRoot,
        };
        if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FileName;
    }

    private void BrowseOp_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Find the 1Password CLI (op.exe)",
            Filter = "op.exe|op.exe|Programs (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) OpPathBox.Text = dialog.FileName;
    }

    private void UseDefault_Click(object sender, RoutedEventArgs e) => PathBox.Text = string.Empty;

    private string EffectiveLogFolder => string.IsNullOrWhiteSpace(LogFolderBox.Text)
        ? AppPaths.SessionLogDirectory
        : Environment.ExpandEnvironmentVariables(LogFolderBox.Text.Trim());

    private void BrowseLogFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the session log folder",
            InitialDirectory = Directory.Exists(EffectiveLogFolder) ? EffectiveLogFolder : AppPaths.LocalRoot,
        };
        if (dialog.ShowDialog(this) == true) LogFolderBox.Text = dialog.FolderName;
    }

    private void DefaultLogFolder_Click(object sender, RoutedEventArgs e) => LogFolderBox.Text = string.Empty;

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(EffectiveLogFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{EffectiveLogFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not open {EffectiveLogFolder}: {ex.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- storage ----------

    private bool SqlSelected => StorageBox.SelectedIndex == 1;

    private void StorageBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateStoragePanels();

    private void UpdateStoragePanels()
    {
        if (FilePanel is null || SqlPanel is null) return;
        FilePanel.Visibility = SqlSelected ? Visibility.Collapsed : Visibility.Visible;
        SqlPanel.Visibility = SqlSelected ? Visibility.Visible : Visibility.Collapsed;
    }

    private SqlStorageSettings SqlFromForm() => new()
    {
        Server = ServerBox.Text.Trim(),
        Database = DatabaseBox.Text.Trim(),
        Authentication = AuthBox.SelectedIndex < 0 ? SqlAuthentication.Windows : (SqlAuthentication)AuthBox.SelectedIndex,
        TrustServerCertificate = TrustCertBox.IsChecked == true,
        PollSeconds = _settings.Sql.PollSeconds,
    };

    /// <summary>Runs a database action off the UI thread with the buttons disabled, showing the result underneath.</summary>
    private async Task RunSqlAsync(string busyText, Func<SqlConnectionFactory, string> action)
    {
        var sql = SqlFromForm();
        if (!sql.IsConfigured)
        {
            ShowSqlStatus("Enter the server and database first.", ok: false);
            return;
        }
        TestButton.IsEnabled = UpgradeButton.IsEnabled = CopyButton.IsEnabled = false;
        ShowSqlStatus(busyText, ok: null);
        try
        {
            var factory = new SqlConnectionFactory(sql);
            var message = await Task.Run(() => action(factory));
            ShowSqlStatus(message, ok: true);
        }
        catch (Exception ex)
        {
            ShowSqlStatus(ex.Message, ok: false);
        }
        finally
        {
            TestButton.IsEnabled = UpgradeButton.IsEnabled = CopyButton.IsEnabled = true;
        }
    }

    private void ShowSqlStatus(string text, bool? ok)
    {
        SqlStatusText.Text = text;
        SqlStatusText.Foreground = (System.Windows.Media.Brush)FindResource(ok switch
        {
            true => "Chrome.Ok",
            false => "Chrome.Danger",
            null => "Chrome.TextMuted",
        });
    }

    private async void TestSql_Click(object sender, RoutedEventArgs e) =>
        await RunSqlAsync("Connecting...", factory => new SchemaManager(factory).GetStatus().Describe());

    private async void UpgradeSql_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, $"Create or upgrade the RCM tables in {DatabaseBox.Text.Trim()} on {ServerBox.Text.Trim()}?\n\nThis needs db_owner. If the tables are new, you become the first RCM admin.",
                "Create or upgrade tables", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await RunSqlAsync("Creating or upgrading the tables...", factory =>
        {
            var applied = new SchemaManager(factory).Upgrade();
            var status = new SchemaManager(factory).GetStatus();
            return applied.Count == 0
                ? $"Nothing to do. {status.Describe()}"
                : $"Applied schema version {string.Join(", ", applied)}. {status.Describe()}";
        });
    }

    private async void CopyIntoSql_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a connections file to copy into the database",
            Filter = "JSON (*.json)|*.json|All files|*.*",
            CheckFileExists = true,
            FileName = Path.GetFileName(_settings.EffectiveConnectionsFile),
            InitialDirectory = Path.GetDirectoryName(_settings.EffectiveConnectionsFile),
        };
        if (dialog.ShowDialog(this) != true) return;
        var file = dialog.FileName;
        await RunSqlAsync("Copying...", factory =>
        {
            var source = new FileConnectionStore(file).Load();
            using var store = new SqlConnectionStore(factory, cachePath: null);
            var target = store.Load();
            if (!store.IsAdmin) throw new InvalidOperationException("Copying a file in adds top level folders, which needs an RCM admin.");
            var (folders, entries) = DocumentMerge.AppendInto(target, source);
            store.Save(target);
            return $"Copied {folders} folders and {entries} entries from {Path.GetFileName(file)}. Give groups access with Permissions on each folder; until then only RCM admins see them.";
        });
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim();
        if (path.Length > 0)
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir) || !Path.IsPathRooted(path))
            {
                MessageBox.Show(this, "Enter a full path to a .json file, or leave it blank for the default.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        var opPath = OpPathBox.Text.Trim().Trim('"');
        if (opPath.Length > 0 && !File.Exists(opPath))
        {
            MessageBox.Show(this, $"{opPath} does not exist. Fix the path or leave it blank to search for op.exe.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var sql = SqlFromForm();
        if (SqlSelected && !sql.IsConfigured)
        {
            MessageBox.Show(this, "Enter the SQL Server name and database, or choose Local file.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(PollSecondsBox.Text.Trim(), out var pollSeconds) ||
            pollSeconds < SqlStorageSettings.MinPollSeconds || pollSeconds > SqlStorageSettings.MaxPollSeconds)
        {
            MessageBox.Show(this, $"Enter how often to check the database as a whole number of seconds, from {SqlStorageSettings.MinPollSeconds} to {SqlStorageSettings.MaxPollSeconds}.",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            PollSecondsBox.Focus();
            return;
        }
        if (!int.TryParse(LogKeepDaysBox.Text.Trim(), out var keepDays) || keepDays < 0 || keepDays > 36500)
        {
            MessageBox.Show(this, "Enter how many days to keep session logs as a whole number, or 0 to keep them forever.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            LogKeepDaysBox.Focus();
            return;
        }
        var logFolder = LogFolderBox.Text.Trim().Trim('"');
        if (logFolder.Length > 0 && !Path.IsPathRooted(Environment.ExpandEnvironmentVariables(logFolder)))
        {
            MessageBox.Show(this, "Enter a full path for the session log folder, or leave it blank for the default.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            LogFolderBox.Focus();
            return;
        }
        _settings.SessionLogs.Enabled = LogEnabledBox.IsChecked == true;
        _settings.SessionLogs.Redact = LogRedactBox.IsChecked == true;
        _settings.SessionLogs.KeepRawCopy = LogRawBox.IsChecked == true;
        _settings.SessionLogs.Folder = logFolder.Length == 0 ? null : logFolder;
        _settings.SessionLogs.KeepDays = keepDays;

        sql.PollSeconds = pollSeconds;
        _settings.Storage = SqlSelected ? StorageKind.SqlServer : StorageKind.File;
        _settings.Sql = sql;
        _settings.ConnectionsFilePath = path.Length == 0 ? null : path;
        _settings.OnePasswordCliPath = opPath.Length == 0 ? null : opPath;
        _settings.DisabledPlugins = _rows.Where(r => !r.Enabled).Select(r => r.Plugin.Id).ToList();
        DialogResult = true;
    }
}
