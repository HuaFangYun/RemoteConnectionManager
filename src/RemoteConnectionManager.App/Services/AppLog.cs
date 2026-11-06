using System.IO;
using RemoteConnectionManager.Core.Services;

namespace RemoteConnectionManager.App.Services;

/// <summary>Plain text daily log under %LOCALAPPDATA%\RemoteConnectionManager\logs. Never logs secrets.</summary>
public sealed class AppLog
{
    private readonly object _gate = new();
    private readonly string _path;

    public AppLog()
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        _path = System.IO.Path.Combine(AppPaths.LogDirectory, $"rcm-{DateTime.Now:yyyyMMdd}.log");
        PruneOlderThan(TimeSpan.FromDays(14));
    }

    public string Path => _path;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level,-5} {message}{Environment.NewLine}";
        lock (_gate)
        {
            try { File.AppendAllText(_path, line); } catch { /* logging must never throw */ }
        }
    }

    private static void PruneOlderThan(TimeSpan age)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(AppPaths.LogDirectory, "rcm-*.log"))
                if (DateTime.Now - File.GetLastWriteTime(file) > age) File.Delete(file);
        }
        catch { }
    }
}
