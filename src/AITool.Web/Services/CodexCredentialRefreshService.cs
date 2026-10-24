using AITool.Application.Codex;
using AITool.Domain.Codex;
using AITool.Infrastructure.Common;
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
        // 锁获取与前置检查仍受调用方取消约束：此时尚未发出任何刷新请求，取消是安全的。
        using (await RefreshLocks.WaitAsync(linkedSiteId.ToString("N"), cancellationToken))
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

            // 关键段：OpenAI 每次刷新都会轮换 refresh_token，刷新请求一旦发出，旧 token 即被消费。
            // 此后即使调用方（代理请求）已断开，也必须等响应落盘完成——否则新 token 丢失、旧 token 作废，
            // 账号将因 refresh_token_reused 永久失效。因此本段改用内部超时令牌，与调用方取消隔离。
            using var coreCts = new CancellationTokenSource(RefreshSafetyTimeout);
            try
            {
                return await RefreshAndPersistAsync(account, coreCts.Token);
            }
            catch (OperationCanceledException exception) when (coreCts.IsCancellationRequested)
            {
                _logger.LogWarning(exception,
                    "Codex token refresh for linked site {SiteId} exceeded {Timeout}s safety timeout", linkedSiteId, RefreshSafetyTimeout.TotalSeconds);
                return null;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Unable to refresh Codex token for linked site {SiteId}", linkedSiteId);
                return null;
            }
        }
    }

    /// <summary>关键段的上限时长：覆盖“刷新请求发出 → 响应落盘”全过程，防止站点锁被长时间占用。</summary>
    private static readonly TimeSpan RefreshSafetyTimeout = TimeSpan.FromSeconds(30);

    private async Task<string?> RefreshAndPersistAsync(
        CodexAccount account,
        CancellationToken cancellationToken)
    {
        var tokens = await _oauth.RefreshTokenAsync(account.RefreshToken!, cancellationToken);
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
}
