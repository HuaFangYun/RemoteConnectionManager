using Azure.ResourceManager;
using Azure.ResourceManager.KeyVault;
using Azure.ResourceManager.Resources;

namespace RemoteConnectionManager.KeyVault;

/// <summary>Finds subscriptions and the vaults in them through Azure Resource Manager.</summary>
public sealed class VaultCatalog
{
    private readonly AzureAuth _auth;

    public VaultCatalog(AzureAuth auth)
    {
        _auth = auth;
    }

    public async Task<IReadOnlyList<SubscriptionInfo>> GetSubscriptionsAsync(CancellationToken ct)
    {
        var arm = new ArmClient(_auth.Credential());
        var list = new List<SubscriptionInfo>();
        await foreach (var sub in arm.GetSubscriptions().GetAllAsync(ct).ConfigureAwait(true))
            list.Add(new SubscriptionInfo(sub.Data.SubscriptionId, sub.Data.DisplayName, sub.Data.TenantId?.ToString()));
        return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<VaultInfo>> GetVaultsAsync(SubscriptionInfo subscription, CancellationToken ct)
    {
        // A subscription in another tenant needs a token from that tenant.
        var tenant = string.Equals(subscription.TenantId, _auth.HomeTenantId, StringComparison.OrdinalIgnoreCase) ? null : subscription.TenantId;
        var arm = new ArmClient(_auth.Credential(tenant));
        var sub = arm.GetSubscriptionResource(SubscriptionResource.CreateResourceIdentifier(subscription.Id));

        var list = new List<VaultInfo>();
        await foreach (var vault in sub.GetKeyVaultsAsync(cancellationToken: ct).ConfigureAwait(true))
        {
            var uri = vault.Data.Properties.VaultUri ?? new Uri($"https://{vault.Data.Name}.vault.azure.net/");
            list.Add(new VaultInfo(
                vault.Data.Name,
                uri,
                subscription.Id,
                subscription.Name,
                vault.Id.ResourceGroupName ?? string.Empty,
                vault.Data.Location.ToString(),
                vault.Data.Properties.TenantId.ToString()));
        }
        return list.OrderBy(v => v.ResourceGroup, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
