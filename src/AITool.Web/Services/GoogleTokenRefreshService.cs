using System.Collections.Concurrent;
using AITool.Application.Google;
using AITool.Domain.Google;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;

namespace AITool.Web.Services;

/// <summary>
/// 后台服务：周期扫描临期的 Google 账号（GeminiCLI / Antigravity），用 refresh_token 刷新 access_token，
/// 写回 GoogleAccount + LinkedSite.ApiKey 并失效路由缓存，保证转发链路始终用未过期 token。
/// <para>
/// Google access_token 有效期约 1 小时：扫描周期 5 分钟、提前 10 分钟刷新、
/// 同账号两次成功刷新最小间隔 5 分钟（防刷新风暴）。与 Codex 版结构一致。
/// </para>
/// </summary>
public sealed class GoogleTokenRefreshService : BackgroundService
{
    /// <summary>扫描周期。</summary>
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(5);

    /// <summary>提前刷新量：Google access_token 有效期约 1 小时，剩 10 分钟即刷新。</summary>
    private static readonly TimeSpan RefreshLead = TimeSpan.FromMinutes(10);

    /// <summary>同一账号两次成功刷新的最小间隔：短有效期 token 的刷新频率上限。</summary>
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(5);

    /// <summary>同轮内每两次刷新间的小延迟，错峰避免瞬时打满上游。</summary>
    private static readonly TimeSpan InterAccountDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>上游明确拒绝刷新时的临时退避时间，避免每轮重复请求失效账号。</summary>
    private static readonly TimeSpan RefreshFailureBackoff = TimeSpan.FromMinutes(30);

    private readonly IServiceProvider _services;
    private readonly ILogger<GoogleTokenRefreshService> _logger;
    private readonly IHostEnvironment _environment;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _refreshRetryAt = new();

    public GoogleTokenRefreshService(
        IServiceProvider services,
        ILogger<GoogleTokenRefreshService> logger,
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
                _logger.LogError(ex, "Google token refresh loop error");
            }
            await Task.Delay(ScanInterval, stoppingToken);
        }
    }

    internal async Task RefreshDueAccountsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        var oauthClient = scope.ServiceProvider.GetRequiredService<IGoogleOAuthClient>();

        // 尊重 OAuth 功能总开关：关闭时跳过本轮
        var runtime = await cache.GetRuntimeSettingsAsync(ct);
        if (!runtime.OAuthFeaturesEnabled)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var refreshLeadTime = now + RefreshLead;
        var due = await dbContext.GoogleAccounts
            .Where(a => !string.IsNullOrEmpty(a.RefreshToken)
                        && (a.TokenExpiresAt == null
                            || a.TokenExpiresAt <= refreshLeadTime))
            .OrderBy(a => a.TokenExpiresAt)
            .ToListAsync(ct);

        // 上游拒绝刷新（invalid_grant 等）时暂时跳过该账号。
        var nowForBackoff = DateTimeOffset.UtcNow;
        due = due.Where(account => !ShouldBackoffRefresh(account.Id, nowForBackoff)).ToList();

        // 防刷新风暴：5 分钟内成功刷新过且尚未真正过期的账号本轮跳过。
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
            cache.InvalidateGoogleAccounts();
        }
    }

    private async Task<bool> RefreshOneAsync(AppDbContext db, ProxyRequestMetadataCache cache, IGoogleOAuthClient oauthClient, GoogleAccount account, CancellationToken ct)
    {
        try
        {
            var tokens = await oauthClient.RefreshTokenAsync(account.AccountKind, account.RefreshToken!, ct);

            await db.SerialExecuteAsync(async () =>
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
                await db.UpdateAsync(account, ct);

                var site = await db.Sites.InSingleAsync(account.LinkedSiteId);
                if (site != null && !string.IsNullOrWhiteSpace(tokens.AccessToken))
                {
                    site.ApiKey = tokens.AccessToken;
                    await db.UpdateAsync(site, ct);
                }
            }, ct);

            _refreshRetryAt.TryRemove(account.Id, out _);
            _logger.LogInformation("Google account {Id} token refreshed", account.Id);
            return true;
        }
        catch (Exception ex)
        {
            if (IsInvalidGrantFailure(ex))
            {
                var retryAt = DateTimeOffset.UtcNow.Add(RefreshFailureBackoff);
                _refreshRetryAt[account.Id] = retryAt;
                _logger.LogWarning(
                    "Google account {Id} token refresh was rejected (invalid_grant); temporarily skipped until {RetryAt}",
                    account.Id,
                    retryAt);
            }
            else
            {
                _logger.LogWarning(ex, "Refresh failed for Google account {Id}", account.Id);
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

    private static bool IsInvalidGrantFailure(Exception exception)
    {
        return exception is InvalidOperationException
            && exception.Message.Contains("400", StringComparison.OrdinalIgnoreCase)
            && exception.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase);
    }
}
