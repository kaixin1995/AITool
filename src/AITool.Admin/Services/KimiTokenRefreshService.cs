using AITool.Infrastructure.Proxy;
using System.Collections.Concurrent;
using AITool.Application.Kimi;
using AITool.Domain.Kimi;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;

namespace AITool.Admin.Services;

/// <summary>
/// 后台服务：周期扫描临期的 Kimi 账号，用 refresh_token 刷新 access_token，
/// 写回 KimiAccount + LinkedSite.ApiKey 并失效缓存，保证代理转发链路始终使用有效 token。
/// </summary>
public sealed class KimiTokenRefreshService : BackgroundService
{
    /// <summary>扫描周期：5 分钟。</summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(5);

    /// <summary>提前刷新量：剩 30 分钟即刷新（Kimi token 有效期通常为 30 天，可宽裕提前）。</summary>
    private static readonly TimeSpan RefreshLead = TimeSpan.FromMinutes(30);

    /// <summary>同一账号两次成功刷新的最小间隔：5 分钟（Kimi access_token 实测约 15 分钟有效，需更密集的保活节奏）。</summary>
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(5);

    /// <summary>同轮内每两次刷新间的小延迟。</summary>
    private static readonly TimeSpan InterAccountDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>上游明确拒绝刷新时的临时退避时间。</summary>
    private static readonly TimeSpan RefreshFailureBackoff = TimeSpan.FromMinutes(30);

    private readonly IServiceProvider _services;
    private readonly ILogger<KimiTokenRefreshService> _logger;
    private readonly IHostEnvironment _environment;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _refreshRetryAt = new();

    public KimiTokenRefreshService(
        IServiceProvider services,
        ILogger<KimiTokenRefreshService> logger,
        IHostEnvironment environment)
    {
        _services = services;
        _logger = logger;
        _environment = environment;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                _logger.LogError(ex, "Kimi token refresh loop error");
            }
            await Task.Delay(ScanInterval, stoppingToken);
        }
    }

    internal async Task RefreshDueAccountsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        // split 双宿主：变更需推送到 Core，否则 Core 仍用旧 token/旧启用状态。
        var adminCacheInvalidation = scope.ServiceProvider.GetRequiredService<AdminCacheInvalidationService>();
        var oauthClient = scope.ServiceProvider.GetRequiredService<IKimiOAuthClient>();

        var runtime = await cache.GetRuntimeSettingsAsync(ct);
        if (!runtime.OAuthFeaturesEnabled)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var refreshLeadTime = now + RefreshLead;

        using var client = dbContext.Client.CopyNew();
        var due = await client.Queryable<KimiAccount>()
            .Where(a => !a.IsDeleted
                        && !string.IsNullOrEmpty(a.RefreshToken)
                        && (a.TokenExpiresAt == null || a.TokenExpiresAt <= refreshLeadTime))
            .OrderBy(a => a.TokenExpiresAt)
            .ToListAsync(ct);

        var nowForBackoff = DateTimeOffset.UtcNow;
        due = due.Where(account => !ShouldBackoffRefresh(account.Id, nowForBackoff)).ToList();

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
            await Task.Delay(InterAccountDelay, ct);
        }

        if (anyUpdated)
        {
            cache.InvalidateRouteTargets();
            await adminCacheInvalidation.InvalidateRouteTargetsAsync(ct);
            cache.InvalidateKimiAccounts();
        }
    }

    private async Task<bool> RefreshOneAsync(AppDbContext db, ProxyRequestMetadataCache cache, IKimiOAuthClient oauthClient, KimiAccount account, CancellationToken ct)
    {
        try
        {
            var tokens = await oauthClient.RefreshTokenAsync(account.RefreshToken!, account.DeviceId, ct);

            using var client = db.Client.CopyNew();
            client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");

            if (!string.IsNullOrWhiteSpace(tokens.AccessToken))
            {
                account.AccessToken = tokens.AccessToken;
            }
            if (!string.IsNullOrWhiteSpace(tokens.RefreshToken))
            {
                account.RefreshToken = tokens.RefreshToken;
            }
            account.TokenExpiresAt = tokens.ExpiresAt;
            account.LastRefreshAt = DateTimeOffset.UtcNow;
            account.UpdatedAt = DateTimeOffset.UtcNow;

            await client.Updateable(account).ExecuteCommandAsync(ct);

            var site = await client.Queryable<Site>().InSingleAsync(account.LinkedSiteId);
            if (site != null && !string.IsNullOrWhiteSpace(tokens.AccessToken))
            {
                site.ApiKey = tokens.AccessToken;
                await client.Updateable(site).UpdateColumns(s => new { s.ApiKey }).ExecuteCommandAsync(ct);
            }

            _refreshRetryAt.TryRemove(account.Id, out _);
            _logger.LogInformation("Kimi account {Id} ({DisplayName}) token refreshed", account.Id, account.DisplayName);
            return true;
        }
        catch (Exception ex)
        {
            var accountName = !string.IsNullOrWhiteSpace(account.DisplayName)
                ? account.DisplayName
                : account.Email ?? account.Id.ToString();

            var retryAt = DateTimeOffset.UtcNow.Add(RefreshFailureBackoff);
            _refreshRetryAt[account.Id] = retryAt;
            _logger.LogWarning(ex, "Kimi 账号 [{Name}] (Id: {Id}) Token 刷新失败，已退避至 {RetryAt}", accountName, account.Id, retryAt);
            return false;
        }
    }

    private bool ShouldBackoffRefresh(Guid accountId, DateTimeOffset now)
    {
        if (_refreshRetryAt.TryGetValue(accountId, out var retryAt))
        {
            if (now < retryAt)
            {
                return true;
            }
            _refreshRetryAt.TryRemove(accountId, out _);
        }
        return false;
    }
}
