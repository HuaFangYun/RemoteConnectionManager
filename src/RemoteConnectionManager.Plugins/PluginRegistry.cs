using RemoteConnectionManager.Core.Models;

namespace RemoteConnectionManager.Plugins;

/// <summary>
/// Every plugin the app was built with, and which ones the user switched off. The switched off
/// set is the live list from AppSettings, so a change in Settings applies straight away.
/// </summary>
public sealed class PluginRegistry
{
    private readonly Func<IReadOnlyCollection<string>> _disabled;

    public IReadOnlyList<IRcmPlugin> All { get; }

    public PluginRegistry(IEnumerable<IRcmPlugin> plugins, Func<IReadOnlyCollection<string>> disabledIds)
    {
        All = plugins.DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _disabled = disabledIds;
    }

    public bool IsEnabled(IRcmPlugin plugin) => !_disabled().Contains(plugin.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The provider for a credential source whether or not it is enabled, so the UI can say "switched off".</summary>
    public ICredentialProvider? FindCredentialProvider(CredentialSource source) =>
        All.OfType<ICredentialProvider>().FirstOrDefault(p => p.Source == source);

    /// <summary>Enabled credential providers, for the editor's credential list.</summary>
    public IEnumerable<ICredentialProvider> CredentialProviders => All.OfType<ICredentialProvider>().Where(IsEnabled);

    /// <summary>The first enabled server inspector, or null when none is on.</summary>
    public IServerInspector? ServerInspector => All.OfType<IServerInspector>().FirstOrDefault(IsEnabled);
}
