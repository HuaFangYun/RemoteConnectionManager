using Microsoft.Management.Infrastructure;

namespace RemoteConnectionManager.Plugins.ServerChecks;

/// <summary>Plain words for CIM failures over WinRM and DCOM.</summary>
internal static class CimErrors
{
    public static string Describe(string host, CimException? winrm, string? winrmSkipped, CimException dcom)
    {
        if (dcom.NativeErrorCode == NativeErrorCode.AccessDenied || winrm?.NativeErrorCode == NativeErrorCode.AccessDenied)
            return $"Access denied on {host}. Your account needs to be a local administrator there (or in Remote Management Users for WinRM).";

        var parts = new List<string>();
        if (winrm is not null) parts.Add($"WinRM: {Short(winrm)}");
        else if (winrmSkipped is not null) parts.Add($"WinRM: {winrmSkipped}.");
        parts.Add($"DCOM: {Short(dcom)}");
        return $"Could not reach {host}. Check the name resolves, the host is on, and WinRM (5985) or RPC (135 and dynamic ports) is open from this PC. " + string.Join(" ", parts);
    }

    /// <summary>The first sentence of the message, which is the useful part of WS-Man's long errors.</summary>
    private static string Short(CimException ex)
    {
        var message = (ex.Message ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        var stop = message.IndexOf(". ", StringComparison.Ordinal);
        if (stop > 0) message = message[..(stop + 1)];
        if (message.Length > 220) message = message[..220] + "...";
        return message.Length == 0 ? ex.NativeErrorCode.ToString() : message;
    }
}
