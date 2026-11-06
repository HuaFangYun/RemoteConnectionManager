using RemoteConnectionManager.Core.Services;
using RemoteConnectionManager.Core.SessionLogging;
using Xunit;

namespace RemoteConnectionManager.Core.Tests;

public class VtScreenRecorderTests
{
    private static (VtScreenRecorder Recorder, List<string> Lines) Make(int cols = 40, int rows = 5)
    {
        var lines = new List<string>();
        return (new VtScreenRecorder(cols, rows, lines.Add), lines);
    }

    [Fact]
    public void PlainLinesAreLoggedWhenFinished()
    {
        var (r, lines) = Make();
        r.Feed("hello\r\nworld\r\n");
        Assert.Equal(new[] { "hello", "world" }, lines);
    }

    [Fact]
    public void BlankLinesBetweenOutputAreKeptButRunsCollapse()
    {
        var (r, lines) = Make(rows: 20);
        r.Feed("\r\n\r\nfirst\r\n\r\nsecond\r\n\r\n\r\n\r\nthird\r\n");
        Assert.Equal(new[] { "first", "", "second", "", "third" }, lines);
    }

    [Fact]
    public void RowsSkippedByCursorMovesAreNotBlankLines()
    {
        var (r, lines) = Make(rows: 10);
        r.Feed("\x1b[1;1Htop\x1b[6;1Hbottom\r\n");
        Assert.Equal(new[] { "top", "bottom" }, lines);
    }

    [Fact]
    public void ColoursAndTitlesAreDropped()
    {
        var (r, lines) = Make();
        r.Feed("\x1b]0;Administrator: pwsh\a\x1b[32;1mPS\x1b[0m C:\\> ok\r\n");
        Assert.Equal(new[] { "PS C:\\> ok" }, lines);
    }

    [Fact]
    public void LineEditingKeepsOnlyTheFinalText()
    {
        // What PSReadLine does while typing: redraw the line, backspace, erase to end of line.
        var (r, lines) = Make();
        r.Feed("PS> Get-Proc");
        r.Feed("\b\b\b\b\x1b[K");
        r.Feed("Service\x1b[K");
        Assert.Empty(lines);
        r.Feed("\r\n");
        Assert.Equal(new[] { "PS> Get-Service" }, lines);
    }

    [Fact]
    public void RepaintingTheSameTextDoesNotRepeatLines()
    {
        // conhost repaints rows with cursor positioning; unchanged rows must not be logged twice.
        var (r, lines) = Make();
        r.Feed("one\r\ntwo\r\nPS> ");
        r.Feed("\x1b[H\x1b[?25lone\x1b[K\r\ntwo\x1b[K\r\nPS> \x1b[K\x1b[?25h");
        r.Flush();
        Assert.Equal(new[] { "one", "two", "PS>" }, lines);
    }

    [Fact]
    public void RowsPaintedByCursorPositionAreLogged()
    {
        var (r, lines) = Make();
        r.Feed("\x1b[1;1Hfirst\x1b[2;1Hsecond\x1b[3;1H");
        Assert.Equal(new[] { "first", "second" }, lines);
    }

    [Fact]
    public void ScrollingOffTheTopLogsInOrder()
    {
        var (r, lines) = Make(rows: 3);
        for (var i = 1; i <= 6; i++) r.Feed($"line {i}\r\n");
        r.Flush();
        Assert.Equal(Enumerable.Range(1, 6).Select(i => $"line {i}"), lines);
    }

    [Fact]
    public void ClearScreenKeepsWhatWasThere()
    {
        var (r, lines) = Make();
        r.Feed("before");
        r.Feed("\x1b[2J\x1b[Hafter\r\n");
        Assert.Equal(new[] { "before", "after" }, lines);
    }

    [Fact]
    public void LongLinesWrapAtTheScreenEdge()
    {
        var (r, lines) = Make(cols: 10);
        r.Feed("0123456789ABCDE\r\n");
        Assert.Equal(new[] { "0123456789", "ABCDE" }, lines);
    }

    [Fact]
    public void AlternateScreenIsNotLogged()
    {
        var (r, lines) = Make();
        r.Feed("$ vim notes\r\n");
        r.Feed("\x1b[?1049h\x1b[Hvim screen\r\n~\r\n~\r\n\x1b[?1049l");
        r.Feed("$ done\r\n");
        Assert.Equal(new[] { "$ vim notes", "$ done" }, lines);
    }

    [Fact]
    public void SplitEscapeSequencesAcrossReads()
    {
        var (r, lines) = Make();
        r.Feed("ab\x1b[");
        r.Feed("31");
        r.Feed("mcd\r\n");
        Assert.Equal(new[] { "abcd" }, lines);
    }

    [Fact]
    public void ChangedRowIsLoggedAgainWhenFinishedAgain()
    {
        var (r, lines) = Make();
        r.Feed("Progress 10%\r\n");
        r.Feed("\x1b[1;1HProgress 100%\r\n");
        Assert.Equal(new[] { "Progress 10%", "Progress 100%" }, lines);
    }
}

