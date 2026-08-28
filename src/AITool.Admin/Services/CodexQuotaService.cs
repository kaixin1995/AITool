using AITool.Infrastructure.Proxy;
using System.Net.Http.Headers;
using AITool.Application.Accounts;
using AITool.Application.Codex;
using AITool.Domain.Codex;
using AITool.Infrastructure.Common;
using AITool.Infrastructure.Codex;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;

namespace AITool.Admin.Services;

/// <summary>
/// Codex 额度主动查询实现（位于 Web 层，因依赖 ProxyRequestMetadataCache）。
/// <para>
/// 端点为 chatgpt.com/backend-api/wham/usage（与 codex-patrol 一致）。
/// AITool 自己持有 access_token（OAuth/导入后存在 CodexAccount），无需经 CPA 中转，直接请求。
/// 上游只返回每个窗口的 used_percent（无 used/limit 绝对值），由 CodexUsageParser 分类为
/// 5 小时窗口(18000s)与周窗口(604800s)。
/// </para>
/// </summary>
public sealed class CodexQuotaService : ICodexQuotaService, IAccountQuotaProvider
{
    // wham/usage 端点（codex-patrol 同款）
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const string UserAgent = "Codex Desktop/0.149.0-alpha.4.3 (Windows 10.0.19045; x86_64) unknown (Codex Desktop; 26.818.61809)";

    /// <summary>结果缓存 TTL（防抖）。</summary>
    private static readonly TimeSpan ResultCacheTtl = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly IMemoryCache _resultCache;
    private readonly CodexCredentialRefreshService _credentialRefreshService;
    private readonly ILogger<CodexQuotaService> _logger;

    /// <summary>single-flight：同 accountId 并发只一次真实请求。KeyedAsyncLock 会在账号不再使用时回收锁条目。</summary>
    private static readonly KeyedAsyncLock Locks = new();

    public CodexQuotaService(
        HttpClient httpClient,
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        IMemoryCache resultCache,
        CodexCredentialRefreshService credentialRefreshService,
        ILogger<CodexQuotaService> logger)
    {
        _httpClient = httpClient;
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _resultCache = resultCache;
        _credentialRefreshService = credentialRefreshService;
        _logger = logger;
    }

    public string ProviderKey => "codex";

    public async Task<IReadOnlyList<AccountQuotaTarget>> GetAccountsAsync(CancellationToken cancellationToken)
    {
        var accounts = await _dbContext.CodexAccounts
            .Where(a => !a.DisabledByFeatureToggle)
            .OrderBy(a => a.LastQuotaCheckedAt)
            .ToListAsync(cancellationToken);

        return accounts.Select(ToQuotaTarget).ToList();
    }

