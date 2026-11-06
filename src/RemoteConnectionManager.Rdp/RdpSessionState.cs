namespace RemoteConnectionManager.Rdp;

public enum RdpSessionState
{
    Idle,
    Connecting,
    Connected,
    Disconnected,
}

public sealed class RdpDisconnectedEventArgs : EventArgs
{
    public int Reason { get; }
    public int ExtendedReason { get; }
    public string Description { get; }
    /// <summary>True when the user or the app asked for the disconnect, so no reconnect prompt is needed.</summary>
    public bool Requested { get; }

    public RdpDisconnectedEventArgs(int reason, int extendedReason, string description, bool requested)
    {
        Reason = reason;
        ExtendedReason = extendedReason;
        Description = description;
        Requested = requested;
    }
}

public sealed class RdpLogEventArgs : EventArgs
{
    public string Message { get; }
    public RdpLogEventArgs(string message) => Message = message;
}

/// <summary>
/// An explicit account for one connect attempt, for example a password read from Key Vault.
/// Lives only for the call; the control never stores it.
/// </summary>
public sealed class RdpSignIn
{
    public string UserName { get; }
    public string? Domain { get; }
    public string Password { get; }

    public RdpSignIn(string userName, string? domain, string password)
    {
        UserName = userName;
        Domain = domain;
        Password = password;
    }

    public override string ToString() => Domain is null ? UserName : $"{Domain}\\{UserName}";
}
