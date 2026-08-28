using AITool.Domain.Models;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Infrastructure.Persistence;
using AITool.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Admin.Controllers.Admin;

/// <summary>
/// 模型健康监控 API：监控列表（含 48 段时间线）、添加/移除监控。
/// <para>
/// 迁移自 <c>Pages/Admin/ModelHealth/Index.cshtml.cs</c>。
/// 时间线算法（48 段分桶）与原 PageModel 完全一致，按 RequestedAt 时间范围分桶，
/// 每段状态为「全成功=success，否则=fail」。
/// </para>
/// <para>
/// 数据源是 <see cref="ProxyUsageLog"/>，按 RequestModel / AttemptedModel / TargetSiteId 三路匹配。
/// 加载时会顺手清理「指向已删除模型」的孤儿监控配置。
/// </para>
/// </summary>
[ApiController]
[Route("api/admin/model-health")]
public sealed class ModelHealthApiController : ControllerBase
{
    /// <summary>
    /// 数据库上下文。
    /// </summary>
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 初始化模型健康监控 API 控制器。
    /// </summary>
    public ModelHealthApiController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 获取健康监控面板数据（监控模型列表 + 每个模型各站点的健康详情 + 48 段时间线）。
    /// </summary>
    /// <param name="range">时间范围：1d / 7d（默认）/ 30d。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet]
    public async Task<IActionResult> GetDashboard([FromQuery] string? range, CancellationToken cancellationToken)
    {
        var result = await LoadDashboardAsync(range, cancellationToken);
        return Ok(ApiResponse.Ok(result));
    }

    /// <summary>
    /// 添加模型到健康监控。
    /// </summary>
    [HttpPost("{modelId:guid}/monitor")]
    public async Task<IActionResult> AddMonitor(Guid modelId, CancellationToken cancellationToken)
    {
        var exists = await _dbContext.ModelHealthMonitors.AnyAsync(x => x.ModelLibraryItemId == modelId, cancellationToken);
        if (exists)
        {
            return Ok(ApiResponse.Ok("该模型已在监控列表中"));
        }

        var modelExists = await _dbContext.ModelLibraryItems.AnyAsync(x => x.Id == modelId, cancellationToken);
        if (!modelExists)
        {
            return NotFound(ApiResponse.Fail("模型不存在", "model_not_found"));
        }

        _dbContext.ModelHealthMonitors.Add(new ModelHealthMonitor { ModelLibraryItemId = modelId });
        return Ok(ApiResponse.Ok("监控已添加"));
    }

    /// <summary>
    /// 从健康监控移除模型。
    /// </summary>
    [HttpDelete("{modelId:guid}/monitor")]
    public async Task<IActionResult> RemoveMonitor(Guid modelId, CancellationToken cancellationToken)
    {
        var monitor = (await _dbContext.ModelHealthMonitors
            .Where(m => m.ModelLibraryItemId == modelId)
            .Take(1)
            .ToListAsync(cancellationToken))
            .FirstOrDefault();
        if (monitor is null)
        {
            return NotFound(ApiResponse.Fail("该模型未在监控列表中", "monitor_not_found"));
        }

        _dbContext.ModelHealthMonitors.Remove(monitor);
        return Ok(ApiResponse.Ok("监控已移除"));
    }

