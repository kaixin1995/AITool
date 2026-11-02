using AITool.Application.Networking;
using System.Net.Http.Headers;
using AITool.Application.Accounts;
using AITool.Application.Kimi;
using AITool.Domain.Kimi;
using AITool.Infrastructure.Common;
using AITool.Infrastructure.Kimi;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;

namespace AITool.Web.Services;

/// <summary>
/// Kimi 账号额度主动查询实现（IAccountQuotaProvider，ProviderKey="kimi"）。
/// <para>
/// 数据源为 GET {ApiBaseUrl}/v1/usages（Bearer access_token，逆向自 Kimi Code CLI /usage）：
/// 顶层 usage 为周额度，limits[] 为滚动限流窗口（如 300 分钟 = 5 小时）。
/// 任一窗口用量达到全局阈值时自动禁用账号与关联站点（与 Codex/Google 巡检口径一致）。
/// </para>
/// </summary>
public sealed class KimiQuotaService : IAccountQuotaProvider
{
    private static readonly TimeSpan ResultCacheTtl = TimeSpan.FromSeconds(30);

    private readonly NetworkBoundOAuthClientFactory? _networkFactory;
    private readonly HttpClient _httpClient;
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly IMemoryCache _resultCache;
    private readonly KimiCredentialRefreshService _credentialRefreshService;
    private readonly ILogger<KimiQuotaService> _logger;
    private readonly IQuotaRecheckScheduleStore? _scheduleStore;

    /// <summary>single-flight：同 accountId 并发只一次真实请求。</summary>
    private static readonly KeyedAsyncLock Locks = new();

    public KimiQuotaService(
        HttpClient httpClient,
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        IMemoryCache resultCache,
        KimiCredentialRefreshService credentialRefreshService,
        ILogger<KimiQuotaService> logger,
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

    public string ProviderKey => "kimi";

    public async Task<IReadOnlyList<AccountQuotaTarget>> GetAccountsAsync(CancellationToken cancellationToken)
    {
        // 与 Google 提供程序口径一致：总开关禁用的账号不参与巡检（避免白发真实查询），
        // 功能重开时由 ApplyFeatureToggleAsync 统一恢复。
        var accounts = await _dbContext.KimiAccounts
            .Where(a => !a.IsDeleted && !a.DisabledByFeatureToggle)
            .OrderBy(a => a.LastQuotaCheckedAt)
            .ToListAsync(cancellationToken);

        return accounts.Select(ToQuotaTarget).ToList();
    }

    public async Task<IReadOnlyList<AccountQuotaTarget>> FindByLinkedSiteIdsAsync(
        IReadOnlyList<Guid> siteIds,
        CancellationToken cancellationToken)
    {
        var accounts = await _dbContext.KimiAccounts
            .Where(a => !a.IsDeleted && siteIds.Contains(a.LinkedSiteId))
            .ToListAsync(cancellationToken);
        return accounts.Select(ToQuotaTarget).ToList();
    }

    public AccountQuotaSnapshot? ParseCachedQuota(string rawJson)
    {
        var windows = KimiQuotaParser.Parse(rawJson);
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

    public async Task<AccountQuotaSnapshot> QueryAsync(
        AccountQuotaTarget account,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var current = (await _dbContext.KimiAccounts
            .Where(a => a.Id == account.AccountId && !a.IsDeleted)
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
                _scheduleStore, _logger, cancellationToken);
            _metadataCache.InvalidateRouteTargets();
            _metadataCache.InvalidateKimiAccounts();
            return;
        }

        if (enabled)
        {
            await QuotaAccountStateWriter.EnableAsync(_dbContext, ProviderKey, account.AccountId,
                string.Equals(reason, "feature-toggle-on", StringComparison.OrdinalIgnoreCase), false, cancellationToken);
            _metadataCache.InvalidateRouteTargets();
            _metadataCache.InvalidateKimiAccounts();
            return;
        }

        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var current = (await client.Queryable<KimiAccount>()
            .Where(a => a.Id == account.AccountId)
            .ToListAsync(cancellationToken))
            .FirstOrDefault();
        if (current is null || current.IsDeleted) return;

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
        _metadataCache.InvalidateKimiAccounts();
    }

    public async Task ApplyFeatureToggleAsync(bool enabled, CancellationToken cancellationToken)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var accounts = await client.Queryable<KimiAccount>()
            .Where(a => !a.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var account in accounts)
        {
            if (enabled)
            {
                await QuotaAccountStateWriter.EnableAsync(_dbContext, ProviderKey, account.Id,
                    true, true, cancellationToken);
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
        _metadataCache.InvalidateKimiAccounts();
    }

    /// <summary>手动「刷新额度」入口：强制实时查询。</summary>
    public async Task<AccountQuotaSnapshot> ForceRefreshAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var target = (await GetAccountsAsync(cancellationToken)).FirstOrDefault(a => a.AccountId == accountId);
        if (target is null)
        {
            return new AccountQuotaSnapshot { Success = false, Error = "账号不存在", CheckedAt = DateTimeOffset.UtcNow };
        }

        return await QueryAsync(target, forceRefresh: true, cancellationToken);
    }

    private async Task<QuotaQueryResult> QueryAsync(KimiAccount account, bool forceRefresh, CancellationToken cancellationToken)
    {
        var cacheKey = "kimi-quota-" + account.Id.ToString("N");
        if (!forceRefresh && _resultCache.TryGetValue(cacheKey, out QuotaQueryResult? cached) && cached != null)
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
                    _metadataCache.InvalidateKimiAccounts();

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
                    _logger.LogWarning(ex, "Persist kimi quota result failed for account {Id}", account.Id);
                }
            }

