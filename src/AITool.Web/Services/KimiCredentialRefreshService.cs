using AITool.Application.Networking;
using AITool.Application.Kimi;
using AITool.Domain.Kimi;
using AITool.Domain.Sites;
using AITool.Infrastructure.Common;
using AITool.Infrastructure.Persistence;

namespace AITool.Web.Services;

/// <summary>
/// Kimi 凭证刷新服务：负责单个 Kimi 账号的 token 刷新与隐藏 Site.ApiKey 同步，
/// 支持 single-flight 防并发刷新冲突。
/// </summary>
public sealed class KimiCredentialRefreshService
{
    private static readonly KeyedAsyncLock RefreshLocks = new();

    private readonly NetworkBoundOAuthClientFactory? _networkFactory;
    private readonly AppDbContext _dbContext;
    private readonly IKimiOAuthClient _oauth;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly ILogger<KimiCredentialRefreshService> _logger;

    public KimiCredentialRefreshService(
        AppDbContext dbContext,
        IKimiOAuthClient oauth,
        ProxyRequestMetadataCache metadataCache,
        ILogger<KimiCredentialRefreshService> logger,
        NetworkBoundOAuthClientFactory? networkFactory = null)
    {
        _networkFactory = networkFactory;
        _dbContext = dbContext;
        _oauth = oauth;
        _metadataCache = metadataCache;
        _logger = logger;
    }

    /// <summary>
    /// 当代理请求命中 401 时，通过 linkedSiteId 刷新凭证。
    /// </summary>
    public async Task<string?> RefreshForAccountAsync(Guid accountId, string staleAccessToken, CancellationToken cancellationToken)
    {
        using (await NetworkBoundOAuthClientFactory.LockCredentialsAsync("kimi", accountId, cancellationToken))
        {
            var account = await _dbContext.KimiAccounts.InSingleAsync(accountId);
            if (account is null || account.IsDeleted || string.IsNullOrWhiteSpace(account.RefreshToken)) return null;
            if (!string.Equals(account.AccessToken, staleAccessToken, StringComparison.Ordinal)) return account.AccessToken;
            return await RefreshAccountInternalAsync(account, cancellationToken);
        }
    }

    public async Task<string?> RefreshAsync(Guid linkedSiteId, string staleAccessToken, CancellationToken cancellationToken)
    {
        if (linkedSiteId == Guid.Empty) return null;
        var accounts = await _dbContext.KimiAccounts.Where(a => a.LinkedSiteId == linkedSiteId && !a.IsDeleted)
            .ToListAsync(cancellationToken);
        if (accounts.Count != 1) return null;
        try
        {
            return await RefreshForAccountAsync(accounts[0].Id, staleAccessToken, cancellationToken);
        }
        catch (NetworkPolicyException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Unable to refresh Kimi token for linked site {SiteId}", linkedSiteId);
            return null;
        }
    }

    /// <summary>
    /// 主动刷新指定 Kimi 账号的凭证（如手动点击刷新或更新 refresh_token 后）。
    /// </summary>
    public async Task<KimiAccount> RefreshKimiCredentialAsync(Guid accountId, CancellationToken cancellationToken, string? replacementRefreshToken = null)
    {
        using (await NetworkBoundOAuthClientFactory.LockCredentialsAsync("kimi", accountId, cancellationToken))
        {
            using var client = _dbContext.Client.CopyNew();
            client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
            var account = await client.Queryable<KimiAccount>()
                .InSingleAsync(accountId)
                ?? throw new KeyNotFoundException("Kimi 账号不存在");

            if (!string.IsNullOrWhiteSpace(replacementRefreshToken))
                account.RefreshToken = replacementRefreshToken.Trim();
            if (string.IsNullOrWhiteSpace(account.RefreshToken))
            {
                throw new InvalidOperationException("Kimi 账号未配置 refresh_token，无法刷新");
            }

            await RefreshAccountInternalAsync(account, cancellationToken);

            return await client.Queryable<KimiAccount>().InSingleAsync(accountId) ?? account;
        }
    }

    private async Task<string?> RefreshAccountInternalAsync(KimiAccount account, CancellationToken ct)
    {
        using var network = _networkFactory?.CreateForAccount(new AccountIdentity("kimi", account.Id), "credential-refresh");
        var tokens = await (network?.KimiOAuth ?? _oauth).RefreshTokenAsync(account.RefreshToken!, account.DeviceId, ct);
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");

        if (!string.IsNullOrWhiteSpace(tokens.AccessToken))
        {
            account.AccessToken = tokens.AccessToken;
        }
        if (!string.IsNullOrWhiteSpace(tokens.RefreshToken))
        {
            account.RefreshToken = tokens.RefreshToken;
        }
        account.TokenExpiresAt = tokens.ExpiresAt;
        account.LastRefreshAt = DateTimeOffset.UtcNow;
        account.UpdatedAt = DateTimeOffset.UtcNow;

        client.Ado.BeginTran();
        try
        {
        await client.Updateable(account)
            .UpdateColumns(a => new { a.AccessToken, a.RefreshToken, a.TokenExpiresAt, a.LastRefreshAt, a.UpdatedAt })
            .ExecuteCommandAsync(ct);

        var site = await client.Queryable<Site>().InSingleAsync(account.LinkedSiteId);
        if (site != null && !string.IsNullOrWhiteSpace(tokens.AccessToken))
        {
            site.ApiKey = tokens.AccessToken;
            await client.Updateable(site).UpdateColumns(s => new { s.ApiKey }).ExecuteCommandAsync(ct);
        }

            client.Ado.CommitTran();
        }
        catch { client.Ado.RollbackTran(); throw; }

        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateKimiAccounts();
        _logger.LogInformation("Kimi account {Id} ({DisplayName}) token refreshed successfully", account.Id, account.DisplayName);
        return tokens.AccessToken;
    }
}
