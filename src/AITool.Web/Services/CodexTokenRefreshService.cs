using System.Collections.Concurrent;
using AITool.Application.Codex;
using AITool.Domain.Codex;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;

namespace AITool.Web.Services;

/// <summary>
/// 后台服务：周期扫描临期的 Codex 账号，用 refresh_token 刷新 access_token，
/// 写回 CodexAccount + LinkedSite.ApiKey 并失效路由缓存，保证转发链路始终用未过期 token。
/// <para>
/// 性能（P7/P8）：周期 5 分钟、按 TokenExpiresAt 错峰、每两次刷新间小延迟避免瞬时打满上游；
/// 与 OAuth 客户端的 single-flight 协同，同 token 并发只刷一次；失败不重试等下一轮自然退避。
/// </para>
/// </summary>
public sealed class CodexTokenRefreshService : BackgroundService
{
    /// <summary>扫描周期。</summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(5);

    /// <summary>提前刷新量：到期前多久就刷新（Codex access_token 有效期约 10-15 天，剩 1 天即刷新）。</summary>
    private static readonly TimeSpan RefreshLead = TimeSpan.FromDays(1);

    /// <summary>同一账号两次成功刷新的最小间隔：限制短有效期 token 的刷新频率。</summary>
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(30);

    /// <summary>同轮内每两次刷新间的小延迟，错峰避免瞬时打满上游。</summary>
    private static readonly TimeSpan InterAccountDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>上游明确拒绝刷新时的临时退避时间，避免每轮重复请求失效账号。</summary>
    private static readonly TimeSpan RefreshFailureBackoff = TimeSpan.FromHours(1);

    private readonly IServiceProvider _services;
    private readonly ILogger<CodexTokenRefreshService> _logger;
    private readonly IHostEnvironment _environment;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _refreshRetryAt = new();

