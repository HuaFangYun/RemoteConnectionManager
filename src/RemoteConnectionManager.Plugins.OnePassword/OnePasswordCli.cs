using System.Diagnostics;
using System.Text;

namespace RemoteConnectionManager.Plugins.OnePassword;

/// <summary>Finds and runs op.exe. Output is captured through pipes; nothing goes through a shell or a temp file.</summary>
public sealed class OnePasswordCli
{
    private readonly Func<string?> _configuredPath;

    public OnePasswordCli(Func<string?> configuredPath)
    {
        _configuredPath = configuredPath;
    }

    public sealed record Result(int ExitCode, string StdOut, string StdErr);

    /// <summary>The settings path when it exists, then the usual install places, then PATH.</summary>
    public string? Find()
    {
        var configured = _configuredPath()?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(configured))
            return File.Exists(configured) ? configured : null;

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>
        {
            Path.Combine(local, "Microsoft", "WinGet", "Links", "op.exe"),
            Path.Combine(programFiles, "1Password CLI", "op.exe"),
            Path.Combine(profile, "scoop", "shims", "op.exe"),
        };
        var wingetPackages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        try
        {
            if (Directory.Exists(wingetPackages))
                candidates.AddRange(Directory.EnumerateDirectories(wingetPackages, "AgileBits.1Password.CLI*")
                    .Select(d => Path.Combine(d, "op.exe")));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim().Trim('"'), "op.exe")); } catch (ArgumentException) { }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<Result> RunAsync(string exe, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        // A service account token in the environment would bypass the user's own unlock; this plugin is for the desktop app only.
        psi.Environment.Remove("OP_SERVICE_ACCOUNT_TOKEN");
        psi.Environment.Remove("OP_CONNECT_TOKEN");
        psi.Environment.Remove("OP_CONNECT_HOST");

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new PluginException($"Could not start the 1Password CLI at {exe}: {ex.Message}", ex);
        }
        process.StandardInput.Close();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderr = process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            return new Result(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new PluginException($"The 1Password CLI did not answer within {timeout.TotalSeconds:0} seconds. If 1Password showed an unlock prompt, approve it and reconnect.");
        }
    }
}
