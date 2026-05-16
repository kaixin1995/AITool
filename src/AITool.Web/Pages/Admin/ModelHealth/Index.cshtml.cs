using AITool.Domain.Models;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.ModelHealth;

/// <summary>
/// 模型健康页中的站点状态。
/// </summary>
public class ModelHealthSiteViewModel
{
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;
    /// <summary>
    /// 远程模型名称。
    /// </summary>
    public string RemoteModelName { get; set; } = string.Empty;
    /// <summary>
    /// 最近状态。
    /// </summary>
    public string LastStatus { get; set; } = string.Empty;
    /// <summary>
    /// 最近检测时间。
    /// </summary>
    public DateTimeOffset? LastCheckedAt { get; set; }
    /// <summary>
    /// 最近耗时（毫秒）。
    /// </summary>
    public int? LastDurationMs { get; set; }
    /// <summary>
    /// 最近日志。
    /// </summary>
    public List<ModelHealthLogEntry> RecentLogs { get; set; } = [];
    /// <summary>
    /// 时间线片段。
    /// </summary>
    public List<ModelHealthTimelineSegment> TimelineSegments { get; set; } = [];
    /// <summary>
    /// 成功率。
    /// </summary>
    public double SuccessRate { get; set; }
}

/// <summary>
/// 模型健康日志项。
/// </summary>
public class ModelHealthLogEntry
{
    /// <summary>
    /// 状态。
    /// </summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>
    /// 耗时（毫秒）。
    /// </summary>
    public int DurationMs { get; set; }
    /// <summary>
    /// 检测时间。
    /// </summary>
    public DateTimeOffset CheckedAt { get; set; }
    /// <summary>
    /// 错误信息。
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// 模型健康时间线片段。
/// </summary>
public class ModelHealthTimelineSegment
{
    /// <summary>
    /// 状态。
    /// </summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>
    /// 数量。
    /// </summary>
    public int Count { get; set; }
    /// <summary>
    /// 开始时间。
    /// </summary>
    public DateTimeOffset StartAt { get; set; }
    /// <summary>
    /// 结束时间。
    /// </summary>
    public DateTimeOffset EndAt { get; set; }
}

/// <summary>
/// 已监控模型项。
/// </summary>
public class MonitoredModelItem
{
    /// <summary>
    /// 模型库项标识。
    /// </summary>
    public Guid ModelLibraryItemId { get; set; }
    /// <summary>
    /// 显示名称。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>
    /// 站点数量。
    /// </summary>
    public int SiteCount { get; set; }
    /// <summary>
    /// 健康站点数量。
    /// </summary>
    public int HealthySiteCount { get; set; }
    /// <summary>
    /// 异常站点数量。
    /// </summary>
    public int UnhealthySiteCount { get; set; }
    /// <summary>
    /// 平均成功率。
    /// </summary>
    public double AverageSuccessRate { get; set; }
    /// <summary>
    /// 最近检测时间。
    /// </summary>
    public DateTimeOffset? LastCheckedAt { get; set; }
    /// <summary>
    /// 平均耗时（毫秒）。
    /// </summary>
    public int? AverageDurationMs { get; set; }
    /// <summary>
    /// 请求总数。
    /// </summary>
    public int TotalRequestCount { get; set; }
}

/// <summary>
/// 模型选择项。
/// </summary>
public class ModelSelectItem
{
    /// <summary>
    /// 标识。
    /// </summary>
    public Guid Id { get; set; }
    /// <summary>
    /// 显示名称。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// 健康数据时间范围选项。
/// </summary>
public sealed class ModelHealthRangeOption
{
    /// <summary>
    /// 值。
    /// </summary>
    public string Value { get; set; } = string.Empty;
    /// <summary>
    /// 标签。
    /// </summary>
    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// 模型健康页面模型。
/// </summary>
public class IndexModel : PageModel
{
    /// <summary>
    /// 数据库上下文。
    /// </summary>
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 模型健康页面模型。
    /// </summary>
    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 可选模型列表。
    /// </summary>
    public List<ModelSelectItem> AvailableModels { get; set; } = [];

    /// <summary>
    /// 已监控模型列表。
    /// </summary>
    public List<MonitoredModelItem> MonitoredModels { get; set; } = [];

    /// <summary>
    /// 健康数据。
    /// </summary>
    public Dictionary<Guid, List<ModelHealthSiteViewModel>> HealthData { get; set; } = [];

    /// <summary>
    /// 时间范围选项。
    /// </summary>
    public List<ModelHealthRangeOption> RangeOptions { get; } =
    [
        new() { Value = "1d", Label = "最近 24 小时" },
        new() { Value = "7d", Label = "最近 7 天" },
        new() { Value = "30d", Label = "最近 30 天" }
    ];

    /// <summary>
    /// 当前时间范围。
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string Range { get; set; } = "7d";

