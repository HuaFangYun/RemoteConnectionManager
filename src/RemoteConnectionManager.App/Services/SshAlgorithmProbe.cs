using System.Collections.Concurrent;
using System.Diagnostics;
using RemoteConnectionManager.Core.Services;

namespace RemoteConnectionManager.App.Services;

/// <summary>
/// Asks ssh.exe which algorithms it was built with (<c>ssh -Q kex|key|cipher|mac</c>), once per
/// executable, so the legacy switch never names one it would reject. Null when ssh cannot be asked;
/// <see cref="SshLauncher"/> then uses a safe default list.
/// </summary>
public static class SshAlgorithmProbe
{
    private static readonly ConcurrentDictionary<string, SshAlgorithms?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static SshAlgorithms? Get(string sshPath, AppLog log) => Cache.GetOrAdd(sshPath, path =>
    {
        try
        {
            var result = new SshAlgorithms(Query(path, "kex"), Query(path, "key"), Query(path, "cipher"), Query(path, "mac"));
            log.Info($"ssh -Q on {path}: {result.Kex.Count} kex, {result.HostKeys.Count} key, {result.Ciphers.Count} cipher, {result.Macs.Count} mac algorithms");
            return result;
        }
        catch (Exception ex)
        {
            log.Warn($"Could not ask {path} which algorithms it supports, using the default legacy list: {ex.Message}");
            return null;
        }
    });

    private static IReadOnlySet<string> Query(string path, string what)
    {
        using var process = Process.Start(new ProcessStartInfo(path, $"-Q {what}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("ssh did not start");
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(3000))
        {
            try { process.Kill(); } catch { }
            throw new TimeoutException($"ssh -Q {what} did not finish");
        }
        var set = SshLauncher.ParseQuery(output.GetAwaiter().GetResult());
        if (set.Count == 0) throw new InvalidOperationException($"ssh -Q {what} returned nothing");
        return set;
    }
}
