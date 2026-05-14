using AITool.Domain.Models;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.ModelHealth;

// 模型健康度视图模型，展示指定模型在各站点的健康状态
public class ModelHealthSiteViewModel
{
    // 站点名称
    public string SiteName { get; set; } = string.Empty;
    // 远程模型名
    public string RemoteModelName { get; set; } = string.Empty;
    // 最近检测状态
    public string LastStatus { get; set; } = string.Empty;
    // 最近检测时间
    public DateTimeOffset? LastCheckedAt { get; set; }
    // 最近检测耗时（毫秒）
    public int? LastDurationMs { get; set; }
    // 最近 N 条检测记录（用于统计与计数）
    public List<ModelHealthLogEntry> RecentLogs { get; set; } = [];
    // 时间轴线段（固定宽度聚合展示，失败优先标红）
    public List<ModelHealthTimelineSegment> TimelineSegments { get; set; } = [];
    // 成功率（最近 N 条中成功的比例）
    public double SuccessRate { get; set; }
}

// 检测日志条目视图模型
public class ModelHealthLogEntry
{
    // 检测状态
    public string Status { get; set; } = string.Empty;
    // 耗时（毫秒）
    public int DurationMs { get; set; }
    // 检测时间
    public DateTimeOffset CheckedAt { get; set; }
    // 错误信息
    public string? ErrorMessage { get; set; }
}

public class ModelHealthTimelineSegment
{
    // 线段聚合后的状态，存在失败则标记为失败
    public string Status { get; set; } = string.Empty;
    // 该线段包含的请求数
    public int Count { get; set; }
    // 线段起始时间
    public DateTimeOffset StartAt { get; set; }
    // 线段结束时间
    public DateTimeOffset EndAt { get; set; }
}

// 已监控模型视图项
public class MonitoredModelItem
{
    public Guid ModelLibraryItemId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public int SiteCount { get; set; }
    public int HealthySiteCount { get; set; }
    public int UnhealthySiteCount { get; set; }
    public double AverageSuccessRate { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public int? AverageDurationMs { get; set; }
    public int TotalRequestCount { get; set; }
}

// 模型下拉选项
public class ModelSelectItem
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class ModelHealthRangeOption
{
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}

// 模型健康看板页面模型，支持持久化监控配置
public class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // 所有可选模型列表（用于下拉框添加监控）
    public List<ModelSelectItem> AvailableModels { get; set; } = [];

    // 已配置监控的模型列表
    public List<MonitoredModelItem> MonitoredModels { get; set; } = [];

    // 各监控模型的健康数据，按模型 ID 索引
    public Dictionary<Guid, List<ModelHealthSiteViewModel>> HealthData { get; set; } = [];

    public List<ModelHealthRangeOption> RangeOptions { get; } =
    [
        new() { Value = "1d", Label = "最近 24 小时" },
        new() { Value = "7d", Label = "最近 7 天" },
        new() { Value = "30d", Label = "最近 30 天" }
    ];

    [BindProperty(SupportsGet = true)]
    public string Range { get; set; } = "7d";

    // 操作提示
    public string? StatusMessage { get; set; }
    public bool StatusSuccess { get; set; }

    // 加载已监控模型及其健康数据
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

    // 实际的数据加载逻辑
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

    private static DateTimeOffset ResolveRecentCutoff(string? range)
    {
        return (range ?? "7d").Trim().ToLowerInvariant() switch
        {
            "1d" => DateTimeOffset.UtcNow.AddDays(-1),
            "30d" => DateTimeOffset.UtcNow.AddDays(-30),
            _ => DateTimeOffset.UtcNow.AddDays(-7)
        };
    }

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

    // 添加模型到监控列表
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

    // 移除模型监控
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