    /// <summary>
    /// 状态提示。
    /// </summary>
    public string? StatusMessage { get; set; }
    /// <summary>
    /// 操作是否成功。
    /// </summary>
    public bool StatusSuccess { get; set; }

    /// <summary>
    /// 处理页面加载请求。
    /// </summary>
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoadDataAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // 加载失败时保证页面不崩溃，显示错误提示
            StatusMessage = "加载数据时出错：" + ex.Message;
            StatusSuccess = false;
        }
    }

    /// <summary>
    /// 加载页面数据。
    /// </summary>
    private async Task LoadDataAsync(CancellationToken cancellationToken)
    {
        /* 加载已监控的模型列表 */
        var monitors = await _dbContext.ModelHealthMonitors.ToListAsync(cancellationToken);
        var monitoredModelIds = monitors.Select(m => m.ModelLibraryItemId).Distinct().ToList();

        var models = await _dbContext.ModelLibraryItems
            .Where(m => monitoredModelIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m, cancellationToken);
        var orphanMonitors = monitors
            .Where(m => !models.ContainsKey(m.ModelLibraryItemId))
            .ToList();
        if (orphanMonitors.Count > 0)
        {
            // 历史删除模型留下的监控配置在这里顺手清掉，避免页面继续出现已删除模型。
            _dbContext.ModelHealthMonitors.RemoveRange(orphanMonitors);
            await _dbContext.SaveChangesAsync(cancellationToken);
            monitors = monitors
                .Where(m => models.ContainsKey(m.ModelLibraryItemId))
                .ToList();
        }

        monitoredModelIds = monitors
            .Select(m => m.ModelLibraryItemId)
            .Distinct()
            .ToList();

        MonitoredModels = monitors
            .Select(m => new MonitoredModelItem
            {
                ModelLibraryItemId = m.ModelLibraryItemId,
                DisplayName = models[m.ModelLibraryItemId].DisplayName
            })
            .OrderBy(m => m.DisplayName)
            .ToList();

        /* 构建可选模型列表（排除已监控的） */
        AvailableModels = await _dbContext.ModelLibraryItems
            .Where(m => !monitoredModelIds.Contains(m.Id))
            .OrderBy(m => m.DisplayName)
            .Select(m => new ModelSelectItem
            {
                Id = m.Id,
                DisplayName = m.DisplayName
            })
            .ToListAsync(cancellationToken);

        /* 加载每个监控模型的健康数据 */
        if (monitoredModelIds.Count > 0)
        {
            var mappings = await _dbContext.SiteModelMappings
                .Where(m => monitoredModelIds.Contains(m.ModelLibraryItemId) && m.IsEnabled)
                .ToListAsync(cancellationToken);

            var siteIds = mappings.Select(m => m.SiteId).Distinct().ToList();
            var sites = await _dbContext.Sites
                .Where(s => siteIds.Contains(s.Id) && s.IsEnabled)
                .ToDictionaryAsync(s => s.Id, s => s, cancellationToken);

            var recentCutoff = ResolveRecentCutoff(Range);
            // 先全量加载到内存，再按最近时间窗口过滤，避免 SQLite 无法翻译 DateTimeOffset 比较。
            var allLogs = await _dbContext.ProxyUsageLogs
                .Where(x => x.IsFinalResult)
                .ToListAsync(cancellationToken);
            allLogs = allLogs
                .Where(x => x.RequestedAt >= recentCutoff)
                .ToList();

            var routeRules = await _dbContext.ProxyRouteRules
                .Where(x => x.IsEnabled)
                .ToListAsync(cancellationToken);

            /* 按模型分组 */
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
                    .Where(l => matchedRequestModels.Contains(l.RequestModel)
                        || matchedAttemptedModels.Contains(l.AttemptedModel))
                    .ToList();

                var healthList = modelMappings.Select(map =>
                {
                    sites.TryGetValue(map.SiteId, out var site);
                    var siteLogs = modelLogs
                        .Where(l => l.TargetSiteId == map.SiteId
                            || string.Equals(l.AttemptedModel, map.RemoteModelName, StringComparison.Ordinal))
                        .OrderByDescending(l => l.RequestedAt)
                        .ToList();

                    var timelineSegments = BuildTimelineSegments(siteLogs);
                    var latestLog = siteLogs.FirstOrDefault();
                    var successCount = siteLogs.Count(l => l.Status == "success");
                    var totalLogs = siteLogs.Count;

                    return new ModelHealthSiteViewModel
                    {
                        SiteName = site?.Name ?? "(未知站点)",
                        RemoteModelName = map.RemoteModelName,
                        LastStatus = latestLog?.Status ?? map.LastStatus,
                        LastCheckedAt = latestLog?.RequestedAt,
                        LastDurationMs = latestLog?.TotalDurationMs,
                        RecentLogs = siteLogs.Select(l => new ModelHealthLogEntry
                        {
                            Status = l.Status,
                            DurationMs = l.TotalDurationMs,
                            CheckedAt = l.RequestedAt,
                            ErrorMessage = l.ErrorMessage
                        }).ToList(),
                        TimelineSegments = timelineSegments,
                        SuccessRate = totalLogs > 0 ? (double)successCount / totalLogs : 0
                    };
                })
                .OrderBy(s => s.SiteName)
                .ToList();

                HealthData[modelId] = healthList;
            }

            foreach (var monitored in MonitoredModels)
            {
                var healths = HealthData.GetValueOrDefault(monitored.ModelLibraryItemId) ?? [];

                monitored.SiteCount = healths.Count;
                monitored.HealthySiteCount = healths.Count(x => x.LastStatus == "success");
                monitored.UnhealthySiteCount = healths.Count(x => x.LastStatus == "fail");
                monitored.AverageSuccessRate = healths.Count > 0 ? healths.Average(x => x.SuccessRate) : 0;
                monitored.LastCheckedAt = healths
                    .Where(x => x.LastCheckedAt.HasValue)
                    .Select(x => x.LastCheckedAt)
                    .OrderByDescending(x => x)
                    .FirstOrDefault();
                monitored.AverageDurationMs = healths.Any(x => x.LastDurationMs.HasValue)
                    ? (int)Math.Round(healths.Where(x => x.LastDurationMs.HasValue).Average(x => x.LastDurationMs ?? 0), MidpointRounding.AwayFromZero)
                    : null;
                // 汇总最近展示窗口内的请求数，供列表模式直接展示。
                monitored.TotalRequestCount = healths.Sum(x => x.RecentLogs.Count);
            }
        }
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
    /// 构建健康时间线片段。
    /// </summary>
    private static List<ModelHealthTimelineSegment> BuildTimelineSegments(List<ProxyUsageLog> siteLogs)
    {
        const int segmentCount = 48;

        if (siteLogs.Count == 0)
        {
            return [];
        }

        var orderedLogs = siteLogs
            .OrderBy(l => l.RequestedAt)
            .ToList();
        var startAt = orderedLogs.First().RequestedAt;
        var endAt = orderedLogs.Last().RequestedAt;

        if (startAt >= endAt)
        {
            return
            [
                new ModelHealthTimelineSegment
                {
                    Status = string.Equals(orderedLogs[0].Status, "success", StringComparison.OrdinalIgnoreCase) ? "success" : "fail",
                    Count = orderedLogs.Count,
                    StartAt = startAt,
                    EndAt = endAt
                }
            ];
        }

        var totalTicks = endAt.UtcTicks - startAt.UtcTicks;
        var bucketSize = Math.Max(totalTicks / segmentCount, 1L);
        var buckets = new List<ModelHealthTimelineSegment>(segmentCount);

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

            buckets.Add(new ModelHealthTimelineSegment
            {
                Status = bucketLogs.Any(log => !string.Equals(log.Status, "success", StringComparison.OrdinalIgnoreCase)) ? "fail" : "success",
                Count = bucketLogs.Count,
                StartAt = bucketLogs.First().RequestedAt,
                EndAt = bucketLogs.Last().RequestedAt
            });
        }

