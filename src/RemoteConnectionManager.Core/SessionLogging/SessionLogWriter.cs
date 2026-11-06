using System.Text;
using System.Threading.Channels;
using RemoteConnectionManager.Core.Services;

namespace RemoteConnectionManager.Core.SessionLogging;

/// <summary>What goes in a session log's header.</summary>
public sealed record SessionLogInfo(string EntryName, string Kind, string Target, string? Folder = null);

/// <summary>
/// Writes one tab's session log: a plain text file under <c>session-logs\yyyy-MM\</c> with a header,
/// finished lines from <see cref="VtScreenRecorder"/> passed through <see cref="LogRedactor"/>, time
/// marks after a quiet spell, and a footer. Output is queued and written on a background task so a
/// busy terminal never waits on the disk. Thread safe: <see cref="Write"/> is called from the read loop.
/// </summary>
public sealed class SessionLogWriter : IDisposable
{
    /// <summary>A time mark line is written when output resumes after this long.</summary>
    public static readonly TimeSpan QuietGap = TimeSpan.FromMinutes(5);

    private readonly SessionLogSettings _settings;
    private readonly SessionLogInfo _info;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Channel<Item> _queue = Channel.CreateUnbounded<Item>(); // multi reader options so Reader.Count is supported
    private readonly Task _worker;
    private readonly LogRedactor _redactor = new();
    private readonly VtScreenRecorder _recorder;
    private readonly string _basePath;
    private StreamWriter? _text;
    private StreamWriter? _raw;
    private long _bytes;
    private int _part = 1;
    private DateTimeOffset _lastLine;
    private bool _disposed;

    private abstract record Item;
    private sealed record Output(string Text) : Item;
    private sealed record SizeChange(int Columns, int Rows) : Item;
    private sealed record Note(string Text) : Item;
    private sealed record Secret(string Value) : Item;
    private sealed record EndOfRun(string Text) : Item;
    private sealed record FlushRequest(TaskCompletionSource Done) : Item;

    /// <summary>The text log being written now (changes when a log rolls over to a new part).</summary>
    public string FilePath { get; private set; }

    /// <summary>Raised on the writer task when the log cannot be written; logging stops after it.</summary>
    public event Action<Exception>? Failed;

    public SessionLogWriter(SessionLogSettings settings, SessionLogInfo info, int columns, int rows, Func<DateTimeOffset>? clock = null)
    {
        _settings = settings.Clone();
        _info = info;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _recorder = new VtScreenRecorder(columns, rows, WriteLine);

        var now = _clock();
        var folder = Path.Combine(_settings.EffectiveFolder, now.ToString("yyyy-MM"));
        Directory.CreateDirectory(folder);
        _basePath = UniqueBase(folder, $"{SafeName(info.EntryName)}_{now:yyyy-MM-dd_HHmmss}");
        FilePath = _basePath + ".log";
        Open();
        _lastLine = now;
        _worker = Task.Run(RunAsync);
    }

    // ---------- called from any thread ----------

    public void Write(string output)
    {
        if (!string.IsNullOrEmpty(output)) _queue.Writer.TryWrite(new Output(output));
    }

    public void Resize(int columns, int rows) => _queue.Writer.TryWrite(new SizeChange(columns, rows));

    /// <summary>A line of RCM's own, for example "reconnected". Written after anything already queued.</summary>
    public void AddNote(string text) => _queue.Writer.TryWrite(new Note(text));

    /// <summary>The run ended (shell exited, connection dropped): logs what is still on screen, then the note.</summary>
    public void EndRun(string text) => _queue.Writer.TryWrite(new EndOfRun(text));

    /// <summary>Masks this value from here on, for example a password RCM is about to type.</summary>
    public void AddSecret(string value) => _queue.Writer.TryWrite(new Secret(value));

    /// <summary>Waits until everything queued so far is on disk. For tests and Open log.</summary>
    public async Task FlushAsync()
    {
        var request = new FlushRequest(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!_queue.Writer.TryWrite(request)) return;
        // The writer task completes the request, or abandons it when it stops.
        await Task.WhenAny(request.Done.Task, _worker).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Writer.TryComplete();
        // Bounded wait: a tab closing must not hang on a slow share.
        try { _worker.Wait(TimeSpan.FromSeconds(3)); } catch { }
    }

    // ---------- writer task ----------

