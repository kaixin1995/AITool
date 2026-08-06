using AITool.Application.Codex;
using AITool.Domain.Codex;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;

namespace AITool.Admin.Services;

/// <summary>
/// 在实时代理请求命中 Codex 上游 401 时，立即刷新账号凭证并同步隐藏站点。
/// <para>
/// 仅在 Admin 宿主注册：依赖 AppDbContext 直接读写数据库。
/// Core 宿主无数据库访问，不使用本服务。
/// </para>
/// </summary>
public sealed class CodexCredentialRefreshService
{
    private readonly AppDbContext _dbContext;
    private readonly ICodexOAuthClient _oauth;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly AdminCacheInvalidationService _adminCacheInvalidation;
    private readonly ILogger<CodexCredentialRefreshService> _logger;

    public CodexCredentialRefreshService(
        AppDbContext dbContext,
        ICodexOAuthClient oauth,
        ProxyRequestMetadataCache metadataCache,
        AdminCacheInvalidationService adminCacheInvalidation,
        ILogger<CodexCredentialRefreshService> logger)
    {
        _dbContext = dbContext;
        _oauth = oauth;
        _metadataCache = metadataCache;
        _adminCacheInvalidation = adminCacheInvalidation;
        _logger = logger;
    }

    public async Task<string?> RefreshAsync(
        Guid linkedSiteId,
        string staleAccessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            var account = (await _dbContext.CodexAccounts
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

            var tokens = await _oauth.RefreshTokenAsync(account.RefreshToken, cancellationToken);
            await _dbContext.SerialExecuteAsync(async () =>
            {
                // 仅在上游返回了非空值时才覆盖，避免空响应清空有效 token。
                if (!string.IsNullOrWhiteSpace(tokens.AccessToken))
                {
                    account.AccessToken = tokens.AccessToken;
                }
                if (!string.IsNullOrWhiteSpace(tokens.RefreshToken))
                {
                    account.RefreshToken = tokens.RefreshToken;
                }
                if (!string.IsNullOrWhiteSpace(tokens.IdToken))
                {
                    account.IdToken = tokens.IdToken;
                }

                account.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(
                    tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600);
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
            _metadataCache.InvalidateCodexAccounts();
            // 路由目标缓存（含 Site.ApiKey）必须推送到 Core，否则 Core 转发仍用过期 token。
            await _adminCacheInvalidation.InvalidateRouteTargetsAsync(cancellationToken);
            _logger.LogInformation("Codex account {Id} token refreshed after upstream unauthorized", account.Id);
            return tokens.AccessToken;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Unable to refresh Codex token for linked site {SiteId}", linkedSiteId);
            return null;
        }
    }
}
