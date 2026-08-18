using System.Collections.Concurrent;
using AITool.Application.Codex;
using AITool.Domain.Codex;
using AITool.Infrastructure.Persistence;

namespace AITool.Web.Services;

/// <summary>
/// 在实时代理请求命中 Codex 上游 401 时，立即刷新账号凭证并同步隐藏站点。
/// </summary>
public sealed class CodexCredentialRefreshService
{
    /// <summary>
    /// 同一隐藏站点的 401 刷新采用 single-flight，避免并发请求重复轮换 refresh_token。
    /// </summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> RefreshLocks = new();

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
                account.AccessToken = tokens.AccessToken;
                account.RefreshToken = tokens.RefreshToken;
                if (!string.IsNullOrWhiteSpace(tokens.IdToken))
                {
                    account.IdToken = tokens.IdToken;
                }

                account.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(
                    tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600);
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
