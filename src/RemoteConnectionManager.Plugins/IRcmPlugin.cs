namespace RemoteConnectionManager.Plugins;

/// <summary>
/// Something RCM can switch on or off in Settings. A plugin also implements one or more feature
/// contracts (<see cref="ICredentialProvider"/>, <see cref="IServerInspector"/>). Plugins are
/// registered in the app's service container; there is no loading from a folder.
/// </summary>
public interface IRcmPlugin
{
    /// <summary>Stable id used in settings.json to switch the plugin off, for example "onepassword".</summary>
    string Id { get; }
    string Name { get; }
    /// <summary>One or two sentences for the Settings window.</summary>
    string Description { get; }

    /// <summary>
    /// A quick check for the Settings window, for example "1Password CLI 2.30.0 found". Must not
    /// prompt the user or read any secret.
    /// </summary>
    Task<PluginStatus> GetStatusAsync(CancellationToken ct);
}

public sealed record PluginStatus(bool Ready, string Text)
{
    public static PluginStatus Ok(string text) => new(true, text);
    public static PluginStatus Problem(string text) => new(false, text);
}

/// <summary>
/// Thrown by a plugin when something the user can fix went wrong. The message is shown as is,
/// so it must be plain words and must never contain a secret.
/// </summary>
public sealed class PluginException : Exception
{
    public PluginException(string message) : base(message) { }
    public PluginException(string message, Exception inner) : base(message, inner) { }
}
