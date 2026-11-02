using AITool.Application.Networking;
using System.Collections.Concurrent;
using AITool.Application.Xai;
using AITool.Domain.Sites;
using AITool.Domain.Xai;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;

namespace AITool.Web.Services;

/// <summary>
/// 后台服务：周期扫描临期的 xAI 账号，用 refresh_token 刷新 access_token，
/// 写回 XaiAccount + LinkedSite.ApiKey 并失效路由缓存，保证转发链路始终使用有效 token。
/// xAI access_token 约 6 小时过期，提前 30 分钟刷新；上游拒绝刷新（invalid_grant 等）
/// 时标记 RequiresReauth 并退避，等待用户重新设备码登录。
/// </summary>
public sealed class XaiTokenRefreshService : BackgroundService
{
    /// <summary>扫描周期：5 分钟。</summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(5);

    /// <summary>提前刷新量：剩 30 分钟即刷新。</summary>
    private static readonly TimeSpan RefreshLead = TimeSpan.FromMinutes(30);

    /// <summary>同一账号两次成功刷新的最小间隔：5 分钟。</summary>
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(5);

    /// <summary>同轮内每两次刷新间的小延迟。</summary>
    private static readonly TimeSpan InterAccountDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>上游明确拒绝刷新时的临时退避时间。</summary>
    private static readonly TimeSpan RefreshFailureBackoff = TimeSpan.FromMinutes(30);

    private readonly IServiceProvider _services;
    private readonly ILogger<XaiTokenRefreshService> _logger;
    private readonly IHostEnvironment _environment;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _refreshRetryAt = new();

    public XaiTokenRefreshService(
        IServiceProvider services,
        ILogger<XaiTokenRefreshService> logger,
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
                _logger.LogError(ex, "xAI token refresh loop error");
            }
            await Task.Delay(ScanInterval, stoppingToken);
        }
    }

    internal async Task RefreshDueAccountsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        var oauthClient = scope.ServiceProvider.GetRequiredService<IXaiOAuthClient>();
        var networkFactory = scope.ServiceProvider.GetService<NetworkBoundOAuthClientFactory>();

        var runtime = await cache.GetRuntimeSettingsAsync(ct);
        if (!runtime.OAuthFeaturesEnabled)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var refreshLeadTime = now + RefreshLead;

        using var client = dbContext.Client.CopyNew();
        var due = await client.Queryable<XaiAccount>()
            .Where(a => !a.IsDeleted
                        && !a.RequiresReauth
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
            var updated = await RefreshOneAsync(dbContext, cache, oauthClient, account, ct, networkFactory);
            if (updated) anyUpdated = true;
            await Task.Delay(InterAccountDelay, ct);
        }

        if (anyUpdated)
        {
            cache.InvalidateRouteTargets();
        }
    }

    private async Task<bool> RefreshOneAsync(AppDbContext db, ProxyRequestMetadataCache cache, IXaiOAuthClient oauthClient, XaiAccount account, CancellationToken ct, NetworkBoundOAuthClientFactory? networkFactory = null)
    {
        try
        {
            using var refreshLease = await NetworkBoundOAuthClientFactory.LockCredentialsAsync("xai", account.Id, ct);
            var current = await db.XaiAccounts.InSingleAsync(account.Id);
            if (current is null || current.IsDeleted || current.RequiresReauth || string.IsNullOrWhiteSpace(current.RefreshToken)
                || !string.Equals(current.AccessToken, account.AccessToken, StringComparison.Ordinal)) return false;
            account = current;
            using var network = networkFactory?.CreateForAccount(new AccountIdentity("xai", account.Id), "background-token-refresh");
            var tokens = await (network?.XaiOAuth ?? oauthClient).RefreshTokenAsync(account.RefreshToken!, ct);

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

            await client.Updateable(account)
                .UpdateColumns(a => new { a.AccessToken, a.RefreshToken, a.TokenExpiresAt, a.LastRefreshAt, a.UpdatedAt })
                .ExecuteCommandAsync(ct);

            var site = await client.Queryable<Site>().InSingleAsync(account.LinkedSiteId);
            if (site != null && !string.IsNullOrWhiteSpace(tokens.AccessToken))
            {
                site.ApiKey = tokens.AccessToken;
                await client.Updateable(site).UpdateColumns(s => new { s.ApiKey }).ExecuteCommandAsync(ct);
            }

            _refreshRetryAt.TryRemove(account.Id, out _);
            _logger.LogInformation("xAI account {Id} ({DisplayName}) token refreshed", account.Id, account.DisplayName);
            return true;
        }
        catch (XaiRefreshTokenInvalidException ex)
        {
            // refresh_token 已被上游拒绝：标记重新登录并长期退避（不再周期重试）。
            try
            {
                using var client = db.Client.CopyNew();
                client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
                var updatedAt = DateTimeOffset.UtcNow;
                await client.Updateable<XaiAccount>()
                    .SetColumns(a => new XaiAccount { RequiresReauth = true, UpdatedAt = updatedAt })
                    .Where(a => a.Id == account.Id && !a.IsDeleted && a.AccessToken == account.AccessToken
                        && a.RefreshToken == account.RefreshToken && a.LastRefreshAt == account.LastRefreshAt)
                    .ExecuteCommandAsync(ct);
                cache.InvalidateRouteTargets();
            }
            catch (Exception persistEx)
            {
                _logger.LogWarning(persistEx, "Failed to mark xAI account {Id} as requires-reauth", account.Id);
            }

            var accountName = !string.IsNullOrWhiteSpace(account.DisplayName) ? account.DisplayName : account.Email ?? account.Id.ToString();
            _logger.LogWarning(ex, "xAI 账号 [{Name}] (Id: {Id}) refresh_token 已失效，已标记需重新登录", accountName, account.Id);
            _refreshRetryAt[account.Id] = DateTimeOffset.UtcNow.AddHours(12);
            return false;
        }
        catch (Exception ex)
        {
            var accountName = !string.IsNullOrWhiteSpace(account.DisplayName)
                ? account.DisplayName
                : account.Email ?? account.Id.ToString();

            var retryAt = DateTimeOffset.UtcNow.Add(RefreshFailureBackoff);
            _refreshRetryAt[account.Id] = retryAt;
            _logger.LogWarning(ex, "xAI 账号 [{Name}] (Id: {Id}) Token 刷新失败，已退避至 {RetryAt}", accountName, account.Id, retryAt);
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
