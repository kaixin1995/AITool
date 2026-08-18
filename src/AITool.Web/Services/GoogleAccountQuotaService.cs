using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AITool.Application.Accounts;
using AITool.Application.Google;
using AITool.Domain.Google;
using AITool.Infrastructure.Common;
using AITool.Infrastructure.Google;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;

namespace AITool.Web.Services;

/// <summary>
/// Google 账号额度主动查询实现（IAccountQuotaProvider，ProviderKey="google"）。
/// <para>
/// Antigravity：v1internal:fetchAvailableModels 返回每个模型的 quotaInfo.remainingFraction
/// （对齐 gcli2api fetch_quota_info），换算为已用百分比窗口；tier/积分取登录时 loadCodeAssist 的存档。
/// GeminiCLI：上游无额度查询接口（对齐 gcli2api：仅展示 tier），返回带 tier 的空窗口快照。
/// </para>
/// </summary>
public sealed class GoogleAccountQuotaService : IAccountQuotaProvider
{
    private static readonly TimeSpan ResultCacheTtl = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly IMemoryCache _resultCache;
    private readonly GoogleCredentialRefreshService _credentialRefreshService;
    private readonly ILogger<GoogleAccountQuotaService> _logger;

    /// <summary>single-flight：同 accountId 并发只一次真实请求。KeyedAsyncLock 会在账号不再使用时回收锁条目。</summary>
    private static readonly KeyedAsyncLock Locks = new();

    public GoogleAccountQuotaService(
        HttpClient httpClient,
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        IMemoryCache resultCache,
        GoogleCredentialRefreshService credentialRefreshService,
        ILogger<GoogleAccountQuotaService> logger)
    {
        _httpClient = httpClient;
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _resultCache = resultCache;
        _credentialRefreshService = credentialRefreshService;
        _logger = logger;
    }

    public string ProviderKey => "google";

