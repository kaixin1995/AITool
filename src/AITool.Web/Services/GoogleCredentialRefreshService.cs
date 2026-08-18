using System.Collections.Concurrent;
using AITool.Application.Google;
using AITool.Domain.Google;
using AITool.Infrastructure.Persistence;

namespace AITool.Web.Services;

/// <summary>
/// 在实时代理请求命中 Google 上游（GeminiCLI / Antigravity）401 时，立即刷新账号凭证并同步隐藏站点。
/// </summary>
public sealed class GoogleCredentialRefreshService
{
    /// <summary>
    /// 同一隐藏站点的 401 刷新采用 single-flight。Google 可能轮换 refresh_token，
    /// 并发重复刷新会让其中一个请求写回已失效的 token。
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> RefreshLocks = new();

    private readonly AppDbContext _dbContext;
    private readonly IGoogleOAuthClient _oauth;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly ILogger<GoogleCredentialRefreshService> _logger;

    public GoogleCredentialRefreshService(
        AppDbContext dbContext,
        IGoogleOAuthClient oauth,
        ProxyRequestMetadataCache metadataCache,
        ILogger<GoogleCredentialRefreshService> logger)
    {
        _dbContext = dbContext;
        _oauth = oauth;
        _metadataCache = metadataCache;
        _logger = logger;
    }

    public async Task<string?> RefreshAsync(
        Guid linkedSiteId,
        string staleAccessToken,
        CancellationToken cancellationToken)
    {
        var refreshLock = RefreshLocks.GetOrAdd(linkedSiteId, static _ => new SemaphoreSlim(1, 1));
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            return await RefreshCoreAsync(linkedSiteId, staleAccessToken, cancellationToken);
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task<string?> RefreshCoreAsync(
        Guid linkedSiteId,
        string staleAccessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var account = (await _dbContext.GoogleAccounts
                .Where(item => item.LinkedSiteId == linkedSiteId)
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

            var tokens = await _oauth.RefreshTokenAsync(account.AccountKind, account.RefreshToken, cancellationToken);
            await _dbContext.SerialExecuteAsync(async () =>
            {
                account.AccessToken = tokens.AccessToken;
                // Google 刷新响应通常不回传 refresh_token，保留旧值。
                if (!string.IsNullOrWhiteSpace(tokens.RefreshToken))
                {
                    account.RefreshToken = tokens.RefreshToken;
                }

                account.TokenExpiresAt = tokens.ExpiresAt;
                account.LastRefreshAt = DateTimeOffset.UtcNow;
                await _dbContext.UpdateAsync(account, cancellationToken);

                var site = await _dbContext.Sites.InSingleAsync(account.LinkedSiteId);
                if (site is not null)
                {
                    site.ApiKey = tokens.AccessToken;
                    await _dbContext.UpdateAsync(site, cancellationToken);
                }
            }, cancellationToken);

            _metadataCache.InvalidateRouteTargets();
            _metadataCache.InvalidateGoogleAccounts();
            _logger.LogInformation("Google account {Id} token refreshed after upstream unauthorized", account.Id);
            return tokens.AccessToken;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Unable to refresh Google token for linked site {SiteId}", linkedSiteId);
            return null;
        }
    }
}
