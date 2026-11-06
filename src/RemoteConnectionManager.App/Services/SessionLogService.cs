using System.Diagnostics;
using System.IO;
using RemoteConnectionManager.Core.Services;
using RemoteConnectionManager.Core.SessionLogging;

namespace RemoteConnectionManager.App.Services;

/// <summary>
/// Starts session logs for terminal, SSH and telnet tabs and tidies the log folder. Settings are
/// read live, so a change in Settings applies to the next tab opened.
/// </summary>
public sealed class SessionLogService
{
    private readonly AppSettings _settings;
    private readonly AppLog _log;

    public SessionLogService(AppSettings settings, AppLog log)
    {
        _settings = settings;
        _log = log;
    }

    public SessionLogSettings Settings => _settings.SessionLogs;
    public string Folder => Settings.EffectiveFolder;

    /// <summary>A new log, or null (with a warning in the app log) when the folder cannot be written.</summary>
    public SessionLogWriter? Start(SessionLogInfo info, int columns, int rows)
    {
        try
        {
            var writer = new SessionLogWriter(Settings, info, columns, rows);
            writer.Failed += ex => _log.Error($"[{info.EntryName}] session log stopped: {writer.FilePath}", ex);
            _log.Info($"[{info.EntryName}] logging session to {writer.FilePath}");
            return writer;
        }
        catch (Exception ex)
        {
            _log.Error($"[{info.EntryName}] could not start a session log in {Folder}", ex);
            return null;
        }
    }

    /// <summary>Deletes logs older than the keep setting, off the UI thread.</summary>
    public void PruneInBackground()
    {
        var folder = Folder;
        var days = Settings.KeepDays;
        if (days <= 0) return;
        Task.Run(() =>
        {
            try
            {
                var removed = SessionLogFiles.Prune(folder, days);
                if (removed > 0) _log.Info($"Deleted {removed} session logs older than {days} days from {folder}");
            }
            catch (Exception ex)
            {
                _log.Warn($"Could not tidy session logs in {folder}: {ex.Message}");
            }
        });
    }

    /// <summary>Opens the log folder (created if missing) in Explorer.</summary>
    public void OpenFolder(string? selectFile = null)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var args = selectFile is not null && File.Exists(selectFile) ? $"/select,\"{selectFile}\"" : $"\"{Folder}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not open {Folder}: {ex.Message}");
        }
    }

    /// <summary>Opens a log in the default text editor.</summary>
    public void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not open {path}: {ex.Message}");
            OpenFolder(path);
        }
    }
}