    /// <summary>额度查询结果（内部口径）。</summary>
    private sealed record GoogleQuotaInfo
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public string? PlanType { get; init; }
        public string RawJson { get; init; } = string.Empty;
        public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
        public IReadOnlyList<GoogleQuotaWindow> Windows { get; init; } = [];
    }

    private sealed record GoogleQuotaWindow
    {
        public string Id { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public double UsedPercent { get; init; }
        public string ResetLabel { get; init; } = "N/A";
        public DateTimeOffset? ResetAtUtc { get; init; }
    }

    public async Task<IReadOnlyList<AccountQuotaTarget>> GetAccountsAsync(CancellationToken cancellationToken)
    {
        var accounts = await _dbContext.GoogleAccounts
            .Where(a => !a.DisabledByFeatureToggle)
            .OrderBy(a => a.LastQuotaCheckedAt)
            .ToListAsync(cancellationToken);

        return accounts.Select(ToQuotaTarget).ToList();
    }

    public AccountQuotaSnapshot? ParseCachedQuota(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            var windows = GoogleQuotaParser.Parse(rawJson);
            if (windows is null)
            {
                return null;
            }

            return new AccountQuotaSnapshot
            {
                Success = true,
                RawJson = rawJson,
                Windows = windows.Select(ToQuotaWindow).ToList(),
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<AccountQuotaSnapshot> QueryAsync(
        AccountQuotaTarget account,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var current = (await _dbContext.GoogleAccounts
            .Where(a => a.Id == account.AccountId)
            .ToListAsync(cancellationToken))
            .FirstOrDefault();

        if (current is null)
        {
            return new AccountQuotaSnapshot
            {
                Success = false,
                Error = "账号不存在",
                CheckedAt = DateTimeOffset.UtcNow,
            };
        }

        var info = await QueryAsync(current, forceRefresh, cancellationToken);
        return ToQuotaSnapshot(info);
    }

    public async Task SetEnabledAsync(
        AccountQuotaTarget account,
        bool enabled,
        string reason,
        CancellationToken cancellationToken)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var current = (await client.Queryable<GoogleAccount>()
            .Where(a => a.Id == account.AccountId)
            .ToListAsync(cancellationToken))
            .FirstOrDefault();
        if (current is null) return;

        if (enabled)
        {
            current.IsEnabled = true;
            if (string.Equals(reason, "quota-recovered", StringComparison.OrdinalIgnoreCase))
            {
                current.ManuallyDisabled = false;
                current.IsQuotaCooling = false;
                current.QuotaCoolingUntil = null;
            }
            if (string.Equals(reason, "feature-toggle-on", StringComparison.OrdinalIgnoreCase))
            {
                current.DisabledByFeatureToggle = false;
            }
        }
        else
        {
            current.IsEnabled = false;
            if (string.Equals(reason, "feature-toggle-off", StringComparison.OrdinalIgnoreCase))
            {
                current.DisabledByFeatureToggle = account.IsEnabled;
            }
        }

        await client.Updateable(current)
            .UpdateColumns(x => new { x.IsEnabled, x.ManuallyDisabled, x.DisabledByFeatureToggle, x.IsQuotaCooling, x.QuotaCoolingUntil })
            .ExecuteCommandAsync(cancellationToken);
        await SetLinkedSiteEnabledAsync(client, current.LinkedSiteId, enabled, cancellationToken);
        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateGoogleAccounts();
    }

    public async Task ApplyFeatureToggleAsync(bool enabled, CancellationToken cancellationToken)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var accounts = await client.Queryable<GoogleAccount>().ToListAsync(cancellationToken);

        foreach (var account in accounts)
        {
            if (!enabled)
            {
                account.DisabledByFeatureToggle = account.IsEnabled;
                account.IsEnabled = false;
                await client.Updateable(account)
                    .UpdateColumns(x => new { x.IsEnabled, x.DisabledByFeatureToggle })
                    .ExecuteCommandAsync(cancellationToken);
                await SetLinkedSiteEnabledAsync(client, account.LinkedSiteId, false, cancellationToken);
            }
            else if (account.DisabledByFeatureToggle)
            {
                account.IsEnabled = true;
                account.DisabledByFeatureToggle = false;
                await client.Updateable(account)
                    .UpdateColumns(x => new { x.IsEnabled, x.DisabledByFeatureToggle })
                    .ExecuteCommandAsync(cancellationToken);
                await SetLinkedSiteEnabledAsync(client, account.LinkedSiteId, true, cancellationToken);
            }
        }

        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateGoogleAccounts();
    }

    private async Task<GoogleQuotaInfo> QueryAsync(GoogleAccount account, bool forceRefresh, CancellationToken cancellationToken)
    {
        var cacheKey = "google-quota-" + account.Id.ToString("N");
        if (!forceRefresh && _resultCache.TryGetValue(cacheKey, out GoogleQuotaInfo? cached) && cached != null)
        {
            return cached;
        }

        using (await Locks.WaitAsync(account.Id.ToString("N"), cancellationToken))
        {
            if (!forceRefresh && _resultCache.TryGetValue(cacheKey, out cached) && cached != null)
            {
                return cached;
            }

            var info = await QueryUpstreamAsync(account, cancellationToken, allowTokenRefresh: true);

            if (info.Success)
            {
                try
                {
                    using var writeClient = _dbContext.Client.CopyNew();
                    writeClient.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
                    account.LastQuotaRawJson = info.RawJson;
                    account.LastQuotaCheckedAt = DateTimeOffset.UtcNow;
                    await writeClient.Updateable(account)
                        .UpdateColumns(x => new { x.LastQuotaRawJson, x.LastQuotaCheckedAt })
                        .ExecuteCommandAsync(cancellationToken);
                    _metadataCache.InvalidateGoogleAccounts();

                    var runtime = await _metadataCache.GetRuntimeSettingsAsync(cancellationToken);
                    if (account.IsEnabled)
                    {
                        var maxPercent = GetMaxUsedPercent(info);
                        var threshold = (double)runtime.OAuthAutoDisableThresholdPercent;
                        if (maxPercent.HasValue && maxPercent.Value >= threshold)
                        {
                            await DisableAccountAsync(account, cancellationToken,
                                $"额度使用 {maxPercent.Value:F1}% 达到全局阈值 {threshold}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Persist google quota result failed for account {Id}", account.Id);
                }
            }

            _resultCache.Set(cacheKey, info, ResultCacheTtl);
            return info;
        }
    }

    private async Task<GoogleQuotaInfo> QueryUpstreamAsync(GoogleAccount account, CancellationToken ct, bool allowTokenRefresh)
    {
        if (string.IsNullOrEmpty(account.AccessToken))
        {
            return new GoogleQuotaInfo { Success = false, Error = "账号无 access_token" };
        }

        // GeminiCLI：上游无额度接口（对齐 gcli2api——仅 tier 展示），返回带 tier 的空窗口快照。
        if (!string.Equals(account.AccountKind, GoogleAccountKinds.Antigravity, StringComparison.OrdinalIgnoreCase))
        {
            return new GoogleQuotaInfo
            {
                Success = true,
                PlanType = account.SubscriptionTier,
                RawJson = JsonSerializer.Serialize(new { tier = account.SubscriptionTier ?? "unknown" }),
            };
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{GoogleAccountKinds.GetBaseUrl(account.AccountKind)}/v1internal:fetchAvailableModels")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
            request.Headers.TryAddWithoutValidation("User-Agent", GoogleAccountKinds.AntigravityUserAgent);
            request.Headers.TryAddWithoutValidation("requestId", $"req-{Guid.NewGuid():N}");
            request.Headers.TryAddWithoutValidation("requestType", "agent");

            using var response = await _httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && allowTokenRefresh)
                {
                    var refreshed = await _credentialRefreshService.RefreshAsync(account.LinkedSiteId, account.AccessToken, ct);
                    if (!string.IsNullOrWhiteSpace(refreshed))
                    {
                        account.AccessToken = refreshed;
                        return await QueryUpstreamAsync(account, ct, allowTokenRefresh: false);
                    }
                }

                return new GoogleQuotaInfo { Success = false, Error = $"上游返回 {(int)response.StatusCode}", RawJson = body };
            }

            var windows = GoogleQuotaParser.Parse(body);
            return new GoogleQuotaInfo
            {
                Success = true,
                PlanType = account.SubscriptionTier,
                RawJson = body,
                Windows = (windows ?? []).Select(w => new GoogleQuotaWindow
                {
                    Id = w.Id,
                    Label = w.Id,
                    UsedPercent = w.UsedPercent,
                    ResetLabel = w.ResetLabel,
                    ResetAtUtc = w.ResetAtUtc,
                }).ToList(),
            };
        }
        catch (Exception ex)
        {
            return new GoogleQuotaInfo { Success = false, Error = ex.Message };
        }
    }

    /// <summary>自动禁用判定：取所有模型窗口的最大已用百分比。</summary>
    private static double? GetMaxUsedPercent(GoogleQuotaInfo info)
        => info.Windows.Count == 0 ? null : info.Windows.Max(w => w.UsedPercent);

    private static AccountQuotaTarget ToQuotaTarget(GoogleAccount account) => new()
    {
        ProviderKey = "google",
        AccountId = account.Id,
        DisplayName = account.DisplayName,
        LinkedSiteId = account.LinkedSiteId,
        IsEnabled = account.IsEnabled,
        IsQuotaCooling = account.IsQuotaCooling,
        DisabledByFeatureToggle = account.DisabledByFeatureToggle,
        ManuallyDisabled = account.ManuallyDisabled,
        TokenExpiresAt = account.TokenExpiresAt,
        LastQuotaCheckedAt = account.LastQuotaCheckedAt,
        LastQuotaRawJson = account.LastQuotaRawJson,
    };

    private static AccountQuotaSnapshot ToQuotaSnapshot(GoogleQuotaInfo info) => new()
    {
        Success = info.Success,
        Error = info.Error,
        PlanType = info.PlanType,
        RawJson = info.RawJson,
        CheckedAt = info.CheckedAt,
        Windows = info.Windows.Select(ToQuotaWindow).ToList(),
    };

    private static AccountQuotaWindow ToQuotaWindow(GoogleQuotaWindow window) => new()
    {
        Id = window.Id,
        Label = window.Label,
        UsedPercent = window.UsedPercent,
        ResetLabel = window.ResetLabel,
        ResetAtUtc = window.ResetAtUtc,
    };

    private static AccountQuotaWindow ToQuotaWindow(GoogleQuotaParser.Window window) => new()
    {
        Id = window.Id,
        Label = window.Label,
        UsedPercent = window.UsedPercent,
        ResetLabel = window.ResetLabel,
        ResetAtUtc = window.ResetAtUtc,
    };

    private static async Task SetLinkedSiteEnabledAsync(
        SqlSugar.ISqlSugarClient client,
        Guid linkedSiteId,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var site = await client.Queryable<Domain.Sites.Site>().InSingleAsync(linkedSiteId);
        if (site is null || site.IsEnabled == enabled) return;

        site.IsEnabled = enabled;
        await client.Updateable(site)
            .UpdateColumns(x => new { x.IsEnabled })
            .ExecuteCommandAsync(cancellationToken);
    }

    private async Task DisableAccountAsync(GoogleAccount account, CancellationToken ct, string reason)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        account.IsEnabled = false;
        await client.Updateable(account)
            .UpdateColumns(x => new { x.IsEnabled })
            .ExecuteCommandAsync(ct);

        var site = await client.Queryable<Domain.Sites.Site>().InSingleAsync(account.LinkedSiteId);
        if (site != null && site.IsEnabled)
        {
            site.IsEnabled = false;
            await client.Updateable(site).ExecuteCommandAsync(ct);
        }

        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateGoogleAccounts();
        _logger.LogWarning("Google account {Id} auto-disabled: {Reason}", account.Id, reason);
    }
}
