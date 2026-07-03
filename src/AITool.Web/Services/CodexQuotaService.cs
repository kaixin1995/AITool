using System.Collections.Concurrent;
using System.Net.Http.Headers;
using AITool.Application.Codex;
using AITool.Domain.Codex;
using AITool.Infrastructure.Codex;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;

namespace AITool.Web.Services;

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
    private readonly IMemoryCache _resultCache;
    private readonly ILogger<CodexQuotaService> _logger;

    /// <summary>single-flight：同 accountId 并发只一次真实请求。</summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public CodexQuotaService(
        HttpClient httpClient,
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        IMemoryCache resultCache,
        ILogger<CodexQuotaService> logger)
    {
        _httpClient = httpClient;
        _dbContext = dbContext;
        _metadataCache = metadataCache;
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

        // single-flight
        var gate = _locks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // 二次检查缓存（等待期间可能已被并发填充）
            if (!forceRefresh && _resultCache.TryGetValue(cacheKey, out cached) && cached != null)
            {
                return cached;
            }

            var info = await QueryUpstreamAsync(account, cancellationToken);

            // 持久化（更新 LastQuotaRawJson/LastQuotaCheckedAt；自动禁用判定仍用百分比阈值）
            try
            {
                account.LastQuotaRawJson = info.RawJson;
                account.LastQuotaCheckedAt = DateTimeOffset.UtcNow;
                await _dbContext.UpdateAsync(account, cancellationToken);

                // 自动禁用判定：任一窗口使用百分比达到全局阈值时禁用（阈值用百分比 0-100 表达）
                var runtime = await _metadataCache.GetRuntimeSettingsAsync(cancellationToken);
                if (info.Success && account.IsEnabled)
                {
                    var maxPercent = GetMaxUsedPercent(info);
                    var threshold = (double)runtime.CodexAutoDisableThresholdPercent;
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

            // 写缓存（无论成功失败都缓存 30s，避免失败风暴）
            _resultCache.Set(cacheKey, info, ResultCacheTtl);
            return info;
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

    private static double? GetMaxUsedPercent(CodexQuotaInfo info)
    {
        var percents = info.Windows
            .Where(w => w.UsedPercent.HasValue)
            .Select(w => w.UsedPercent!.Value)
            .ToList();
        return percents.Count > 0 ? percents.Max() : null;
    }

    private async Task DisableAccountAsync(CodexAccount account, CancellationToken ct, string reason)
    {
        account.IsEnabled = false;
        await _dbContext.UpdateAsync(account, ct);

        var site = await _dbContext.Sites.InSingleAsync(account.LinkedSiteId);
        if (site != null && site.IsEnabled)
        {
            site.IsEnabled = false;
            await _dbContext.UpdateAsync(site, ct);
        }

        _metadataCache.InvalidateRouteTargets();
        _logger.LogWarning("Codex account {Id} auto-disabled: {Reason}", account.Id, reason);
    }
}
