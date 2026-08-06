using System.Collections.Concurrent;
using AITool.Application.Codex;
using AITool.Domain.Codex;
using AITool.Infrastructure.Codex;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using Microsoft.Extensions.Hosting;

namespace AITool.Web.Services;

/// <summary>
/// Codex 巡检后台服务：周期检查各账号额度，按使用情况决定缓存 vs 真实刷新，并自动禁用额度耗尽的账号。
/// <para>
/// 缓存策略（移植 codex-patrol QuotaCachePolicy + TTL 兜底）：
/// 被使用的账号真实刷新；未被使用且窗口未重置且未超 maxCacheHours → 用缓存；否则真实刷新。
/// 使用检测：查 ProxyUsageLogs 该账号隐藏 Site 自上次刷新后是否有新记录（AITool 自身是代理，日志已记录）。
/// </para>
/// <para>
/// 自动禁用：周窗口 used% ≥ 账号 AutoDisableThreshold（默认 95）→ 禁用；已禁用且周额度恢复 → 启用。
/// </para>
/// </summary>
public sealed class CodexInspectionService : BackgroundService
{
    /// <summary>轮询基线间隔。巡检周期最小 30 秒，基线 10 秒确保 30 秒设置能及时触发。</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

    /// <summary>内存日志上限。</summary>
    private const int MaxLogs = 200;

    private readonly IServiceProvider _services;
    private readonly ILogger<CodexInspectionService> _logger;
    private readonly IHostEnvironment _environment;
    private readonly SiteUsageTracker _siteUsageTracker;

    // —— 进程内状态（供前端轮询展示）——
    private InspectionRunResult? _lastRun;
    private readonly ConcurrentQueue<InspectionLogEntry> _logs = new();
    private DateTimeOffset _nextScheduledAt;
    private int _running; // 0/1 重入保护

    public CodexInspectionService(
        IServiceProvider services,
        ILogger<CodexInspectionService> logger,
        IHostEnvironment environment,
        SiteUsageTracker siteUsageTracker)
    {
        _services = services;
        _logger = logger;
        _environment = environment;
        _siteUsageTracker = siteUsageTracker;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_environment.IsEnvironment("Testing")) return;

