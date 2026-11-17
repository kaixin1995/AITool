using AITool.Application.Networking;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;

namespace AITool.Infrastructure.Sites;

/// <summary>The selected credential and its effective, unresolved network selector.</summary>
public sealed record SiteCredentialContext(
    Guid? SiteKeyId,
    string ApiKey,
    string? EgressProxyUrl,
    AccountIdentity? AccountIdentity = null,
    string? KeyEgressProxyUrl = null);

/// <summary>Selects the highest priority enabled credential for a site-level operation.</summary>
public sealed class SiteKeySelector
{
    private readonly AppDbContext _dbContext;
    private readonly IAccountNetworkPolicyStore? _accountPolicies;

    public SiteKeySelector(AppDbContext dbContext, IAccountNetworkPolicyStore? accountPolicies = null)
    {
        _dbContext = dbContext;
        _accountPolicies = accountPolicies;
    }

    /// <summary>Retained for callers which only need the secret.</summary>
    public async Task<string> GetActiveKeyAsync(Guid siteId, CancellationToken cancellationToken = default)
        => (await GetActiveCredentialAsync(siteId, cancellationToken)).ApiKey;

    public async Task<SiteCredentialContext> GetActiveCredentialAsync(Guid siteId,
        CancellationToken cancellationToken = default)
    {
        var keys = await _dbContext.SiteKeys
            .Where(k => k.SiteId == siteId && k.IsEnabled)
            .OrderBy(k => k.Priority)
            .ThenBy(k => k.CreatedAt)
            .ThenBy(k => k.Id)
            .ToListAsync(cancellationToken);
        var site = await _dbContext.Sites.FirstAsync(s => s.Id == siteId, cancellationToken);
        var key = keys.FirstOrDefault();
        // Preserve the legacy fallback when the first selected credential is empty.
        if (string.IsNullOrEmpty(key?.KeyValue)) key = null;
        AccountIdentity? account = null;
        if (_accountPolicies is not null)
        {
            var owners = await _accountPolicies.GetLinkedSiteOwnersAsync([siteId], cancellationToken);
            owners.TryGetValue(siteId, out account);
        }
        return new SiteCredentialContext(key?.Id, key?.KeyValue ?? site?.ApiKey ?? string.Empty,
            !string.IsNullOrWhiteSpace(key?.EgressProxyUrl) ? key.EgressProxyUrl : site?.EgressProxyUrl,
            account, key?.EgressProxyUrl);
    }
}