    /// <summary>
    /// 加载监控面板完整数据。逻辑迁移自 ModelHealth/Index.cshtml.cs 的 LoadDataAsync。
    /// </summary>
    private async Task<object> LoadDashboardAsync(string? range, CancellationToken cancellationToken)
    {
        // 1. 加载监控 + 清理孤儿监控（指向已删除模型）。
        var monitors = await _dbContext.ModelHealthMonitors.ToListAsync(cancellationToken);
        var monitoredModelIds = monitors.Select(m => m.ModelLibraryItemId).Distinct().ToList();
        var models = await _dbContext.ModelLibraryItems
            .Where(m => monitoredModelIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m, cancellationToken);

        var orphanMonitors = monitors.Where(m => !models.ContainsKey(m.ModelLibraryItemId)).ToList();
        if (orphanMonitors.Count > 0)
        {
            _dbContext.ModelHealthMonitors.RemoveRange(orphanMonitors);
            monitors = monitors.Where(m => models.ContainsKey(m.ModelLibraryItemId)).ToList();
        }
        monitoredModelIds = monitors.Select(m => m.ModelLibraryItemId).Distinct().ToList();

        var monitoredModels = monitors
            .Select(m => new
            {
                modelLibraryItemId = m.ModelLibraryItemId,
                displayName = models[m.ModelLibraryItemId].ModelName
            })
            .OrderBy(m => m.displayName)
            .ToList();

        // 2. 可选模型（排除已监控的）。
        var availableModels = await _dbContext.ModelLibraryItems
            .Where(m => !monitoredModelIds.Contains(m.Id))
            .OrderBy(m => m.ModelName)
            .Select(m => new { id = m.Id, modelName = m.ModelName, displayName = m.ModelName })
            .ToListAsync(cancellationToken);

        // 3. 无监控模型时直接返回空 healthData。
        var healthData = new Dictionary<Guid, List<object>>();
        if (monitoredModelIds.Count == 0)
        {
            return new
            {
                monitoredModels,
                availableModels,
                healthData,
                rangeOptions = new[] { new { value = "1d", label = "近 1 天" }, new { value = "7d", label = "近 7 天" }, new { value = "30d", label = "近 30 天" } }
            };
        }

        // 4. 加载映射、站点、日志、路由规则。
        var mappings = await _dbContext.SiteModelMappings
            .Where(m => monitoredModelIds.Contains(m.ModelLibraryItemId) && m.IsEnabled)
            .ToListAsync(cancellationToken);
        var siteIds = mappings.Select(m => m.SiteId).Distinct().ToList();
        var sites = await _dbContext.Sites
            .Where(s => siteIds.Contains(s.Id) && s.IsEnabled)
            .ToDictionaryAsync(s => s.Id, s => s, cancellationToken);

        var recentCutoff = ResolveRecentCutoff(range);
        var matchedModelNames = monitoredModelIds
            .Select(id => models.TryGetValue(id, out var m) ? m.ModelName : null)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.Ordinal);
        var matchedRemoteNames = mappings.Select(m => m.RemoteModelName).ToHashSet(StringComparer.Ordinal);
        var matchedUpstreamNames = (await _dbContext.ProxyRouteRules
            .Where(x => x.IsEnabled && matchedModelNames.Contains(x.ExternalModelName))
            .ToListAsync(cancellationToken))
            .Select(x => x.UpstreamModelName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToHashSet(StringComparer.Ordinal);

        var allLogs = await _dbContext.ProxyUsageLogs
            .Where(x => x.RequestedAt >= recentCutoff)
            .Where(x => matchedModelNames.Contains(x.RequestModel)
                || matchedRemoteNames.Contains(x.AttemptedModel)
                || matchedUpstreamNames.Contains(x.AttemptedModel))
            .ToListAsync(cancellationToken);

        var routeRules = await _dbContext.ProxyRouteRules
            .Where(x => x.IsEnabled)
            .ToListAsync(cancellationToken);

        // 5. 按模型分组聚合。
        var modelSummaries = new List<object>();
        foreach (var modelId in monitoredModelIds)
        {
            var modelMappings = mappings
                .Where(m => m.ModelLibraryItemId == modelId && sites.ContainsKey(m.SiteId))
                .ToList();
            var modelName = models.TryGetValue(modelId, out var currentModel) ? currentModel.ModelName : string.Empty;
            var relatedRouteRules = routeRules
                .Where(x => string.Equals(x.ExternalModelName, modelName, StringComparison.Ordinal))
                .ToList();
            var matchedRequestModels = relatedRouteRules
                .Select(x => x.ExternalModelName)
                .Concat([modelName])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);
            var matchedAttemptedModels = modelMappings
                .Select(x => x.RemoteModelName)
                .Concat(relatedRouteRules.Select(x => x.UpstreamModelName))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToHashSet(StringComparer.Ordinal);
            var modelLogs = allLogs
                .Where(l => matchedRequestModels.Contains(l.RequestModel) || matchedAttemptedModels.Contains(l.AttemptedModel))
                .ToList();

            // 用强类型 record 投影，避免反射开销（原 PageModel 用强类型 ViewModel，零反射）。
            var healthList = modelMappings.Select(map =>
            {
                sites.TryGetValue(map.SiteId, out var site);
                var siteLogs = modelLogs
                    .Where(l => l.TargetSiteId == map.SiteId
                        || (l.TargetSiteId == Guid.Empty && string.Equals(l.AttemptedModel, map.RemoteModelName, StringComparison.Ordinal)))
                    .OrderByDescending(l => l.RequestedAt)
                    .ToList();

                var latestLog = siteLogs.FirstOrDefault();
                var successCount = siteLogs.Count(l => l.Status == "success");
                var totalLogs = siteLogs.Count;

                return new HealthSiteItem
                {
                    SiteName = site?.Name ?? "(未知站点)",
                    RemoteModelName = map.RemoteModelName,
                    LastStatus = latestLog?.Status ?? map.LastStatus,
                    LastCheckedAt = latestLog?.RequestedAt,
                    LastDurationMs = latestLog?.TotalDurationMs,
                    RecentLogs = siteLogs.Select(l => new HealthLogEntry
                    {
                        Status = l.Status,
                        DurationMs = l.TotalDurationMs,
                        CheckedAt = l.RequestedAt,
                        ErrorMessage = l.ErrorMessage
                    }).ToList(),
                    TimelineSegments = BuildTimelineSegments(siteLogs),
                    SuccessRate = totalLogs > 0 ? (double)successCount / totalLogs : 0,
                    SuccessCount = successCount,
                    FailureCount = totalLogs - successCount
                };
            })
            .OrderBy(s => s.SiteName)
            .ToList();

            healthData[modelId] = healthList.Select(h => (object)new
            {
                siteName = h.SiteName,
                remoteModelName = h.RemoteModelName,
                lastStatus = h.LastStatus,
                lastCheckedAt = h.LastCheckedAt,
                lastDurationMs = h.LastDurationMs,
                successRate = h.SuccessRate,
                successCount = h.SuccessCount,
                failureCount = h.FailureCount,
                totalRequestCount = h.SuccessCount + h.FailureCount,
                timelineSegments = h.TimelineSegments
            }).ToList();

            // 模型级汇总（二次聚合）—— 直接属性访问，零反射。
            var modelLevelLogs = healthList
                .SelectMany(h => h.RecentLogs)
                .Select(x => new ProxyUsageLog
                {
                    Status = x.Status,
                    TotalDurationMs = x.DurationMs,
                    RequestedAt = x.CheckedAt,
                    ErrorMessage = x.ErrorMessage ?? string.Empty
                })
                .ToList();

            var siteCount = healthList.Count;
            var healthySiteCount = healthList.Count(x => string.Equals(x.LastStatus, "success", StringComparison.OrdinalIgnoreCase));
            var unhealthySiteCount = healthList.Count(x => string.Equals(x.LastStatus, "fail", StringComparison.OrdinalIgnoreCase));
            var lastCheckedAt = healthList
                .Where(x => x.LastCheckedAt.HasValue)
                .Select(x => x.LastCheckedAt)
                .OrderByDescending(x => x)
                .FirstOrDefault();
            var durations = healthList.Where(x => x.LastDurationMs.HasValue).Select(x => x.LastDurationMs!.Value).ToList();
            var averageDurationMs = durations.Count > 0
                ? (int)Math.Round(durations.Average(), MidpointRounding.AwayFromZero)
                : (int?)null;
            var successTotal = healthList.Sum(x => x.SuccessCount);
            var failureTotal = healthList.Sum(x => x.FailureCount);
            var totalRequestCount = successTotal + failureTotal;

            modelSummaries.Add(new
            {
                modelLibraryItemId = modelId,
                displayName = models[modelId].ModelName,
                siteCount,
                healthySiteCount,
                unhealthySiteCount,
                lastCheckedAt,
                averageDurationMs,
                successCount = successTotal,
                failureCount = failureTotal,
                totalRequestCount,
                averageSuccessRate = totalRequestCount > 0 ? (double)successTotal / totalRequestCount : 0,
                timelineSegments = BuildTimelineSegments(modelLevelLogs)
            });
        }