public class LogRedactorTests
{
    [Theory]
    [InlineData("enable secret 5 $1$mERr$hx5rVt7rPNoS4wqbXKX7m0", "enable secret 5 ********")]
    [InlineData(" password 7 0822455D0A16", " password 7 ********")]
    [InlineData("username admin privilege 15 secret 9 $9$abc", "username admin privilege 15 secret 9 ********")]
    [InlineData("username bob password 0 Hunter2!", "username bob password 0 ********")]
    [InlineData("snmp-server community Publ1c RO", "snmp-server community ******** RO")]
    [InlineData("snmp-server community \"s3cret\" operator", "snmp-server community ******** operator")]
    [InlineData("tacacs-server key 7 060506324F41", "tacacs-server key 7 ********")]
    [InlineData("radius-server host 10.1.1.1 key MyKey", "radius-server host 10.1.1.1 key ********")]
    [InlineData(" key-string 7 1234ABCD", " key-string 7 ********")]
    [InlineData("password manager user-name admin sha1 5baa61e4c9b93f3f", "password manager user-name admin sha1 ********")]
    [InlineData("Server=x;Database=y;User Id=sa;Password=P@ss;", "Server=x;Database=y;User Id=sa;Password=********;")]
    [InlineData("$p = ConvertTo-SecureString 'Hunter2' -AsPlainText -Force", "$p = ConvertTo-SecureString ******** -AsPlainText -Force")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U", "Bearer ********")]
    public void MasksSecrets(string line, string expected) =>
        Assert.Equal(expected, new LogRedactor().Redact(line));

    [Theory]
    [InlineData("Enter password for user bob")]
    [InlineData("admin@10.22.22.1's password:")]
    [InlineData("Password:")]
    [InlineData("Your password has expired")]
    [InlineData("show running-config")]
    public void LeavesOrdinaryTextAlone(string line) =>
        Assert.Equal(line, new LogRedactor().Redact(line));

    [Fact]
    public void MasksValuesRcmTyped()
    {
        var r = new LogRedactor();
        r.AddSecret("Sw!tchPass99");
        Assert.Equal("% Unknown command: \"********\"", r.Redact("% Unknown command: \"Sw!tchPass99\""));
    }

    [Fact]
    public void DropsPrivateKeyBlocks()
    {
        var r = new LogRedactor();
        Assert.Equal("[private key removed from log]", r.Redact("-----BEGIN OPENSSH PRIVATE KEY-----"));
        Assert.Null(r.Redact("b3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW"));
        Assert.Null(r.Redact("-----END OPENSSH PRIVATE KEY-----"));
        Assert.Equal("after", r.Redact("after"));
    }
}

public class SessionLogWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rcm-log-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private SessionLogSettings Settings(bool raw = false) => new() { Folder = _dir, KeepRawCopy = raw };

    [Fact]
    public async Task WritesHeaderRedactedLinesAndFooter()
    {
        var clock = new DateTimeOffset(2026, 11, 6, 14, 32, 5, TimeSpan.FromHours(11));
        string path;
        using (var log = new SessionLogWriter(Settings(raw: true), new SessionLogInfo("Core router: 10/22", "SSH", "netadmin@10.22.22.1", "Network"), 80, 24, () => clock))
        {
            path = log.FilePath;
            log.Write("core-sw# show run | i secret\r\n");
            log.Write("enable secret 5 $1$abc$def\r\ncore-sw# ");
            log.AddSecret("Typed1234");
            log.Write("\r\nTyped1234\r\n");
            await log.FlushAsync();
            Assert.Contains("core-sw# show run | i secret", ReadShared(path));
            log.EndRun("disconnected");
        }

        Assert.Equal(Path.Combine(_dir, "2026-11", "Core_router__10_22_2026-11-06_143205.log"), path);
        var text = File.ReadAllText(path);
        Assert.Contains("# Entry:   Core router: 10/22  (Network)", text);
        Assert.Contains("# Target:  netadmin@10.22.22.1", text);
        Assert.Contains("enable secret 5 ********", text);
        Assert.DoesNotContain("$1$abc", text);
        Assert.DoesNotContain("Typed1234", text);
        Assert.Contains("--- 2026-11-06 14:32:05 disconnected ---", text);
        Assert.Contains("# Closed:", text);
        Assert.True(File.Exists(Path.ChangeExtension(path, ".vt")));
    }

    [Fact]
    public async Task TimeMarkAfterAQuietSpell()
    {
        var now = new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
        string path;
        using (var log = new SessionLogWriter(Settings(), new SessionLogInfo("pwsh", "Terminal", "pwsh.exe"), 80, 24, () => now))
        {
            path = log.FilePath;
            log.Write("first\r\n");
            await log.FlushAsync();
            now = now.AddMinutes(20);
            log.Write("second\r\n");
        }
        var lines = File.ReadAllLines(path);
        var first = Array.IndexOf(lines, "first");
        Assert.Equal("--- 2026-11-06 09:20:00 ---", lines[first + 1]);
        Assert.Equal("second", lines[first + 2]);
    }

    [Fact]
    public void SecondLogInTheSameSecondGetsItsOwnFile()
    {
        var now = DateTimeOffset.Now;
        using var a = new SessionLogWriter(Settings(), new SessionLogInfo("x", "Terminal", "t"), 80, 24, () => now);
        using var b = new SessionLogWriter(Settings(), new SessionLogInfo("x", "Terminal", "t"), 80, 24, () => now);
        Assert.NotEqual(a.FilePath, b.FilePath);
    }

    [Fact]
    public void PruneDeletesOnlyOldLogs()
    {
        var month = Directory.CreateDirectory(Path.Combine(_dir, "2026-01")).FullName;
        var old = Path.Combine(month, "old.log");
        var keep = Path.Combine(_dir, "new.log");
        var other = Path.Combine(month, "notes.txt");
        File.WriteAllText(old, "x");
        File.WriteAllText(keep, "x");
        File.WriteAllText(other, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-100));

        Assert.Equal(1, SessionLogFiles.Prune(_dir, 90));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(other));
        Assert.Equal(0, SessionLogFiles.Prune(_dir, 0));
    }

    [Theory]
    [InlineData("Core router", "Core_router")]
    [InlineData("a/b\\c:d", "a_b_c_d")]
    [InlineData("...", "session")]
    public void SafeNames(string name, string expected) => Assert.Equal(expected, SessionLogWriter.SafeName(name));

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }
}
