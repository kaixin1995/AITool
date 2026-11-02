using AITool.Application.Networking;
using System.Collections.Concurrent;
using AITool.Application.Google;
using AITool.Domain.Google;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;

namespace AITool.Web.Services;

/// <summary>
/// 后台服务：周期扫描临期的 Google 账号（Antigravity），用 refresh_token 刷新 access_token，
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
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset RetryAt, string? AccessToken, string? RefreshToken)> _refreshRetryAt = new();

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
        var networkFactory = scope.ServiceProvider.GetService<NetworkBoundOAuthClientFactory>();

        // 尊重 OAuth 功能总开关：关闭时跳过本轮
        var runtime = await cache.GetRuntimeSettingsAsync(ct);
        if (!runtime.OAuthFeaturesEnabled)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var refreshLeadTime = now + RefreshLead;
        var due = await dbContext.GoogleAccounts
            .Where(a => !a.RequiresReauth && !string.IsNullOrEmpty(a.RefreshToken)
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
            var updated = await RefreshOneAsync(dbContext, cache, oauthClient, account, ct, networkFactory);
            if (updated) anyUpdated = true;
            await Task.Delay(InterAccountDelay, ct);
        }

        if (anyUpdated)
        {
            cache.InvalidateRouteTargets();
            cache.InvalidateGoogleAccounts();
        }
    }

    private async Task<bool> RefreshOneAsync(AppDbContext db, ProxyRequestMetadataCache cache, IGoogleOAuthClient oauthClient, GoogleAccount account, CancellationToken ct, NetworkBoundOAuthClientFactory? networkFactory = null)
    {
        try
        {
            using var refreshLease = await NetworkBoundOAuthClientFactory.LockCredentialsAsync("google", account.Id, ct);
            var current = await db.GoogleAccounts.InSingleAsync(account.Id);
            if (current is null || current.RequiresReauth || !string.Equals(current.AccessToken, account.AccessToken, StringComparison.Ordinal)) return false;
            account = current;
            if (string.IsNullOrWhiteSpace(account.RefreshToken)) return false;
            using var network = networkFactory?.CreateForAccount(new AccountIdentity("google", account.Id), "background-token-refresh");
            ct.ThrowIfCancellationRequested();
            // Once sent, the upstream may consume the old refresh token even if the caller stops.
            using var refreshSafety = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var tokens = await (network?.GoogleOAuth ?? oauthClient).RefreshTokenAsync(account.AccountKind, account.RefreshToken, refreshSafety.Token);
            await GoogleAccountProvisioner.PersistRotationAsync(db, cache, account.Id, tokens);

            _refreshRetryAt.TryRemove(account.Id, out _);
            _logger.LogInformation("Google account {Id} token refreshed", account.Id);
            return true;
        }
        catch (Exception ex)
        {
            var accountName = !string.IsNullOrWhiteSpace(account.DisplayName)
                ? account.DisplayName
                : account.Email ?? account.Id.ToString();

            if (IsInvalidGrantFailure(ex, out var reason))
            {
                var retryAt = DateTimeOffset.UtcNow.Add(RefreshFailureBackoff);
                _refreshRetryAt[account.Id] = (retryAt, account.AccessToken, account.RefreshToken);
                _logger.LogWarning(
                    "Google 账号 [{Name}] (Id: {Id}) Token 刷新失败：{Reason}，已暂停重试至 {RetryAt}。若需使用请在账号管理中重新授权登录。",
                    accountName,
                    account.Id,
                    reason,
                    retryAt);
            }
            else if (IsForbiddenRefreshFailure(ex))
            {
                var retryAt = DateTimeOffset.UtcNow.Add(RefreshFailureBackoff);
                _refreshRetryAt[account.Id] = (retryAt, account.AccessToken, account.RefreshToken);
                _logger.LogWarning(
                    "Google 账号 [{Name}] (Id: {Id}) Token 刷新被上游拒绝 (403 Forbidden)，已暂时跳过重试至 {RetryAt}",
                    accountName,
                    account.Id,
                    retryAt);
            }
            else
            {
                _logger.LogWarning(ex, "Google 账号 [{Name}] (Id: {Id}) Token 刷新异常", accountName, account.Id);
            }

            return false;
        }
    }

    private bool ShouldBackoffRefresh(Guid accountId, DateTimeOffset now)
    {
        if (!_refreshRetryAt.TryGetValue(accountId, out var retryAt)) return false;
        if (retryAt.RetryAt > now) return true;
        _refreshRetryAt.TryRemove(accountId, out _);
        return false;
    }

    private static bool IsInvalidGrantFailure(Exception exception, out string reason)
    {
        reason = string.Empty;
        if (exception is not InvalidOperationException)
        {
            return false;
        }

        var message = exception.Message;
        if (message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("expired or revoked", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("401", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Refresh Token 已失效或被上游撤销 (invalid_grant)";
            return true;
        }

        if (message.Contains("400", StringComparison.OrdinalIgnoreCase))
        {
            reason = "请求参数或凭证无效 (400 Bad Request)";
            return true;
        }

        return false;
    }

    private static bool IsForbiddenRefreshFailure(Exception exception)
    {
        return exception is InvalidOperationException
            && (exception.Message.Contains("403", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase));
    }
}