        return buckets;
    }

    /// <summary>
    /// 添加模型健康监控。
    /// </summary>
    public async Task<IActionResult> OnPostAddMonitorAsync(Guid modelId, CancellationToken cancellationToken)
    {
        try
        {
            var exists = await _dbContext.ModelHealthMonitors
                .AnyAsync(m => m.ModelLibraryItemId == modelId, cancellationToken);
            if (!exists)
            {
                _dbContext.ModelHealthMonitors.Add(new ModelHealthMonitor
                {
                    ModelLibraryItemId = modelId
                });
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "添加监控失败：" + ex.Message;
            StatusSuccess = false;
        }
        try
        {
            await LoadDataAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            StatusMessage = (StatusMessage ?? "") + " 加载数据出错：" + ex.Message;
            StatusSuccess = false;
        }
        return Page();
    }

    /// <summary>
    /// 移除模型健康监控。
    /// </summary>
    public async Task<IActionResult> OnPostRemoveMonitorAsync(Guid modelId, CancellationToken cancellationToken)
    {
        try
        {
            var monitor = await _dbContext.ModelHealthMonitors
                .FirstOrDefaultAsync(m => m.ModelLibraryItemId == modelId, cancellationToken);
            if (monitor is not null)
            {
                _dbContext.ModelHealthMonitors.Remove(monitor);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "移除监控失败：" + ex.Message;
            StatusSuccess = false;
        }
        try
        {
            await LoadDataAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            StatusMessage = (StatusMessage ?? "") + " 加载数据出错：" + ex.Message;
            StatusSuccess = false;
        }
        return Page();
    }
}
