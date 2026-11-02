using System.Security.Cryptography;
using System.Text;
using AITool.Application.Networking;
using AITool.Application.Proxy;
using AITool.Domain.Operations;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;

namespace AITool.Web.Services;

/// <summary>Resolves live account policy first, retaining site selectors until the actual send.</summary>
public sealed class ForwardNetworkPolicyResolver(
    IServiceScopeFactory scopeFactory,
    IAccountNetworkPolicyResolver accountPolicies) : IForwardNetworkPolicyResolver
{
    // Process-local HMAC avoids exposing a reusable digest of legacy URL credentials in pool keys.
    private static readonly byte[] LegacyIdentityKey = RandomNumberGenerator.GetBytes(32);

    public async Task<ForwardNetworkPolicyResolution> ResolveAsync(ProxyForwardRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var context = request.NetworkContext;
        if (request.AccountIdentity is not null && context?.Account is not null
            && request.AccountIdentity != context.Account)
            throw Error("account_network_identity_conflict", "The forwarding account identity does not match its network context.");
        var account = request.AccountIdentity ?? context?.Account;
        var isolation = account is null ? context?.IsolationKey ?? "ordinary-forward"
            : $"account:{account.ProviderKey}:{account.AccountId:N}";
        ResolvedNetworkConfiguration configuration;
        if (account is not null)
        {
            // Never freeze an account policy in a route snapshot or operation context.
            configuration = await accountPolicies.ResolveForAccountAsync(account, cancellationToken);
            if (configuration.Mode != NetworkMode.Legacy)
                return new(configuration, isolation);
        }
        else if (context is not null)
        {
            configuration = await accountPolicies.ResolveAsync(context, cancellationToken);
            if (configuration.Mode != NetworkMode.Legacy)
                return new(configuration, isolation);
        }

        var raw = request.EgressProxyUrl?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return new(ResolvedNetworkConfiguration.Legacy, isolation, account is null && context is null);

        using var scope = scopeFactory.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<AppDbContext>().Client.CopyNew();
        // Ordinary site selectors intentionally retain the historic global-off default behavior.
        // Account ProxyProfile selections were already validated above and cannot fail open here.
        if (!await db.Queryable<SystemRuntimeSettings>()
                .AnyAsync(s => s.Id == 1 && s.DeveloperProxyProfilesEnabled, cancellationToken))
            return new(ResolvedNetworkConfiguration.Legacy, isolation, account is null && context is null);

        if (raw.Equals("None", StringComparison.OrdinalIgnoreCase) || raw.Equals("direct", StringComparison.OrdinalIgnoreCase))
            return new(ResolvedNetworkConfiguration.Direct, isolation);

        Guid profileId;
        if (raw.StartsWith("profile:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(raw[8..], out profileId) || profileId == Guid.Empty)
                throw Error("invalid_proxy_profile_reference", "The selected proxy profile reference is invalid.");
        }
        else if (!raw.Contains("://", StringComparison.Ordinal))
        {
            // Resolve legacy keys to identity, not a sanitized URL that loses protected credentials.
            var profile = (await db.Queryable<ProxyProfile>().ToListAsync(cancellationToken))
                .SingleOrDefault(p => string.Equals(p.Key, raw, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
                throw Error("proxy_profile_not_found", "The selected proxy profile does not exist.");
            profileId = profile.Id;
        }
        else
        {
            if (!ProxyProfileSecretService.TryNormalizeProxyUrl(raw, out var endpoint, out var credentials, out _))
                throw Error("invalid_egress_proxy", "The selected egress proxy endpoint is invalid.");
            // A legacy URL has no persisted profile generation. Its opaque fingerprint gives the
            // transport a stable non-secret identity and changes whenever endpoint/credentials change.
            var fingerprint = HMACSHA256.HashData(LegacyIdentityKey, Encoding.UTF8.GetBytes(raw));
            configuration = new ResolvedNetworkConfiguration
            {
                Mode = NetworkMode.ProxyProfile,
                ProxyProfileId = new Guid(fingerprint.AsSpan(0, 16)),
                ProfileRevision = 1,
                ProxyEndpoint = new Uri(endpoint, UriKind.Absolute),
                Credentials = credentials
            };
            return new(configuration, isolation);
        }

        // The account resolver re-reads global state, endpoint, revision, enablement and secrets.
        configuration = await accountPolicies.ResolveAsync(new NetworkOperationContext(
            account, isolation, new AccountNetworkSelection(NetworkMode.ProxyProfile, profileId)), cancellationToken);
        return new(configuration, isolation);
    }

    private static NetworkPolicyException Error(string code, string message) => new(code, message);
}
