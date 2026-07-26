using AITool.Infrastructure.Proxy;
using AITool.Application.Codex;
using AITool.Domain.Codex;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;

namespace AITool.Admin.Services;

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

    /// <summary>提前刷新量：到期前多久就刷新。</summary>
    private static readonly TimeSpan RefreshLead = TimeSpan.FromHours(1);

    /// <summary>同轮内每两次刷新间的小延迟，错峰避免瞬时打满上游。</summary>
    private static readonly TimeSpan InterAccountDelay = TimeSpan.FromMilliseconds(500);

    private readonly IServiceProvider _services;
    private readonly ICodexOAuthClient _oauth;
    private readonly ILogger<CodexTokenRefreshService> _logger;
    private readonly IHostEnvironment _environment;

    public CodexTokenRefreshService(
        IServiceProvider services,
        ICodexOAuthClient oauth,
        ILogger<CodexTokenRefreshService> logger,
        IHostEnvironment environment)
    {
        _services = services;
        _oauth = oauth;
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
        // 双宿主下需通过 AdminCacheInvalidationService 把变更推送到 Core，否则 Core 仍用旧 token。
        var adminCacheInvalidation = scope.ServiceProvider.GetRequiredService<AdminCacheInvalidationService>();

        // 尊重 Codex 功能总开关：关闭时跳过本轮
        var runtime = await cache.GetRuntimeSettingsAsync(ct);
        if (!runtime.CodexFeaturesEnabled)
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

        if (due.Count == 0)
        {
            return;
        }

        var anyUpdated = false;
        foreach (var account in due)
        {
            if (ct.IsCancellationRequested) break;
            var updated = await RefreshOneAsync(dbContext, cache, account, ct);
            if (updated) anyUpdated = true;
            // 错峰
            await Task.Delay(InterAccountDelay, ct);
        }

        if (anyUpdated)
        {
            // 路由目标缓存（含 Site.ApiKey）必须推送到 Core，否则 Core 转发仍用过期 token。
            await adminCacheInvalidation.InvalidateRouteTargetsAsync(ct);
            // CodexAccounts 缓存只在 Admin 端（Core 不缓存账号实体），本地失效即可。
            cache.InvalidateCodexAccounts();
        }
    }

    private async Task<bool> RefreshOneAsync(AppDbContext db, ProxyRequestMetadataCache cache, CodexAccount account, CancellationToken ct)
    {
        try
        {
            // single-flight：OAuth 客户端内部保证同 refresh_token 并发只刷一次
            var tokens = await _oauth.RefreshTokenAsync(account.RefreshToken!, ct);

            account.AccessToken = tokens.AccessToken;
            account.RefreshToken = tokens.RefreshToken; // 部分上游轮换 refresh_token，以返回值为准
            if (!string.IsNullOrEmpty(tokens.IdToken)) account.IdToken = tokens.IdToken;
            account.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn > 0 ? tokens.ExpiresIn : 3600);
            account.LastRefreshAt = DateTimeOffset.UtcNow;
            await db.UpdateAsync(account, ct);

            // 同步写回隐藏 Site.ApiKey（列更新，避免全字段覆盖并发写）
            var site = await db.Sites.InSingleAsync(account.LinkedSiteId);
            if (site != null)
            {
                site.ApiKey = tokens.AccessToken;
                await db.UpdateAsync(site, ct);
            }

            _logger.LogInformation("Codex account {Id} token refreshed", account.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refresh failed for Codex account {Id}", account.Id);
            return false;
        }
    }
}
