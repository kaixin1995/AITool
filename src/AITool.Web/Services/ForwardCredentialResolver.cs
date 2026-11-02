using AITool.Application.Networking;
using AITool.Application.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;

namespace AITool.Web.Services;

internal sealed record ForwardOAuthCredentialSnapshot(string Provider, Guid AccountId, Guid SiteId,
    string? AccessToken, string? RefreshToken, DateTimeOffset? LastRefreshAt);

/// <summary>Revalidates site ownership and device OAuth credentials at the send boundary.</summary>
public sealed class ForwardCredentialResolver(IServiceScopeFactory scopeFactory) : IForwardCredentialResolver
{
    public async Task<ForwardCredentialLease> ResolveAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
    {
        if (request.SiteId is not { } siteId || siteId == Guid.Empty)
        {
            if (!request.AllowNonSiteTarget || request.AccountIdentity is not null
                || !string.IsNullOrWhiteSpace(request.ManagedSource))
                throw Blocked();
            return new ForwardCredentialLease(request.TargetApiKey);
        }

        using var scope = scopeFactory.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<AppDbContext>().Client.CopyNew();
        var site = await db.Queryable<Site>().InSingleAsync(siteId);
        if (site is null) throw Blocked();
        if (!string.Equals(site.ManagedSource?.Trim() ?? string.Empty, request.ManagedSource?.Trim() ?? string.Empty,
                StringComparison.OrdinalIgnoreCase)) throw Blocked();
        if (string.IsNullOrWhiteSpace(site.ManagedSource))
        {
            if (request.AccountIdentity is not null) throw Blocked();
            return new ForwardCredentialLease(request.TargetApiKey);
        }

        var provider = ProviderFor(site.ManagedSource);
        if (provider is null || request.AccountIdentity is not { } identity || identity.AccountId == Guid.Empty
            || !string.Equals(identity.ProviderKey, provider, StringComparison.OrdinalIgnoreCase)
            || !SameAddress(site.BaseUrl, request.TargetBaseUrl)) throw Blocked();
        var isDevice = provider == "device-oauth";
        var gate = isDevice ? await DeviceOAuthRefreshGate.AcquireAsync(identity.AccountId, cancellationToken) : null;
        try
        {
            // A single SQLite statement observes one snapshot across all owner tables and token.
            // The statement and connection finish before the caller starts network I/O.
            cancellationToken.ThrowIfCancellationRequested();
            var rows = await db.Ado.SqlQueryAsync<DeviceCredentialSnapshot>("""
                SELECT s.ManagedSource, s.BaseUrl,
                    CASE @provider
                        WHEN 'codex' THEN (SELECT c.AccessToken FROM CodexAccounts c WHERE c.Id=@accountId AND c.LinkedSiteId=s.Id)
                        WHEN 'google' THEN (SELECT g.AccessToken FROM GoogleAccounts g WHERE g.Id=@accountId AND g.LinkedSiteId=s.Id)
                        WHEN 'kimi' THEN (SELECT k.AccessToken FROM KimiAccounts k WHERE k.Id=@accountId AND k.LinkedSiteId=s.Id)
                        WHEN 'xai' THEN (SELECT x.AccessToken FROM XaiAccounts x WHERE x.Id=@accountId AND x.LinkedSiteId=s.Id)
                        WHEN 'device-oauth' THEN d.AccessToken END AS AccessToken,
                    CASE @provider
                        WHEN 'codex' THEN (SELECT c.RefreshToken FROM CodexAccounts c WHERE c.Id=@accountId)
                        WHEN 'google' THEN (SELECT g.RefreshToken FROM GoogleAccounts g WHERE g.Id=@accountId)
                        WHEN 'kimi' THEN (SELECT k.RefreshToken FROM KimiAccounts k WHERE k.Id=@accountId)
                        WHEN 'xai' THEN (SELECT x.RefreshToken FROM XaiAccounts x WHERE x.Id=@accountId)
                        WHEN 'device-oauth' THEN d.RefreshToken END AS RefreshToken,
                    CASE @provider
                        WHEN 'codex' THEN (SELECT c.LastRefreshAt FROM CodexAccounts c WHERE c.Id=@accountId)
                        WHEN 'google' THEN (SELECT g.LastRefreshAt FROM GoogleAccounts g WHERE g.Id=@accountId)
                        WHEN 'kimi' THEN (SELECT k.LastRefreshAt FROM KimiAccounts k WHERE k.Id=@accountId)
                        WHEN 'xai' THEN (SELECT x.LastRefreshAt FROM XaiAccounts x WHERE x.Id=@accountId)
                        WHEN 'device-oauth' THEN d.LastRefreshAt END AS LastRefreshAt,
                    (SELECT COUNT(*) FROM CodexAccounts c WHERE c.LinkedSiteId=s.Id) +
                    (SELECT COUNT(*) FROM GoogleAccounts g WHERE g.LinkedSiteId=s.Id) +
                    (SELECT COUNT(*) FROM KimiAccounts k WHERE k.LinkedSiteId=s.Id AND k.IsDeleted=0) +
                    (SELECT COUNT(*) FROM XaiAccounts x WHERE x.LinkedSiteId=s.Id AND x.IsDeleted=0) +
                    (SELECT COUNT(*) FROM DeviceOAuthAccounts o WHERE o.LinkedSiteId=s.Id AND o.IsDeleted=0) AS OwnerCount,
                    CASE @provider
                        WHEN 'codex' THEN (SELECT COUNT(*) FROM CodexAccounts c WHERE c.LinkedSiteId=s.Id AND c.Id=@accountId)
                        WHEN 'google' THEN (SELECT COUNT(*) FROM GoogleAccounts g WHERE g.LinkedSiteId=s.Id AND g.Id=@accountId)
                        WHEN 'kimi' THEN (SELECT COUNT(*) FROM KimiAccounts k WHERE k.LinkedSiteId=s.Id AND k.Id=@accountId AND k.IsDeleted=0)
                        WHEN 'xai' THEN (SELECT COUNT(*) FROM XaiAccounts x WHERE x.LinkedSiteId=s.Id AND x.Id=@accountId AND x.IsDeleted=0)
                        WHEN 'device-oauth' THEN (SELECT COUNT(*) FROM DeviceOAuthAccounts o WHERE o.LinkedSiteId=s.Id AND o.Id=@accountId AND o.IsDeleted=0)
                        ELSE 0 END AS ExpectedCount
                FROM Sites s
                LEFT JOIN DeviceOAuthAccounts d ON d.LinkedSiteId=s.Id AND d.Id=@accountId AND d.IsDeleted=0
                WHERE s.Id=@siteId
                """, new { siteId, accountId = identity.AccountId, provider }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = rows.SingleOrDefault();
            if (snapshot is null || !string.Equals(snapshot.ManagedSource?.Trim(), site.ManagedSource?.Trim(), StringComparison.OrdinalIgnoreCase)
                || !SameAddress(snapshot.BaseUrl, request.TargetBaseUrl)
                || snapshot.OwnerCount != 1 || snapshot.ExpectedCount != 1
                || (isDevice && string.IsNullOrWhiteSpace(snapshot.AccessToken)))
                throw Blocked();
            return new ForwardCredentialLease(isDevice ? snapshot.AccessToken! : request.TargetApiKey, isDevice, gate,
                credentialSnapshot: new ForwardOAuthCredentialSnapshot(identity.ProviderKey, identity.AccountId, siteId,
                    snapshot.AccessToken, snapshot.RefreshToken, snapshot.LastRefreshAt));
        }
        catch
        {
            gate?.Dispose();
            throw;
        }
    }

    public sealed class DeviceCredentialSnapshot
    {
        public string? ManagedSource { get; set; }
        public string BaseUrl { get; set; } = string.Empty;
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public DateTimeOffset? LastRefreshAt { get; set; }
        public long OwnerCount { get; set; }
        public long ExpectedCount { get; set; }
    }

    private static string? ProviderFor(string source) => source.Trim().ToLowerInvariant() switch
    {
        "codex" => "codex", "google" => "google", "kimi_oauth" => "kimi",
        "xai_oauth" => "xai", "device_oauth" => "device-oauth", _ => null
    };
    private static bool SameAddress(string a, string b)
    {
        if (!Uri.TryCreate(a, UriKind.Absolute, out var left) || !Uri.TryCreate(b, UriKind.Absolute, out var right))
            return false;
        return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase)
            && left.Port == right.Port
            && string.Equals(left.UserInfo, right.UserInfo, StringComparison.Ordinal)
            && string.Equals(left.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/'),
                right.GetComponents(UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/'), StringComparison.Ordinal)
            && string.Equals(left.Query, right.Query, StringComparison.Ordinal)
            && string.Equals(left.Fragment, right.Fragment, StringComparison.Ordinal);
    }
    private static NetworkPolicyException Blocked()
        => new("forward_credential_invalid", "The current forwarding credential or site ownership is invalid.");
}