        return new
        {
            monitoredModels = modelSummaries,
            availableModels,
            healthData,
            rangeOptions = new[] { new { value = "1d", label = "近 1 天" }, new { value = "7d", label = "近 7 天" }, new { value = "30d", label = "近 30 天" } }
        };
    }

    /// <summary>
    /// 计算最近数据的时间下限。
    /// </summary>
    private static DateTimeOffset ResolveRecentCutoff(string? range)
    {
        return (range ?? "7d").Trim().ToLowerInvariant() switch
        {
            "1d" => DateTimeOffset.UtcNow.AddDays(-1),
            "30d" => DateTimeOffset.UtcNow.AddDays(-30),
            _ => DateTimeOffset.UtcNow.AddDays(-7)
        };
    }

    /// <summary>
    /// 构建健康时间线片段（48 段分桶）。逻辑与原 PageModel 完全一致。
    /// </summary>
    private static List<object> BuildTimelineSegments(List<ProxyUsageLog> siteLogs)
    {
        const int segmentCount = 48;

        if (siteLogs.Count == 0)
        {
            return [];
        }

        var orderedLogs = siteLogs.OrderBy(l => l.RequestedAt).ToList();
        var startAt = orderedLogs.First().RequestedAt;
        var endAt = orderedLogs.Last().RequestedAt;

        // 退化情况：所有日志同一时刻（或乱序导致 start>=end），合并成单段。
        if (startAt >= endAt)
        {
            var singleSuccess = orderedLogs.Count(log => string.Equals(log.Status, "success", StringComparison.OrdinalIgnoreCase));
            return
            [
                (object)new
                {
                    status = singleSuccess == orderedLogs.Count ? "success" : "fail",
                    count = orderedLogs.Count,
                    successCount = singleSuccess,
                    failureCount = orderedLogs.Count - singleSuccess,
                    startAt,
                    endAt
                }
            ];
        }

        var totalTicks = endAt.UtcTicks - startAt.UtcTicks;
        var bucketSize = Math.Max(totalTicks / segmentCount, 1L);
        var buckets = new List<object>(segmentCount);

        for (var i = 0; i < segmentCount; i++)
        {
            var bucketStartTicks = startAt.UtcTicks + (bucketSize * i);
            var bucketEndTicks = i == segmentCount - 1
                ? endAt.UtcTicks
                : Math.Min(startAt.UtcTicks + (bucketSize * (i + 1)), endAt.UtcTicks);
            var bucketLogs = orderedLogs
                .Where(log =>
                {
                    var ticks = log.RequestedAt.UtcTicks;
                    return i == segmentCount - 1
                        ? ticks >= bucketStartTicks && ticks <= bucketEndTicks
                        : ticks >= bucketStartTicks && ticks < bucketEndTicks;
                })
                .ToList();

            if (bucketLogs.Count == 0)
            {
                continue;
            }

            var successCount = bucketLogs.Count(log => string.Equals(log.Status, "success", StringComparison.OrdinalIgnoreCase));
            buckets.Add(new
            {
                status = successCount == bucketLogs.Count ? "success" : "fail",
                count = bucketLogs.Count,
                successCount,
                failureCount = bucketLogs.Count - successCount,
                startAt = bucketLogs.First().RequestedAt,
                endAt = bucketLogs.Last().RequestedAt
            });
        }

        return buckets;
    }
}

/// <summary>
/// 健康面板单个站点+模型的健康项（强类型，避免反射）。
/// </summary>
internal sealed class HealthSiteItem
{
    public string SiteName { get; set; } = string.Empty;
    public string RemoteModelName { get; set; } = string.Empty;
    public string LastStatus { get; set; } = string.Empty;
    public DateTimeOffset? LastCheckedAt { get; set; }
    public int? LastDurationMs { get; set; }
    public List<HealthLogEntry> RecentLogs { get; set; } = [];
    public List<object> TimelineSegments { get; set; } = [];
    public double SuccessRate { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
}

/// <summary>
/// 健康面板单条日志项。
/// </summary>
internal sealed class HealthLogEntry
{
    public string Status { get; set; } = string.Empty;
    public int DurationMs { get; set; }
    public DateTimeOffset CheckedAt { get; set; }
    public string? ErrorMessage { get; set; }
}