    public CodexTokenRefreshService(
        IServiceProvider services,
        ILogger<CodexTokenRefreshService> logger,
        IHostEnvironment environment)
    {
        _services = services;
        _logger = logger;
        _environment = environment;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 测试环境跳过，避免后台循环干扰
        if (_environment.IsEnvironment("Testing"))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshDueAccountsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Codex token refresh loop error");
            }
            await Task.Delay(ScanInterval, stoppingToken);
        }
    }

    private async Task RefreshDueAccountsAsync(CancellationToken ct)
    {
        // BackgroundService 是 singleton，需建 scope 取 scoped AppDbContext
        using var scope = _services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        // 现取 typed HttpClient，避免单例持有导致 HttpMessageHandler 被钉死（DNS 陈旧）。
        var oauthClient = scope.ServiceProvider.GetRequiredService<ICodexOAuthClient>();

        // 尊重 Codex 功能总开关：关闭时跳过本轮
        var runtime = await cache.GetRuntimeSettingsAsync(ct);
        if (!runtime.OAuthFeaturesEnabled)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var refreshLeadTime = now + RefreshLead;
        var due = await dbContext.CodexAccounts
            .Where(a => a.IsEnabled
                        && !string.IsNullOrEmpty(a.RefreshToken)
                        && (a.TokenExpiresAt == null
                            || a.TokenExpiresAt <= refreshLeadTime))
            .OrderBy(a => a.TokenExpiresAt)
            .ToListAsync(ct);

        // 上游拒绝刷新时暂时跳过该账号，避免每个扫描周期重复触发同一个失败请求。
        var nowForBackoff = DateTimeOffset.UtcNow;
        due = due.Where(account => !ShouldBackoffRefresh(account.Id, nowForBackoff)).ToList();

        // 防刷新风暴：若上游返回的 expires_in 短于提前量（如导入凭证、上游变更），
        // 账号会每轮扫描都"临期"。30 分钟内成功刷新过且尚未真正过期的账号本轮跳过，
        // 把最坏情况的刷新频率从每 5 分钟一次限制到每小时至多两次；已过期账号不受限（转发链路急需新 token）。
        var recentRefreshFloor = nowForBackoff - MinRefreshInterval;
        due = due.Where(account =>
            account.TokenExpiresAt <= nowForBackoff
            || account.LastRefreshAt is null
            || account.LastRefreshAt <= recentRefreshFloor).ToList();

        if (due.Count == 0)
        {
            return;
        }

        var anyUpdated = false;
        foreach (var account in due)
        {
            if (ct.IsCancellationRequested) break;
            var updated = await RefreshOneAsync(dbContext, cache, oauthClient, account, ct);
            if (updated) anyUpdated = true;
            // 错峰
            await Task.Delay(InterAccountDelay, ct);
        }

        if (anyUpdated)
        {
            cache.InvalidateRouteTargets();
            cache.InvalidateCodexAccounts();
        }
    }

    private async Task<bool> RefreshOneAsync(AppDbContext db, ProxyRequestMetadataCache cache, ICodexOAuthClient oauthClient, CodexAccount account, CancellationToken ct)
    {
        try
        {
            // single-flight：OAuth 客户端内部保证同 refresh_token 并发只刷一次
            // HTTP 调用在锁外执行，避免长时间持有串行锁
            var tokens = await oauthClient.RefreshTokenAsync(account.RefreshToken!, ct);

            // DB 写入用串行锁包裹，避免并发竞态
            await db.SerialExecuteAsync(async () =>
            {
                // 仅在上游返回了非空值时才覆盖，避免空响应清空有效 token 导致永久无法刷新。
                if (!string.IsNullOrWhiteSpace(tokens.AccessToken))
                {
                    account.AccessToken = tokens.AccessToken;
                }
                // OpenAI 会轮换 refresh_token，但某些响应可能不返回新 refresh_token，
                // 此时保留旧值避免被清空导致永久无法刷新。
                if (!string.IsNullOrWhiteSpace(tokens.RefreshToken))
                {
                    account.RefreshToken = tokens.RefreshToken;
                }
                if (!string.IsNullOrEmpty(tokens.IdToken)) account.IdToken = tokens.IdToken;
                account.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600);
                account.LastRefreshAt = DateTimeOffset.UtcNow;
                await db.UpdateAsync(account, ct);

                var site = await db.Sites.InSingleAsync(account.LinkedSiteId);
                if (site != null && !string.IsNullOrWhiteSpace(tokens.AccessToken))
                {
                    site.ApiKey = tokens.AccessToken;
                    await db.UpdateAsync(site, ct);
                }
            }, ct);

            _refreshRetryAt.TryRemove(account.Id, out _);
            _logger.LogInformation("Codex account {Id} token refreshed", account.Id);
            return true;
        }
        catch (Exception ex)
        {
            if (IsForbiddenRefreshFailure(ex))
            {
                var retryAt = DateTimeOffset.UtcNow.Add(RefreshFailureBackoff);
                _refreshRetryAt[account.Id] = retryAt;
                // 403 属于账号当前凭证/区域不可刷新，不让异常堆栈污染启动日志；到期后自动重试。
                _logger.LogWarning(
                    "Codex account {Id} token refresh was rejected (403); temporarily skipped until {RetryAt}",
                    account.Id,
                    retryAt);
            }
            else
            {
                _logger.LogWarning(ex, "Refresh failed for Codex account {Id}", account.Id);
            }

            return false;
        }
    }

    private bool ShouldBackoffRefresh(Guid accountId, DateTimeOffset now)
    {
        if (!_refreshRetryAt.TryGetValue(accountId, out var retryAt)) return false;
        if (retryAt > now) return true;
        _refreshRetryAt.TryRemove(accountId, out _);
        return false;
    }

    private static bool IsForbiddenRefreshFailure(Exception exception)
    {
        return exception is InvalidOperationException
            && exception.Message.Contains("403 Forbidden", StringComparison.OrdinalIgnoreCase);
    }
}