    private async Task RunAsync()
    {
        try
        {
            await foreach (var item in ReadAllAsync())
            {
                switch (item)
                {
                    case Output o:
                        _raw?.Write(o.Text);
                        _recorder.Feed(o.Text);
                        break;
                    case SizeChange s:
                        _recorder.Resize(s.Columns, s.Rows);
                        break;
                    case Secret s:
                        _redactor.AddSecret(s.Value);
                        break;
                    case FlushRequest f:
                        _text?.Flush();
                        _raw?.Flush();
                        f.Done.TrySetResult();
                        break;
                    case Note n:
                        WriteMark(n.Text);
                        break;
                    case EndOfRun e:
                        _recorder.Flush();
                        WriteMark(e.Text);
                        break;
                }
                if (_queue.Reader.Count == 0)
                {
                    _text?.Flush();
                    _raw?.Flush();
                }
            }

            _recorder.Flush();
            WriteFooter();
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
        finally
        {
            try { _text?.Dispose(); } catch { }
            try { _raw?.Dispose(); } catch { }
            _text = null;
            _raw = null;
        }
    }

    private async IAsyncEnumerable<Item> ReadAllAsync()
    {
        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            while (_queue.Reader.TryRead(out var item))
                yield return item;
    }

    private void WriteLine(string line)
    {
        if (_text is null) return;
        var now = _clock();
        if (now - _lastLine >= QuietGap) Append($"--- {now:yyyy-MM-dd HH:mm:ss} ---");
        _lastLine = now;

        var clean = _settings.Redact ? _redactor.Redact(line) : line;
        if (clean is null) return;
        Append(clean);
    }

    private void WriteMark(string text)
    {
        var now = _clock();
        Append($"--- {now:yyyy-MM-dd HH:mm:ss} {text} ---");
        _lastLine = now;
    }

    private void Append(string line)
    {
        if (_text is null) return;
        _text.WriteLine(line);
        _bytes += Encoding.UTF8.GetByteCount(line) + 2;
        if (_bytes >= Math.Max(1, _settings.MaxFileMegabytes) * 1024L * 1024L) Roll();
    }

    private void Roll()
    {
        WriteHeaderLine("--- continued in the next part ---");
        _text?.Dispose();
        _raw?.Dispose();
        _part++;
        FilePath = $"{_basePath}_part{_part}.log";
        Open();
    }

    private void WriteHeaderLine(string line) => _text?.WriteLine(line);

    private void Open()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        _text = new StreamWriter(new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), utf8);
        _bytes = 0;
        if (_settings.KeepRawCopy)
        {
            var rawPath = Path.ChangeExtension(FilePath, ".vt");
            _raw = new StreamWriter(new FileStream(rawPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), utf8);
        }
        WriteHeader();
    }

    private void WriteHeader()
    {
        var now = _clock();
        WriteHeaderLine($"# RemoteConnectionManager session log{(_part > 1 ? $", part {_part}" : string.Empty)}");
        WriteHeaderLine($"# Entry:   {_info.EntryName}{(_info.Folder is { Length: > 0 } f ? $"  ({f})" : string.Empty)}");
        WriteHeaderLine($"# Type:    {_info.Kind}");
        WriteHeaderLine($"# Target:  {_info.Target}");
        WriteHeaderLine($"# User:    {Environment.UserDomainName}\\{Environment.UserName} on {Environment.MachineName}");
        WriteHeaderLine($"# Started: {now:yyyy-MM-dd HH:mm:ss zzz}");
        if (_settings.Redact) WriteHeaderLine("# Passwords, secrets and keys that RCM recognises are shown as ********.");
        WriteHeaderLine(string.Empty);
    }

    private void WriteFooter()
    {
        WriteHeaderLine(string.Empty);
        WriteHeaderLine($"# Closed:  {_clock():yyyy-MM-dd HH:mm:ss zzz}");
    }

    private static string UniqueBase(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        for (var i = 2; File.Exists(path + ".log"); i++) path = Path.Combine(folder, $"{name}_{i}");
        return path;
    }

    /// <summary>A file name safe version of an entry name: invalid characters become _, at most 60 characters.</summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray()).Trim('.', '_');
        if (cleaned.Length == 0) cleaned = "session";
        return cleaned.Length > 60 ? cleaned[..60] : cleaned;
    }
}

/// <summary>Housekeeping for the session log folder.</summary>
public static class SessionLogFiles
{
    /// <summary>Deletes .log and .vt files older than <paramref name="keepDays"/> days, then empty month folders. Returns how many files went.</summary>
    public static int Prune(string folder, int keepDays, DateTime? nowUtc = null)
    {
        if (keepDays <= 0 || !Directory.Exists(folder)) return 0;
        var cutoff = (nowUtc ?? DateTime.UtcNow).AddDays(-keepDays);
        var removed = 0;
        foreach (var pattern in new[] { "*.log", "*.vt" })
            foreach (var file in Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) >= cutoff) continue;
                    File.Delete(file);
                    removed++;
                }
                catch { /* in use or no access: try again next start */ }
            }
        foreach (var dir in Directory.EnumerateDirectories(folder))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch { }
        }
        return removed;
    }
}