    public AccountQuotaSnapshot? ParseCachedQuota(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return null;

        try
        {
            var (planType, windows) = CodexUsageParser.Parse(rawJson);
            return new AccountQuotaSnapshot
            {
                Success = windows.Count > 0,
                PlanType = planType,
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
        var current = (await _dbContext.CodexAccounts
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

        return ToQuotaSnapshot(await QueryAsync(current, forceRefresh, cancellationToken));
    }

    public async Task SetEnabledAsync(
        AccountQuotaTarget account,
        bool enabled,
        string reason,
        CancellationToken cancellationToken)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var current = (await client.Queryable<CodexAccount>()
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
            .UpdateColumns(x => new { x.IsEnabled, x.ManuallyDisabled, x.DisabledByFeatureToggle })
            .ExecuteCommandAsync(cancellationToken);
        await SetLinkedSiteEnabledAsync(client, current.LinkedSiteId, enabled, cancellationToken);
        _metadataCache.InvalidateRouteTargets();
        _metadataCache.InvalidateCodexAccounts();
    }

    public async Task ApplyFeatureToggleAsync(bool enabled, CancellationToken cancellationToken)
    {
        using var client = _dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        var accounts = await client.Queryable<CodexAccount>().ToListAsync(cancellationToken);

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
        _metadataCache.InvalidateCodexAccounts();
    }

    /// <inheritdoc />
    public async Task<CodexQuotaInfo> QueryAsync(CodexAccount account, bool forceRefresh, CancellationToken cancellationToken)
    {
        var cacheKey = "codex-quota-" + account.Id.ToString("N");

        // 防抖：非强制刷新走缓存
        if (!forceRefresh && _resultCache.TryGetValue(cacheKey, out CodexQuotaInfo? cached) && cached != null)
        {
            return cached;
        }

        // single-flight
        using (await Locks.WaitAsync(account.Id.ToString("N"), cancellationToken))
        {
            // 二次检查缓存（等待期间可能已被并发填充）
            if (!forceRefresh && _resultCache.TryGetValue(cacheKey, out cached) && cached != null)
            {
                return cached;
            }

            var info = await QueryUpstreamAsync(account, cancellationToken);

            // 失败响应不覆盖上一次成功额度，避免“刷新时间已更新但额度窗口为空”。
            if (info.Success)
            {
                // 持久化（用 CopyNew 独立连接写入）
                try
                {
                    using var writeClient = _dbContext.Client.CopyNew();
                    writeClient.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
                    account.LastQuotaRawJson = info.RawJson;
                    account.LastQuotaCheckedAt = DateTimeOffset.UtcNow;
                    // 只更新本次变更的列：account 可能来自 30s 元数据缓存（旧快照），整行回写会把
                    // 后台 token 刷新服务刚写入的 AccessToken/TokenExpiresAt 回滚成旧值。
                    await writeClient.Updateable(account)
                        .UpdateColumns(x => new { x.LastQuotaRawJson, x.LastQuotaCheckedAt })
                        .ExecuteCommandAsync(cancellationToken);
                    // 额度快照已变更，失效账号列表缓存，避免巡检读到旧 LastQuotaCheckedAt 导致缓存策略误判。
                    _metadataCache.InvalidateCodexAccounts();

                    // 自动禁用判定：任一窗口使用百分比达到全局阈值时禁用（阈值用百分比 0-100 表达）
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
                    _logger.LogWarning(ex, "Persist codex quota result failed for account {Id}", account.Id);
                }
            }

            // 写缓存（无论成功失败都缓存 30s，避免失败风暴）
            _resultCache.Set(cacheKey, info, ResultCacheTtl);
            return info;
        }
    }

    private async Task<CodexQuotaInfo> QueryUpstreamAsync(
        CodexAccount account,
        CancellationToken ct,
        bool allowTokenRefresh = true)
    {
        if (string.IsNullOrEmpty(account.AccessToken))
        {
            return new CodexQuotaInfo { Success = false, Error = "账号无 access_token" };
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Originator", "codex_cli_rs");
            if (!string.IsNullOrEmpty(account.AccountId))
            {
                request.Headers.TryAddWithoutValidation("Chatgpt-Account-Id", account.AccountId);
            }

            using var response = await _httpClient.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            var info = new CodexQuotaInfo { RawJson = body, CheckedAt = DateTimeOffset.UtcNow };
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && allowTokenRefresh)
                {
                    var refreshedAccessToken = await _credentialRefreshService.RefreshAsync(
                        account.LinkedSiteId,
                        account.AccessToken,
                        ct);
                    if (!string.IsNullOrWhiteSpace(refreshedAccessToken))
                    {
                        account.AccessToken = refreshedAccessToken;
                        return await QueryUpstreamAsync(account, ct, false);
                    }
                }

                info.Success = false;
                info.Error = $"上游返回 {(int)response.StatusCode}";
                return info;
            }

            // 用 codex-patrol 同款解析器分类窗口
            var (planType, windows) = CodexUsageParser.Parse(body);
            info.PlanType = planType;
            info.Windows = windows.Select(w => new CodexQuotaWindow
            {
                Id = w.Id,
                Label = w.Label,
                UsedPercent = w.UsedPercent,
                ResetLabel = w.ResetLabel,
                ResetAtUtc = w.ResetAtUtc,
                LimitWindowSeconds = w.LimitWindowSeconds,
            }).ToList();
            info.FiveHourUsedPercent = info.Windows.FirstOrDefault(w => w.Id == "five-hour")?.UsedPercent;
            info.WeeklyUsedPercent = info.Windows.FirstOrDefault(w => w.Id == "weekly")?.UsedPercent;
            info.Success = true;
            return info;
        }
        catch (Exception ex)
        {
            return new CodexQuotaInfo { Success = false, Error = ex.Message, CheckedAt = DateTimeOffset.UtcNow };
        }
    }

    /// <summary>
    /// 获取用于自动禁用判定的已使用百分比。
    /// 任一额度窗口达到阈值都应触发禁用，因此取所有窗口中的最大值。
    /// </summary>
    private static double? GetMaxUsedPercent(CodexQuotaInfo info)
        => info.Windows.Count == 0 ? null : info.Windows.Max(w => w.UsedPercent);

    private static AccountQuotaTarget ToQuotaTarget(CodexAccount account) => new()
    {
        ProviderKey = "codex",
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

    private static AccountQuotaSnapshot ToQuotaSnapshot(CodexQuotaInfo info) => new()
    {
        Success = info.Success,
        Error = info.Error,
        PlanType = info.PlanType,
        RawJson = info.RawJson,
        CheckedAt = info.CheckedAt,
        Windows = info.Windows.Select(ToQuotaWindow).ToList(),
    };

    private static AccountQuotaWindow ToQuotaWindow(CodexQuotaWindow window) => new()
    {
        Id = window.Id,
        Label = window.Label,
        UsedPercent = window.UsedPercent,
        ResetLabel = window.ResetLabel,
        ResetAtUtc = window.ResetAtUtc,
        LimitWindowSeconds = window.LimitWindowSeconds,
    };

    private static AccountQuotaWindow ToQuotaWindow(CodexUsageParser.Window window) => new()
    {
        Id = window.Id,
        Label = window.Label,
        UsedPercent = window.UsedPercent,
        ResetLabel = window.ResetLabel,
        ResetAtUtc = window.ResetAtUtc,
        LimitWindowSeconds = window.LimitWindowSeconds,
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

    private async Task DisableAccountAsync(CodexAccount account, CancellationToken ct, string reason)
    {
        // 用 CopyNew 独立连接写入；只更新目标列，避免整行覆盖并发写入的其他字段。
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
        _metadataCache.InvalidateCodexAccounts();
        _logger.LogWarning("Codex account {Id} auto-disabled: {Reason}", account.Id, reason);
    }
}
