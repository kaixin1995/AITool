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
    private readonly CodexCredentialRefreshService _credentialRefreshService;
    private readonly ILogger<CodexQuotaService> _logger;

    /// <summary>single-flight：同 accountId 并发只一次真实请求。必须 static：本服务经 typed HttpClient 注册为 transient，实例级字典无法跨实例合并并发。</summary>
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

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
        var gate = Locks.GetOrAdd(account.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
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
            }

            // 写缓存（无论成功失败都缓存 30s，避免失败风暴）
            _resultCache.Set(cacheKey, info, ResultCacheTtl);
            return info;
        }
        finally
        {
            gate.Release();
            // 清理无竞争的 entry，避免账号删除后 SemaphoreSlim 泄漏。
            // 仅当此刻空闲（无人等待）才移除；不 Dispose——并发等待方仍持有引用，释放已 Dispose 的信号量会抛 ObjectDisposedException。
            if (gate.CurrentCount == 1)
            {
                Locks.TryRemove(account.Id, out _);
            }
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
    /// 规则：优先看 5 小时窗口；没有 5 小时才看周窗口。只看一个，不叠加。
    /// </summary>
    private static double? GetMaxUsedPercent(CodexQuotaInfo info)
    {
        var fiveHour = info.Windows.FirstOrDefault(w => w.Id == "five-hour")?.UsedPercent;
        if (fiveHour.HasValue) return fiveHour;
        return info.Windows.FirstOrDefault(w => w.Id == "weekly")?.UsedPercent;
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
