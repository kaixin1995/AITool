using AITool.Application.Networking;
using AITool.Application.Google;
using AITool.Domain.Google;
using AITool.Infrastructure.Common;
using AITool.Infrastructure.Persistence;
using System.Net;

namespace AITool.Web.Services;

/// <summary>
/// 在实时代理请求命中 Google 上游（Antigravity）401 时，立即刷新账号凭证并同步隐藏站点。
/// </summary>
public sealed class GoogleCredentialRefreshService
{
    /// <summary>
    /// 同一隐藏站点的 401 刷新采用 single-flight。Google 可能轮换 refresh_token，
    /// 并发重复刷新会让其中一个请求写回已失效的 token。
    /// </summary>
    private static readonly KeyedAsyncLock RefreshLocks = new();

    private readonly NetworkBoundOAuthClientFactory? _networkFactory;
    private readonly AppDbContext _dbContext;
    private readonly IGoogleOAuthClient _oauth;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly ILogger<GoogleCredentialRefreshService> _logger;

    public GoogleCredentialRefreshService(
        AppDbContext dbContext,
        IGoogleOAuthClient oauth,
        ProxyRequestMetadataCache metadataCache,
        ILogger<GoogleCredentialRefreshService> logger,
        NetworkBoundOAuthClientFactory? networkFactory = null)
    {
        _networkFactory = networkFactory;
        _dbContext = dbContext;
        _oauth = oauth;
        _metadataCache = metadataCache;
        _logger = logger;
    }

    public async Task<string?> RefreshForAccountAsync(Guid accountId, string staleAccessToken, CancellationToken cancellationToken)
    {
        using (await NetworkBoundOAuthClientFactory.LockCredentialsAsync("google", accountId, cancellationToken))
        {
            var account = await _dbContext.GoogleAccounts.InSingleAsync(accountId);
            if (account is null || string.IsNullOrWhiteSpace(account.RefreshToken)) return null;
            if (!string.Equals(account.AccessToken, staleAccessToken, StringComparison.Ordinal)) return account.AccessToken;
            return await RefreshCoreAsync(account.LinkedSiteId, staleAccessToken, cancellationToken, accountId);
        }
    }

    public async Task<string?> RefreshAsync(Guid linkedSiteId, string staleAccessToken, CancellationToken cancellationToken)
    {
        if (linkedSiteId == Guid.Empty) return null;
        var accounts = await _dbContext.GoogleAccounts.Where(a => a.LinkedSiteId == linkedSiteId)
            .ToListAsync(cancellationToken);
        if (accounts.Count != 1) return null;
        try
        {
            return await RefreshForAccountAsync(accounts[0].Id, staleAccessToken, cancellationToken);
        }
        catch (NetworkPolicyException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Unable to refresh Google token for linked site {SiteId}", linkedSiteId);
            return null;
        }
    }

    public async Task<bool> DisableAsync(
        Guid linkedSiteId,
        string reason,
        CancellationToken cancellationToken,
        string? staleAccessToken = null)
    {
        var candidates = await _dbContext.GoogleAccounts.Where(a => a.LinkedSiteId == linkedSiteId).ToListAsync(cancellationToken);
        if (candidates.Count != 1) return false;
        using (await NetworkBoundOAuthClientFactory.LockCredentialsAsync("google", candidates[0].Id, cancellationToken))
        using (await RefreshLocks.WaitAsync(linkedSiteId.ToString("N"), cancellationToken))
        {
            try
            {
                using var client = _dbContext.Client.CopyNew();
                client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
                var account = (await client.Queryable<GoogleAccount>()
                    .Where(item => item.LinkedSiteId == linkedSiteId)
                    .ToListAsync(cancellationToken))
                    .FirstOrDefault();
                if (account is null || account.Id != candidates[0].Id ||
                    (staleAccessToken is not null && !string.Equals(account.AccessToken, staleAccessToken, StringComparison.Ordinal)))
                    return false;

                client.Ado.BeginTran();
                try
                {
                account.IsEnabled = false;
                account.DisabledByUpstream = true;
                await client.Updateable(account)
                    .UpdateColumns(item => new { item.IsEnabled, item.DisabledByUpstream })
                    .ExecuteCommandAsync(cancellationToken);

                var site = await client.Queryable<Domain.Sites.Site>().InSingleAsync(account.LinkedSiteId);
                if (site is not null && site.IsEnabled)
                {
                    site.IsEnabled = false;
                    await client.Updateable(site)
                        .UpdateColumns(item => new { item.IsEnabled })
                        .ExecuteCommandAsync(cancellationToken);
                }

                client.Ado.CommitTran();
                }
                catch { client.Ado.RollbackTran(); throw; }
                finally
                {
                    _metadataCache.InvalidateRouteTargets();
                    _metadataCache.InvalidateGoogleAccounts();
                }
                _logger.LogWarning(
                    "Google account {Id} auto-disabled after upstream 403: {Reason}",
                    account.Id,
                    reason);
                return true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Unable to auto-disable Google account for linked site {SiteId}", linkedSiteId);
                return false;
            }
        }
    }

    internal static bool IsForbiddenResponse(Exception exception)
    {
        if (exception is NetworkPolicyException) return false;
        if (exception is HttpRequestException { StatusCode: HttpStatusCode.Forbidden })
        {
            return true;
        }

        var message = exception.Message;
        return message.Contains("403", StringComparison.OrdinalIgnoreCase)
            && (message.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
                || message.Contains("permission_denied", StringComparison.OrdinalIgnoreCase)
                || message.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                || message.Contains("returned 403", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> RefreshCoreAsync(
        Guid linkedSiteId,
        string staleAccessToken,
        CancellationToken cancellationToken, Guid? accountId = null)
    {
        try
        {
            var account = (await _dbContext.GoogleAccounts
                .Where(item => accountId.HasValue ? item.Id == accountId.Value : item.LinkedSiteId == linkedSiteId)
                .ToListAsync(cancellationToken))
                .FirstOrDefault();
            if (account is null || string.IsNullOrWhiteSpace(account.RefreshToken))
            {
                return null;
            }

            // 后台服务或其他并发请求已经完成刷新时直接复用新 token，避免重复轮换。
            if (!string.Equals(account.AccessToken, staleAccessToken, StringComparison.Ordinal))
            {
                return account.AccessToken;
            }

            using var network = _networkFactory?.CreateForAccount(new AccountIdentity("google", account.Id), "credential-refresh");
            cancellationToken.ThrowIfCancellationRequested();
            // A sent request can rotate the credential even if the original request is cancelled.
            using var refreshSafety = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var tokens = await (network?.GoogleOAuth ?? _oauth).RefreshTokenAsync(account.AccountKind, account.RefreshToken, refreshSafety.Token);
            await GoogleAccountProvisioner.PersistRotationAsync(_dbContext, _metadataCache, account.Id, tokens);

            _metadataCache.InvalidateRouteTargets();
            _metadataCache.InvalidateGoogleAccounts();
            _logger.LogInformation("Google account {Id} token refreshed after upstream unauthorized", account.Id);
            return tokens.AccessToken;
        }
        catch (NetworkPolicyException) { throw; }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Unable to refresh Google token for linked site {SiteId}", linkedSiteId);
            return null;
        }
    }
}