        AddLog("system", "Codex 巡检服务已启动");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Codex inspection loop error");
            }
            await Task.Delay(TickInterval, stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        var quotaService = scope.ServiceProvider.GetRequiredService<ICodexQuotaService>();

        var runtime = await cache.GetRuntimeSettingsAsync(ct);
        // 总开关或巡检开关关闭 → 不调度
        if (!runtime.CodexFeaturesEnabled || !runtime.CodexInspectionEnabled) return;

        var now = DateTimeOffset.UtcNow;
        if (_nextScheduledAt == default) _nextScheduledAt = now;
        if (now < _nextScheduledAt) return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

        try
        {
            var interval = TimeSpan.FromSeconds(Math.Max(30, runtime.CodexInspectionIntervalSeconds));
            _nextScheduledAt = now + interval;
            // 巡检流程：HTTP 额度查询在锁外执行（避免长时间持锁阻塞其他 DB 操作），
            // 只有 DB 写入（禁用/启用账号）在 SerialExecuteAsync 锁内。
            await RunInspectionAsync(dbContext, cache, quotaService, runtime.CodexQuotaMaxCacheHours, runtime.CodexAutoDisableThresholdPercent, forceRefresh: false, autoTriggered: true, ct);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>
    /// 手动触发一轮巡检。走与自动巡检相同的重入保护。
    /// </summary>
    public async Task<InspectionRunResult> RunManualAsync(bool forceRefresh, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return _lastRun ?? new InspectionRunResult { FinishedAt = DateTimeOffset.UtcNow };
        }
        try
        {
            using var scope = _services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
            var quotaService = scope.ServiceProvider.GetRequiredService<ICodexQuotaService>();
            var runtime = await cache.GetRuntimeSettingsAsync(ct);
            return await RunInspectionAsync(dbContext, cache, quotaService, runtime.CodexQuotaMaxCacheHours, runtime.CodexAutoDisableThresholdPercent, forceRefresh, autoTriggered: false, ct);
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>
    /// 获取上次巡检结果（供前端展示）。
    /// </summary>
    public InspectionRunResult? GetLastRun() => _lastRun;

    /// <summary>
    /// 获取巡检操作日志（最新在前，最多 MaxLogs 条）。
    /// </summary>
    public List<InspectionLogEntry> GetLogs() => _logs.Reverse().ToList();

    /// <summary>
    /// 获取调度状态。
    /// </summary>
    public object GetStatus()
    {
        return new
        {
            isRunning = _running == 1,
            nextScheduledAt = _nextScheduledAt == default ? null : (DateTimeOffset?)_nextScheduledAt,
            lastFinishedAt = _lastRun?.FinishedAt,
        };
    }

    // —— 核心 ——
    private async Task<InspectionRunResult> RunInspectionAsync(
        AppDbContext dbContext, ProxyRequestMetadataCache cache, ICodexQuotaService quotaService, int maxCacheHours,
        int autoDisableThresholdPercent,
        bool forceRefresh, bool autoTriggered, CancellationToken ct)
    {
        var result = new InspectionRunResult
        {
            IsRunning = true,
            ForcedRefresh = forceRefresh,
            StartedAt = DateTimeOffset.UtcNow,
            AutoTriggered = autoTriggered,
        };
        _lastRun = result;
        AddLog("inspection", $"{(autoTriggered ? "自动" : "手动")}巡检开始{(forceRefresh ? "（强制真实刷新）" : "")}");

        try
        {
            var accounts = await cache.GetCodexAccountsAsync(ct);

            foreach (var account in accounts)
            {
                if (ct.IsCancellationRequested) break;
                var ar = await InspectAccountAsync(dbContext, cache, quotaService, account, maxCacheHours, autoDisableThresholdPercent, forceRefresh, ct);
                result.Accounts.Add(ar);
            }

            result.IsRunning = false;
            result.FinishedAt = DateTimeOffset.UtcNow;
            AddLog("inspection", $"巡检完成：保留 {result.KeepCount}、禁用 {result.DisableCount}、启用 {result.EnableCount}、缓存命中 {result.CacheCount}、真实刷新 {result.RealRefreshCount}");
            return result;
        }
        catch (Exception ex)
        {
            result.IsRunning = false;
            result.FinishedAt = DateTimeOffset.UtcNow;
            AddLog("system", $"巡检异常：{ex.Message}");
            _logger.LogError(ex, "Codex inspection run error");
            return result;
        }
    }

    private async Task<InspectionAccountResult> InspectAccountAsync(
        AppDbContext dbContext, ProxyRequestMetadataCache cache, ICodexQuotaService quotaService,
        CodexAccount account, int maxCacheHours, int autoDisableThresholdPercent, bool forceRefresh, CancellationToken ct)
    {
        var ar = new InspectionAccountResult { AccountId = account.Id, DisplayName = account.DisplayName };

        // 1. 解析上次额度快照（从 LastQuotaRawJson）
        CodexQuotaInfo? lastInfo = null;
        if (!string.IsNullOrEmpty(account.LastQuotaRawJson))
        {
            lastInfo = BuildInfoFromRaw(account.LastQuotaRawJson);
        }

        // 2. 判定缓存 vs 真实刷新
        var now = DateTimeOffset.UtcNow;
        bool usedCache = false;
        if (!forceRefresh)
        {
            // 是否被使用：读内存映射（SiteUsageTracker），不查 DB
            var hasUsage = HasRecentUsage(account.LinkedSiteId, account.LastQuotaCheckedAt);
            if (QuotaCachePolicy.TryReuseQuota(lastInfo, account.LastQuotaCheckedAt, hasUsage, maxCacheHours, now, out var reason))
            {
                usedCache = true;
                ar.FromCache = true;
                ar.Reason = $"命中缓存：{reason}";
                AddLog("quota", $"账号 {account.DisplayName} 命中缓存：{reason}");
            }
        }

        CodexQuotaInfo info;
        if (usedCache && lastInfo != null)
        {
            info = lastInfo;
        }
        else
        {
            // 真实刷新
            info = await quotaService.QueryAsync(account, forceRefresh: true, ct);
            ar.Reason = info.Success ? "已真实刷新额度" : (info.Error ?? "额度查询失败");
            AddLog("quota", $"账号 {account.DisplayName} 真实刷新{(info.Success ? "成功" : "失败：" + info.Error)}");
        }

        // 3. 提取窗口百分比（优先 5 小时，没有才看周窗口）
        ar.FiveHourUsedPercent = info.Windows.FirstOrDefault(w => w.Id == "five-hour")?.UsedPercent;
        ar.WeeklyUsedPercent = info.Windows.FirstOrDefault(w => w.Id == "weekly")?.UsedPercent;
        var checkPercent = ar.FiveHourUsedPercent ?? ar.WeeklyUsedPercent;

        // 4. 自动禁用/启用判定
        var threshold = (double)autoDisableThresholdPercent;

        if (info.Success && checkPercent.HasValue && checkPercent.Value >= threshold && account.IsEnabled)
        {
            await DisableAccountAsync(dbContext, cache, account, ct, $"额度使用 {checkPercent.Value:F1}% 达到阈值 {threshold}");
            ar.Action = "disable";
            ar.Reason = (ar.Reason + "；").Replace("；；", "；") + $"额度 {checkPercent.Value:F1}%≥{threshold}，已自动禁用";
        }
        else if (info.Success && account.IsEnabled == false && !account.IsQuotaCooling && !account.DisabledByFeatureToggle
                 && !account.ManuallyDisabled
                 && checkPercent.HasValue && checkPercent.Value < threshold)
        {
            await EnableAccountAsync(dbContext, cache, account, ct, "额度已恢复");
            ar.Action = "enable";
            ar.Reason = (ar.Reason + "；").Replace("；；", "；") + "额度已恢复，已自动启用";
        }
        else
        {
            ar.Action = "keep";
        }

        return ar;
    }

    /// <summary>
    /// 查询该账号隐藏 Site 自 since 后是否有新的使用日志（判定是否被使用）。
    /// </summary>
    /// <summary>
    /// 判断该账号关联的 Site 自 since 后是否被使用过。
    /// 读内存映射（SiteUsageTracker，由日志入队时增量更新），零 DB 查询，替代原回查 ProxyUsageLogs。
    /// </summary>
    private bool HasRecentUsage(Guid siteId, DateTimeOffset? since)
    {
        var lastUsedAt = _siteUsageTracker.GetLastUsedAt(siteId);
        if (lastUsedAt is null) return false;
        var cutoff = since ?? DateTimeOffset.UtcNow.AddDays(-1);
        return lastUsedAt.Value > cutoff;
    }

    private static CodexQuotaInfo? BuildInfoFromRaw(string rawJson)
    {
        try
        {
            var (planType, windows) = CodexUsageParser.Parse(rawJson);
            return new CodexQuotaInfo
            {
                Success = true,
                PlanType = planType,
                Windows = windows.Select(w => new CodexQuotaWindow
                {
                    Id = w.Id, Label = w.Label, UsedPercent = w.UsedPercent,
                    ResetLabel = w.ResetLabel, ResetAtUtc = w.ResetAtUtc, LimitWindowSeconds = w.LimitWindowSeconds,
                }).ToList(),
                RawJson = rawJson,
            };
        }
        catch { return null; }
    }

    private static async Task DisableAccountAsync(AppDbContext dbContext, ProxyRequestMetadataCache cache, CodexAccount account, CancellationToken ct, string reason)
    {
        // 用 CopyNew 独立连接写入，不碰单例 SqlSugarScope，无需串行锁
        using var client = dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        account.IsEnabled = false;
        await client.Updateable(account).ExecuteCommandAsync(ct);
        var site = await client.Queryable<Domain.Sites.Site>().InSingleAsync(account.LinkedSiteId);
        if (site != null && site.IsEnabled)
        {
            site.IsEnabled = false;
            await client.Updateable(site).ExecuteCommandAsync(ct);
        }
        cache.InvalidateRouteTargets();
        cache.InvalidateCodexAccounts();
    }

    private static async Task EnableAccountAsync(AppDbContext dbContext, ProxyRequestMetadataCache cache, CodexAccount account, CancellationToken ct, string reason)
    {
        using var client = dbContext.Client.CopyNew();
        client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
        account.IsEnabled = true;
        account.ManuallyDisabled = false;
        await client.Updateable(account).ExecuteCommandAsync(ct);
        var site = await client.Queryable<Domain.Sites.Site>().InSingleAsync(account.LinkedSiteId);
        if (site != null && !site.IsEnabled)
        {
            site.IsEnabled = true;
            await client.Updateable(site).ExecuteCommandAsync(ct);
        }
        cache.InvalidateRouteTargets();
        cache.InvalidateCodexAccounts();
    }

    private void AddLog(string category, string message)
    {
        _logs.Enqueue(new InspectionLogEntry { Category = category, Message = message });
        while (_logs.Count > MaxLogs) _logs.TryDequeue(out _);
    }
}
