namespace RemoteConnectionManager.Core.Services;

public static class AppPaths
{
    public const string AppFolderName = "RemoteConnectionManager";

    public static string RoamingRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    public static string LocalRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    public static string DefaultConnectionsFile => Path.Combine(RoamingRoot, "connections.json");
    public static string SettingsFile => Path.Combine(RoamingRoot, "settings.json");
    public static string LogDirectory => Path.Combine(LocalRoot, "logs");
    public static string SessionLogDirectory => Path.Combine(LocalRoot, "session-logs");
    /// <summary>WebView2 profile (cookies, cache) for website tabs.</summary>
    public static string WebViewDataDirectory => Path.Combine(LocalRoot, "WebView2");

    /// <summary>Last copy of the shared tree for one database, used when it cannot be reached.</summary>
    public static string SqlCacheFile(string server, string database, string suffix = "tree") =>
        Path.Combine(LocalRoot, "sql-cache", $"{Slug(server)}_{Slug(database)}_{suffix}.json");

    private static string Slug(string value) =>
        string.Concat(value.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '_'));

    /// <summary>Azure sign in record: account name and ids only, no tokens.</summary>
    public static string AzureAuthRecordFile => Path.Combine(LocalRoot, "azure-account.json");
}