            if (info.UpstreamUnauthorized) _resultCache.Remove(cacheKey);
            else _resultCache.Set(cacheKey, info, ResultCacheTtl);
            return info;
        }
    }

    private async Task<QuotaQueryResult> QueryUpstreamAsync(KimiAccount account, CancellationToken ct, bool allowTokenRefresh)
    {
        if (string.IsNullOrWhiteSpace(account.AccessToken))
        {
            return new QuotaQueryResult { Success = false, Error = "账号无 access_token" };
        }

        try
        {
            var credential = new ForwardOAuthCredentialSnapshot(ProviderKey, account.Id, account.LinkedSiteId,
                account.AccessToken, account.RefreshToken, account.LastRefreshAt);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{KimiConstants.ApiBaseUrl}/v1/usages");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken.Trim());
            // 对齐官方 Kimi CLI 抓包指纹（KimiCLI UA + kimi_cli 平台标识）。
            request.Headers.TryAddWithoutValidation("User-Agent", KimiConstants.ClientUserAgent);
            request.Headers.TryAddWithoutValidation("X-Msh-Platform", "kimi_cli");
            if (!string.IsNullOrWhiteSpace(account.DeviceId))
            {
                request.Headers.TryAddWithoutValidation("X-Msh-Device-Id", account.DeviceId.Trim());
            }

            using var network = _networkFactory?.CreateForAccount(new AccountIdentity("kimi", account.Id), "quota");
            if (network is not null) network.HttpClient.Timeout = _httpClient.Timeout;
            using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_httpClient.Timeout != Timeout.InfiniteTimeSpan) requestBudget.CancelAfter(_httpClient.Timeout);
            using var response = await (network?.HttpClient ?? _httpClient).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestBudget.Token);
            string body;
            try { body = await response.Content.ReadAsStringAsync(requestBudget.Token); }
            catch (Exception ex) when (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && ex is not OperationCanceledException && !requestBudget.IsCancellationRequested) { body = string.Empty; }

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && allowTokenRefresh && !string.IsNullOrWhiteSpace(account.RefreshToken))
                {
                    string? refreshed;
                    try { refreshed = await _credentialRefreshService.RefreshForAccountAsync(account.Id, account.AccessToken, ct); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        await new ForwardOAuthInvalidationService(_dbContext, _metadataCache).InvalidateAsync(credential.AccessToken!, credential, ct);
                        return new QuotaQueryResult { Success = false, UpstreamUnauthorized = true, Error = "上游 HTTP 401，凭证刷新失败：" + ex.Message };
                    }
                    if (!string.IsNullOrWhiteSpace(refreshed))
                    {
                        var refreshedAccount = await _dbContext.KimiAccounts.InSingleAsync(account.Id);
                        if (refreshedAccount is not null && refreshedAccount.AccessToken == refreshed)
                            return await QueryUpstreamAsync(refreshedAccount, ct, allowTokenRefresh: false);
                        return new QuotaQueryResult { Success = false, UpstreamUnauthorized = true, Error = "上游 HTTP 401，账号凭证已更新，请重试" };
                    }
                }

                var unauthorized = response.StatusCode == System.Net.HttpStatusCode.Unauthorized;
                if (unauthorized)
                    await new ForwardOAuthInvalidationService(_dbContext, _metadataCache).InvalidateAsync(credential.AccessToken!, credential, ct);
                return new QuotaQueryResult { Success = false, UpstreamUnauthorized = unauthorized, Error = $"上游返回 {(int)response.StatusCode}", RawJson = body };
            }

            var windows = KimiQuotaParser.Parse(body);
            if (windows is null)
            {
                return new QuotaQueryResult { Success = false, Error = "响应中没有可用额度数据", RawJson = body };
            }

            return new QuotaQueryResult
            {
                Success = true,
                PlanType = "Kimi Code",
                RawJson = body,
                Windows = windows.Select(ToQuotaWindow).ToList(),
            };
        }
        catch (Exception ex)
        {
            return new QuotaQueryResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>自动禁用判定：取所有窗口的最大已用百分比。</summary>
    private static double? GetMaxUsedPercent(QuotaQueryResult info)
        => info.Windows.Count == 0 ? null : info.Windows.Max(w => w.UsedPercent);

    private sealed record QuotaQueryResult
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public bool UpstreamUnauthorized { get; init; }
        public string? PlanType { get; init; }
        public string RawJson { get; init; } = string.Empty;
        public DateTimeOffset CheckedAt { get; init; } = DateTimeOffset.UtcNow;
        public IReadOnlyList<AccountQuotaWindow> Windows { get; init; } = [];
    }

    private static AccountQuotaTarget ToQuotaTarget(KimiAccount account) => new()
    {
        ProviderKey = "kimi",
        AccountId = account.Id,
        DisplayName = account.DisplayName,
        LinkedSiteId = account.LinkedSiteId,
        IsEnabled = account.IsEnabled,
        IsQuotaCooling = false,
        DisabledByFeatureToggle = account.DisabledByFeatureToggle,
        ManuallyDisabled = account.ManuallyDisabled,
        DisabledByUpstream = account.RequiresReauth,
        TokenExpiresAt = account.TokenExpiresAt,
        LastQuotaCheckedAt = account.LastQuotaCheckedAt,
        LastQuotaRawJson = account.LastQuotaRawJson,
    };

    private static AccountQuotaSnapshot ToQuotaSnapshot(QuotaQueryResult info) => new()
    {
        Success = info.Success,
        Error = info.Error,
        UpstreamUnauthorized = info.UpstreamUnauthorized,
        PlanType = info.PlanType,
        RawJson = info.RawJson,
        CheckedAt = info.CheckedAt,
        Windows = info.Windows,
    };

    private static AccountQuotaWindow ToQuotaWindow(KimiQuotaParser.Window window) => new()
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
        await client.Updateable(site).UpdateColumns(x => new { x.IsEnabled }).ExecuteCommandAsync(cancellationToken);
    }

    private async Task DisableAccountAsync(KimiAccount account, CancellationToken ct, string reason)
    {
        await SetEnabledAsync(ToQuotaTarget(account), false, "quota-threshold", ct);
        _logger.LogWarning("Kimi account {Id} auto-disabled: {Reason}", account.Id, reason);
    }
}
