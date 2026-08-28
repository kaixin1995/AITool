using AITool.Infrastructure.Proxy;
using AITool.Application.Codex;
using AITool.Domain.Codex;
using AITool.Infrastructure.Common;
using AITool.Infrastructure.Persistence;

namespace AITool.Admin.Services;

/// <summary>
/// 在实时代理请求命中 Codex 上游 401 时，立即刷新账号凭证并同步隐藏站点。
/// </summary>
public sealed class CodexCredentialRefreshService
{
    /// <summary>
    /// 同一隐藏站点的 401 刷新采用 single-flight，避免并发请求重复轮换 refresh_token。
    /// </summary>
    private static readonly KeyedAsyncLock RefreshLocks = new();

    private readonly AppDbContext _dbContext;
    private readonly ICodexOAuthClient _oauth;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly ILogger<CodexCredentialRefreshService> _logger;

    public CodexCredentialRefreshService(
        AppDbContext dbContext,
        ICodexOAuthClient oauth,
        ProxyRequestMetadataCache metadataCache,
        ILogger<CodexCredentialRefreshService> logger)
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
        using (await RefreshLocks.WaitAsync(linkedSiteId.ToString("N"), cancellationToken))
        {
            return await RefreshCoreAsync(linkedSiteId, staleAccessToken, cancellationToken);
        }
    }

    private async Task<string?> RefreshCoreAsync(
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

            // 其他请求已经完成刷新时直接复用新 token，避免重复轮换。
            if (!string.Equals(account.AccessToken, staleAccessToken, StringComparison.Ordinal))
            {
                return account.AccessToken;
            }

            var tokens = await _oauth.RefreshTokenAsync(account.RefreshToken, cancellationToken);
            await _dbContext.SerialExecuteAsync(async () =>
            {
                if (!string.IsNullOrWhiteSpace(tokens.AccessToken))
                {
                    account.AccessToken = tokens.AccessToken;
                }

                // OpenAI 会轮换 refresh_token，但某些响应可能不返回新 refresh_token，保留旧值避免被清空导致永久无法刷新。
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
