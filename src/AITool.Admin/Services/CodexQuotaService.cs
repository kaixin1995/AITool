using AITool.Infrastructure.Proxy;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using AITool.Application.Codex;
using AITool.Domain.Codex;
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
public sealed class CodexQuotaService : ICodexQuotaService
{
    // wham/usage 端点（codex-patrol 同款）
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const string UserAgent = "codex_cli_rs/0.133.0 (Mac OS 26.3.1; arm64) iTerm.app/3.6.9";

    /// <summary>结果缓存 TTL（防抖）。</summary>
    private static readonly TimeSpan ResultCacheTtl = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly AdminCacheInvalidationService _adminCacheInvalidation;
    private readonly IMemoryCache _resultCache;
    private readonly ILogger<CodexQuotaService> _logger;

    /// <summary>single-flight：同 accountId 并发只一次真实请求。</summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public CodexQuotaService(
        HttpClient httpClient,
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        AdminCacheInvalidationService adminCacheInvalidation,
        IMemoryCache resultCache,
        ILogger<CodexQuotaService> logger)
    {
        _httpClient = httpClient;
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _adminCacheInvalidation = adminCacheInvalidation;
        _resultCache = resultCache;
        _logger = logger;
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

        // single-flight 锁内做上游 HTTP + DB 写；推送 Core 的 HTTP 挪到锁外，避免持锁等 Core 响应。
        var (info, siteDisabled) = await QueryUnderSingleFlightAsync(account, forceRefresh, cacheKey, cancellationToken);

        // 锁外：仅当账号被自动禁用且 Site 状态变更时，才推送 Core。
        if (siteDisabled)
        {
            try
            {
                await _adminCacheInvalidation.InvalidateRouteTargetsAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "推送 Core 路由缓存失效失败（账号 {Id} 自动禁用后）", account.Id);
            }
        }

        return info;
    }

    /// <summary>
    /// single-flight 锁内的核心查询逻辑。返回额度结果 + 是否触发了 Site 禁用（需锁外推送 Core）。
    /// 上游 HTTP 必须在锁内（single-flight 的目的就是防重复打上游）；DB 写也在锁内保护并发。
    /// </summary>
    private async Task<(CodexQuotaInfo Info, bool SiteDisabled)> QueryUnderSingleFlightAsync(
        CodexAccount account, bool forceRefresh, string cacheKey, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // 二次检查缓存（等待期间可能已被并发填充）
            if (!forceRefresh && _resultCache.TryGetValue(cacheKey, out CodexQuotaInfo? cached) && cached != null)
            {
                return (cached, false);
            }

            var info = await QueryUpstreamAsync(account, cancellationToken);

            // 持久化（更新 LastQuotaRawJson/LastQuotaCheckedAt；自动禁用判定仍用百分比阈值）
            bool siteDisabled = false;
            try
            {
                account.LastQuotaRawJson = info.RawJson;
                account.LastQuotaCheckedAt = DateTimeOffset.UtcNow;
                await _dbContext.UpdateAsync(account, cancellationToken);
                // 额度快照已变更，失效账号列表缓存，避免巡检读到旧 LastQuotaCheckedAt 导致缓存策略误判。
                _metadataCache.InvalidateCodexAccounts();

                // 自动禁用判定：任一窗口使用百分比达到全局阈值时禁用（阈值用百分比 0-100 表达）
                var runtime = await _metadataCache.GetRuntimeSettingsAsync(cancellationToken);
                if (info.Success && account.IsEnabled)
                {
                    var maxPercent = GetMaxUsedPercent(info);
                    var threshold = (double)runtime.CodexAutoDisableThresholdPercent;
                    if (maxPercent.HasValue && maxPercent.Value >= threshold)
                    {
                        siteDisabled = await DisableAccountAsync(account, cancellationToken,
                            $"额度使用 {maxPercent.Value:F1}% 达到全局阈值 {threshold}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Persist codex quota result failed for account {Id}", account.Id);
            }

            // 写缓存（无论成功失败都缓存 30s，避免失败风暴）
            _resultCache.Set(cacheKey, info, ResultCacheTtl);
            return (info, siteDisabled);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<CodexQuotaInfo> QueryUpstreamAsync(CodexAccount account, CancellationToken ct)
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
    /// 规则：优先看 5 小时窗口；没有 5 小时才看周窗口。只看一个，不叠加。
    /// </summary>
    private static double? GetMaxUsedPercent(CodexQuotaInfo info)
    {
        var fiveHour = info.Windows.FirstOrDefault(w => w.Id == "five-hour")?.UsedPercent;
        if (fiveHour.HasValue) return fiveHour;
        return info.Windows.FirstOrDefault(w => w.Id == "weekly")?.UsedPercent;
    }

    /// <summary>
    /// 禁用账号 + 关联隐藏 Site。仅做 DB 写，返回是否有 Site 状态变更。
    /// 缓存失效（含 HTTP 推送 Core）由调用方在 single-flight 锁外统一处理，避免持锁等 HTTP。
    /// </summary>
    /// <returns>true 表示 Site.IsEnabled 被改（需要推送 Core）；false 表示 Site 本就禁用。</returns>
    private async Task<bool> DisableAccountAsync(CodexAccount account, CancellationToken ct, string reason)
    {
        account.IsEnabled = false;
        await _dbContext.UpdateAsync(account, ct);

        var site = await _dbContext.Sites.InSingleAsync(account.LinkedSiteId);
        if (site != null && site.IsEnabled)
        {
            site.IsEnabled = false;
            await _dbContext.UpdateAsync(site, ct);
        }

        // CodexAccounts 缓存只在 Admin 端（Core 不缓存账号实体），本地内存失效即可，无 HTTP。
        _metadataCache.InvalidateCodexAccounts();
        _logger.LogWarning("Codex account {Id} auto-disabled: {Reason}", account.Id, reason);
        return site != null && !site.IsEnabled;
    }
}
