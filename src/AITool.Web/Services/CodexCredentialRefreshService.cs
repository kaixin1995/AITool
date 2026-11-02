using AITool.Application.Networking;
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

    private readonly NetworkBoundOAuthClientFactory? _networkFactory;
    private readonly AppDbContext _dbContext;
    private readonly ICodexOAuthClient _oauth;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly ILogger<CodexCredentialRefreshService> _logger;

    public CodexCredentialRefreshService(
        AppDbContext dbContext,
        ICodexOAuthClient oauth,
        ProxyRequestMetadataCache metadataCache,
        ILogger<CodexCredentialRefreshService> logger,
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
        using (await NetworkBoundOAuthClientFactory.LockCredentialsAsync("codex", accountId, cancellationToken))
        {
            var account = await _dbContext.CodexAccounts.InSingleAsync(accountId);
            if (account is null || string.IsNullOrWhiteSpace(account.RefreshToken)) return null;
            if (!string.Equals(account.AccessToken, staleAccessToken, StringComparison.Ordinal)) return account.AccessToken;
            using var coreCts = new CancellationTokenSource(RefreshSafetyTimeout);
            return await RefreshAndPersistAsync(account, coreCts.Token);
        }
    }

    public async Task<string?> RefreshAsync(Guid linkedSiteId, string staleAccessToken, CancellationToken cancellationToken)
    {
        if (linkedSiteId == Guid.Empty) return null;
        var accounts = await _dbContext.CodexAccounts.Where(a => a.LinkedSiteId == linkedSiteId)
            .ToListAsync(cancellationToken);
        if (accounts.Count != 1) return null;
        try
        {
            return await RefreshForAccountAsync(accounts[0].Id, staleAccessToken, cancellationToken);
        }
        catch (NetworkPolicyException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Unable to refresh Codex token for linked site {SiteId}", linkedSiteId);
            return null;
        }
    }

    /// <summary>关键段的上限时长：覆盖“刷新请求发出 → 响应落盘”全过程，防止站点锁被长时间占用。</summary>
    private static readonly TimeSpan RefreshSafetyTimeout = TimeSpan.FromSeconds(30);

    private async Task<string?> RefreshAndPersistAsync(
        CodexAccount account,
        CancellationToken cancellationToken)
    {
        using var network = _networkFactory?.CreateForAccount(new AccountIdentity("codex", account.Id), "credential-refresh");
        var tokens = await (network?.CodexOAuth ?? _oauth).RefreshTokenAsync(account.RefreshToken!, cancellationToken);
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
            // 列限定写回（token 五列）：account 是转发路径的快照，整行写回会把供给刚
            // 升级的 DisplayName/Email/PlanType/AccountId 回滚成旧值。
            // 已知残余：token 五列与其他刷新写者 last-writer-wins——HTTP 窗口内用户重登写入的
            // 新凭证可能被本请求轮换出的（通常仍有效的）凭证整组替换，低概率且凭证可用，接受。
            await _dbContext.Client.Updateable(account)
                .UpdateColumns(a => new { a.AccessToken, a.RefreshToken, a.IdToken, a.TokenExpiresAt, a.LastRefreshAt })
                .ExecuteCommandAsync(cancellationToken);

            var site = await _dbContext.Sites.InSingleAsync(account.LinkedSiteId);
            if (site is not null && !string.IsNullOrWhiteSpace(tokens.AccessToken))
            {
                site.ApiKey = tokens.AccessToken;
                await _dbContext.Client.Updateable(site).UpdateColumns(s => s.ApiKey).ExecuteCommandAsync(cancellationToken);
            }
        }, cancellationToken);

        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateCodexAccounts();
        _logger.LogInformation("Codex account {Id} token refreshed after upstream unauthorized", account.Id);
        return tokens.AccessToken;
    }
}
