using Azure;
using Azure.Identity;

namespace RemoteConnectionManager.KeyVault;

/// <summary>Turns SDK exceptions into one plain sentence for the in place error bar.</summary>
public static class KeyVaultErrors
{
    public static string Describe(Exception ex, string action)
    {
        switch (ex)
        {
            case OperationCanceledException:
                return $"{action} was cancelled.";
            case AuthenticationRequiredException:
                return "Sign in to Azure first.";
            case CredentialUnavailableException cu:
                return $"Azure sign in is not available: {FirstLine(cu.Message)}";
            case AuthenticationFailedException af:
                return $"Azure sign in failed: {FirstLine(af.Message)}";
            case RequestFailedException rf:
                return DescribeRequest(rf, action);
            case HttpRequestException http:
                return $"{action} failed: could not reach the vault ({FirstLine(http.Message)}). Check the vault name and your network.";
            default:
                return $"{action} failed: {FirstLine(ex.Message)}";
        }
    }

    private static string DescribeRequest(RequestFailedException rf, string action)
    {
        var code = rf.ErrorCode ?? string.Empty;
        if (code.Equals("ForbiddenByFirewall", StringComparison.OrdinalIgnoreCase) ||
            code.Equals("ForbiddenByConnection", StringComparison.OrdinalIgnoreCase))
            return $"{action} was blocked by the vault's network rules. This machine is not allowed through the vault firewall or private endpoint.";

        return rf.Status switch
        {
            401 => $"{action} failed: Azure did not accept the sign in. Sign out and sign in again.",
            403 when code.Equals("ForbiddenByRbac", StringComparison.OrdinalIgnoreCase) =>
                $"{action} was denied. Your account has no Azure role on this vault that allows it (for example Key Vault Secrets Officer).",
            403 when code.Equals("ForbiddenByPolicy", StringComparison.OrdinalIgnoreCase) =>
                $"{action} was denied by the vault's access policy for your account.",
            403 => $"{action} was denied. {FirstLine(rf.Message)}",
            404 => $"{action} failed: not found. It may have been deleted, or the vault name is wrong.",
            409 => $"{action} failed: a deleted item with this name still exists. Recover or purge it in the Azure portal, or pick another name.",
            429 => $"{action} was throttled by Azure. Wait a moment and try again.",
            _ => $"{action} failed ({rf.Status}{(code.Length > 0 ? " " + code : "")}): {FirstLine(rf.Message)}",
        };
    }

    private static string FirstLine(string message)
    {
        var line = message.Split('\n', 2)[0].Trim();
        return line.Length > 300 ? line[..300] + "..." : line;
    }
}
