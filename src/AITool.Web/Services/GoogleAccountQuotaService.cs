using AITool.Application.Networking;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AITool.Application.Accounts;
using AITool.Application.Google;
using AITool.Domain.Google;
using AITool.Domain.Operations;
using SqlSugar;
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
/// </para>
/// </summary>
public sealed class GoogleAccountQuotaService : IAccountQuotaProvider
{
    private static readonly TimeSpan ResultCacheTtl = TimeSpan.FromSeconds(30);

    private readonly NetworkBoundOAuthClientFactory? _networkFactory;
    private readonly HttpClient _httpClient;
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly IMemoryCache _resultCache;
    private readonly GoogleCredentialRefreshService _credentialRefreshService;
    private readonly ILogger<GoogleAccountQuotaService> _logger;
    private readonly IQuotaRecheckScheduleStore? _scheduleStore;

    /// <summary>single-flight：同 accountId 并发只一次真实请求。KeyedAsyncLock 会在账号不再使用时回收锁条目。</summary>
    private static readonly KeyedAsyncLock Locks = new();

    public GoogleAccountQuotaService(
        HttpClient httpClient,
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        IMemoryCache resultCache,
        GoogleCredentialRefreshService credentialRefreshService,
        ILogger<GoogleAccountQuotaService> logger,
        IQuotaRecheckScheduleStore? scheduleStore = null,
        NetworkBoundOAuthClientFactory? networkFactory = null)
    {
        _networkFactory = networkFactory;
        _httpClient = httpClient;
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _resultCache = resultCache;
        _credentialRefreshService = credentialRefreshService;
        _logger = logger;
        _scheduleStore = scheduleStore;
    }

    public string ProviderKey => "google";

    /// <summary>
    /// 自动禁用/恢复只看 Gemini 桶：Gemini 额度耗尽即禁用整个账号；
    /// Claude/GPT-OSS 桶耗尽不禁用（Antigravity 真实额度只有这两桶，其余为上游噪声）。
    /// </summary>
    public double? SelectDisablePercent(AccountQuotaSnapshot snapshot)
    {
        var gemini = snapshot.Windows.FirstOrDefault(window => GoogleQuotaParser.IsGeminiBucket(window.Id));
        return gemini?.UsedPercent;
    }

    /// <summary>额度查询结果（内部口径）。</summary>
    private sealed record GoogleQuotaInfo
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public bool UpstreamUnauthorized { get; init; }
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
            .Where(a => !a.DisabledByFeatureToggle && !a.DisabledByUpstream)
            .OrderBy(a => a.LastQuotaCheckedAt)
            .ToListAsync(cancellationToken);

