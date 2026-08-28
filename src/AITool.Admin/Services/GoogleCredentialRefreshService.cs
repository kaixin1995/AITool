using AITool.Infrastructure.Proxy;
using AITool.Application.Google;
using AITool.Domain.Google;
using AITool.Infrastructure.Common;
using AITool.Infrastructure.Persistence;
using System.Net;

namespace AITool.Admin.Services;

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

    private readonly AppDbContext _dbContext;
    private readonly IGoogleOAuthClient _oauth;
    private readonly ProxyRequestMetadataCache _metadataCache;
    /// <summary>split 双宿主：变更推送 Core（惰性解析，避免 配额服务→失效服务→设置服务→配额服务 的 DI 环）。</summary>
    private readonly IServiceScopeFactory _corePushScopeFactory;
    private readonly ILogger<GoogleCredentialRefreshService> _logger;

    public GoogleCredentialRefreshService(
        AppDbContext dbContext,
        IGoogleOAuthClient oauth,
        ProxyRequestMetadataCache metadataCache,
        IServiceScopeFactory corePushScopeFactory,
        ILogger<GoogleCredentialRefreshService> logger)
    {
        _dbContext = dbContext;
        _oauth = oauth;
        _metadataCache = metadataCache;
        _corePushScopeFactory = corePushScopeFactory;
        _logger = logger;
    }

    public async Task<string?> RefreshAsync(
        Guid linkedSiteId,
        string staleAccessToken,
        CancellationToken cancellationToken)
    {
        using (await RefreshLocks.WaitAsync(linkedSiteId.ToString("N"), cancellationToken))
        {
            return await RefreshCoreAsync(linkedSiteId, staleAccessToken, cancellationToken);
        }
    }

    public async Task<bool> DisableAsync(
        Guid linkedSiteId,
        string reason,
        CancellationToken cancellationToken)
    {
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
                if (account is null)
                {
                    return false;
                }

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

                _metadataCache.InvalidateRouteTargets();
                await PushToCoreAsyncAccountCredentials(CancellationToken.None);
                _metadataCache.InvalidateGoogleAccounts();
                await PushToCoreAsyncAccountCredentials(CancellationToken.None);
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
                if (!string.IsNullOrWhiteSpace(tokens.AccessToken))
                {
                    account.AccessToken = tokens.AccessToken;
                }

                // Google 刷新响应通常不回传 refresh_token，保留旧值。
                if (!string.IsNullOrWhiteSpace(tokens.RefreshToken))
                {
                    account.RefreshToken = tokens.RefreshToken;
                }

                account.TokenExpiresAt = tokens.ExpiresAt;
                account.LastRefreshAt = DateTimeOffset.UtcNow;
                await _dbContext.UpdateAsync(account, cancellationToken);

                var site = await _dbContext.Sites.InSingleAsync(account.LinkedSiteId);
                if (site is not null && !string.IsNullOrWhiteSpace(tokens.AccessToken))
                {
                    site.ApiKey = tokens.AccessToken;
                    await _dbContext.UpdateAsync(site, cancellationToken);
                }
            }, cancellationToken);

            _metadataCache.InvalidateRouteTargets();
            await PushToCoreAsyncAccountCredentials(CancellationToken.None);
            _metadataCache.InvalidateGoogleAccounts();
            await PushToCoreAsyncAccountCredentials(CancellationToken.None);
            _logger.LogInformation("Google account {Id} token refreshed after upstream unauthorized", account.Id);
            return tokens.AccessToken;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Unable to refresh Google token for linked site {SiteId}", linkedSiteId);
            return null;
        }
    }

    /// <summary>惰性解析 AdminCacheInvalidationService 推送变更到 Core（scoped，调用点建作用域）。</summary>
    private async Task PushToCoreAsyncAccountCredentials(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _corePushScopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AdminCacheInvalidationService>()
                .InvalidateAccountCredentialsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // 推送失败不影响主流程：下次写操作或启动推送会重试。
        }
    }
}