        return accounts.Select(ToQuotaTarget).ToList();
    }

    public async Task<AccountQuotaTarget?> GetAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await _dbContext.GoogleAccounts
            .Where(a => a.Id == accountId && !a.DisabledByFeatureToggle && !a.DisabledByUpstream).FirstAsync(cancellationToken);
        return account is null ? null : ToQuotaTarget(account);
    }

    public async Task<IReadOnlyList<AccountQuotaTarget>> FindByLinkedSiteIdsAsync(
        IReadOnlyList<Guid> siteIds,
        CancellationToken cancellationToken)
    {
        var accounts = await _dbContext.GoogleAccounts
            .Where(a => siteIds.Contains(a.LinkedSiteId))
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
        if ((enabled && string.Equals(reason, "quota-recovered", StringComparison.OrdinalIgnoreCase)) ||
            (!enabled && string.Equals(reason, "quota-threshold", StringComparison.OrdinalIgnoreCase)))
        {
            await QuotaAccountStateWriter.ChangeAsync(_dbContext, ProviderKey, account.AccountId, enabled,
                _scheduleStore, _logger, cancellationToken, account.QuotaRecheckLeaseToken);
            _metadataCache.InvalidateRouteTargets();
            _metadataCache.InvalidateGoogleAccounts();
            return;
        }

        if (enabled)
        {
            await QuotaAccountStateWriter.EnableAsync(_dbContext, ProviderKey, account.AccountId,
                string.Equals(reason, "feature-toggle-on", StringComparison.OrdinalIgnoreCase), false, cancellationToken);
            _metadataCache.InvalidateRouteTargets();
            _metadataCache.InvalidateGoogleAccounts();
            return;
        }

        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var current = (await client.Queryable<GoogleAccount>()
            .Where(a => a.Id == account.AccountId)
            .ToListAsync(cancellationToken))
            .FirstOrDefault();
        if (current is null) return;

        if (enabled)
        {
            if (current.RequiresReauth) return;
            current.IsEnabled = true;
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
            .UpdateColumns(x => new { x.IsEnabled, x.DisabledByFeatureToggle })
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
            if (enabled)
            {
                await QuotaAccountStateWriter.EnableAsync(_dbContext, ProviderKey, account.Id,
                    true, true, cancellationToken);
                await client.Updateable<GoogleAccount>()
                    .SetColumns(x => new GoogleAccount { DisabledByFeatureToggle = false })
                    .Where(x => x.Id == account.Id && x.RequiresReauth && x.DisabledByFeatureToggle)
                    .Where(x => SqlFunc.Subqueryable<SystemRuntimeSettings>().Where(s => s.Id == 1 && s.OAuthFeaturesEnabled).Any())
                    .ExecuteCommandAsync(cancellationToken);
                continue;
            }

            if (!enabled)
            {
                account.DisabledByFeatureToggle = account.IsEnabled;
                account.IsEnabled = false;
                await client.Updateable(account)
                    .UpdateColumns(x => new { x.IsEnabled, x.DisabledByFeatureToggle })
                    .ExecuteCommandAsync(cancellationToken);
                await SetLinkedSiteEnabledAsync(client, account.LinkedSiteId, false, cancellationToken);
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
                        var geminiPercent = GetGeminiUsedPercent(info);
                        var threshold = (double)runtime.OAuthAutoDisableThresholdPercent;
                        if (geminiPercent.HasValue && geminiPercent.Value >= threshold)
                        {
                            await DisableAccountAsync(account, cancellationToken,
                                $"Gemini 额度使用 {geminiPercent.Value:F1}% 达到全局阈值 {threshold}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Persist google quota result failed for account {Id}", account.Id);
                }
            }

            if (info.UpstreamUnauthorized) _resultCache.Remove(cacheKey);
            else _resultCache.Set(cacheKey, info, ResultCacheTtl);
            return info;
        }
    }

    private async Task<GoogleQuotaInfo> QueryUpstreamAsync(GoogleAccount account, CancellationToken ct, bool allowTokenRefresh)
    {
        if (string.IsNullOrEmpty(account.AccessToken))
        {
            return new GoogleQuotaInfo { Success = false, Error = "账号无 access_token" };
        }

        try
        {
            var credential = new ForwardOAuthCredentialSnapshot(ProviderKey, account.Id, account.LinkedSiteId,
                account.AccessToken, account.RefreshToken, account.LastRefreshAt);
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"{GoogleAccountKinds.GetBaseUrl(account.AccountKind)}/v1internal:fetchAvailableModels")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
            request.Headers.TryAddWithoutValidation("User-Agent", GoogleAccountKinds.GetUserAgent(account.AccountKind));
            request.Headers.TryAddWithoutValidation("requestId", $"req-{Guid.NewGuid():N}");
            request.Headers.TryAddWithoutValidation("requestType", "agent");

            using var network = _networkFactory?.CreateForAccount(new AccountIdentity("google", account.Id), "quota");
            if (network is not null) network.HttpClient.Timeout = _httpClient.Timeout;
            using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_httpClient.Timeout != Timeout.InfiniteTimeSpan) requestBudget.CancelAfter(_httpClient.Timeout);
            using var response = await (network?.HttpClient ?? _httpClient).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestBudget.Token);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    await _credentialRefreshService.DisableAsync(account.LinkedSiteId, "quota-403", ct, account.AccessToken);
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && allowTokenRefresh && !string.IsNullOrWhiteSpace(account.RefreshToken))
                {
                    string? refreshed;
                    try { refreshed = await _credentialRefreshService.RefreshForAccountAsync(account.Id, account.AccessToken, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (ex is AITool.Application.OAuth.OAuthRefreshRefusedException)
                            await new ForwardOAuthInvalidationService(_dbContext, _metadataCache).InvalidateAsync(credential.AccessToken!, credential, ct);
                        return new GoogleQuotaInfo { Success = false, UpstreamUnauthorized = true, Error = "上游 HTTP 401，凭证刷新失败，请检查后重试：" + ex.Message };
                    }
                    if (!string.IsNullOrWhiteSpace(refreshed))
                    {
                        var refreshedAccount = await _dbContext.GoogleAccounts.InSingleAsync(account.Id);
                        if (refreshedAccount is not null && refreshedAccount.AccessToken == refreshed)
                            return await QueryUpstreamAsync(refreshedAccount, ct, allowTokenRefresh: false);
                        return new GoogleQuotaInfo { Success = false, UpstreamUnauthorized = true, Error = "上游 HTTP 401，账号凭证已更新，请重试" };
                    }
                    return new GoogleQuotaInfo { Success = false, UpstreamUnauthorized = true, Error = "凭证刷新未完成，请稍后重试" };
                }

                var unauthorized = response.StatusCode == System.Net.HttpStatusCode.Unauthorized;
                if (unauthorized)
                    await new ForwardOAuthInvalidationService(_dbContext, _metadataCache).InvalidateAsync(credential.AccessToken!, credential, ct);
                var errorBody = unauthorized ? string.Empty : await response.Content.ReadAsStringAsync(requestBudget.Token);
                return new GoogleQuotaInfo { Success = false, UpstreamUnauthorized = unauthorized, Error = $"上游返回 {(int)response.StatusCode}", RawJson = errorBody };
            }

            var body = await response.Content.ReadAsStringAsync(requestBudget.Token);
            var windows = GoogleQuotaParser.Parse(body);
            return new GoogleQuotaInfo
            {
                Success = true,
                PlanType = account.SubscriptionTier,
                RawJson = body,
                Windows = (windows ?? []).Select(w => new GoogleQuotaWindow
                {
                    Id = w.Id,
                    Label = w.Label,
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

    /// <summary>自动禁用判定：只看 Gemini 桶的已用百分比（Claude/GPT-OSS 桶耗尽不禁用）。</summary>
    private static double? GetGeminiUsedPercent(GoogleQuotaInfo info)
        => info.Windows.FirstOrDefault(window => GoogleQuotaParser.IsGeminiBucket(window.Id))?.UsedPercent;

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
        DisabledByUpstream = account.DisabledByUpstream || account.RequiresReauth,
        TokenExpiresAt = account.TokenExpiresAt,
        LastQuotaCheckedAt = account.LastQuotaCheckedAt,
        LastQuotaRawJson = account.LastQuotaRawJson,
    };

    private static AccountQuotaSnapshot ToQuotaSnapshot(GoogleQuotaInfo info) => new()
    {
        Success = info.Success,
        Error = info.Error,
        UpstreamUnauthorized = info.UpstreamUnauthorized,
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
        await SetEnabledAsync(ToQuotaTarget(account), false, "quota-threshold", ct);
        _logger.LogWarning("Google account {Id} auto-disabled: {Reason}", account.Id, reason);
    }

}
