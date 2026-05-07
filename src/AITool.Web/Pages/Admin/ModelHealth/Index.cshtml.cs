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
    // 最近 N 条检测记录（用于绘制时间线）
    public List<ModelHealthLogEntry> RecentLogs { get; set; } = [];
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
        var monitoredModelIds = monitors.Select(m => m.ModelLibraryItemId).ToList();

        var models = await _dbContext.ModelLibraryItems
            .Where(m => monitoredModelIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m, cancellationToken);

        MonitoredModels = monitors
            .Select(m =>
            {
                models.TryGetValue(m.ModelLibraryItemId, out var model);
                return new MonitoredModelItem
                {
                    ModelLibraryItemId = m.ModelLibraryItemId,
                    DisplayName = model?.DisplayName ?? "(已删除的模型)"
                };
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

            var recentCutoff = DateTimeOffset.UtcNow.AddDays(-7);
            // 先全量加载到内存，再用 C# 过滤，避免 SQLite 无法翻译 DateTimeOffset 比较
            var allLogs = await _dbContext.ProxyUsageLogs
                .Where(x => x.IsFinalResult)
                .ToListAsync(cancellationToken);
            allLogs = allLogs
                .Where(d => monitoredModelIds.Contains(GetModelIdByName(models, d.RequestModel)) && d.RequestedAt >= recentCutoff)
                .ToList();

            /* 按模型分组 */
            foreach (var modelId in monitoredModelIds)
            {
                var modelMappings = mappings
                    .Where(m => m.ModelLibraryItemId == modelId && sites.ContainsKey(m.SiteId))
                    .ToList();
                var modelName = models.TryGetValue(modelId, out var currentModel) ? currentModel.ModelName : string.Empty;
                var modelLogs = allLogs.Where(l => string.Equals(l.RequestModel, modelName, StringComparison.Ordinal)).ToList();

                var healthList = modelMappings.Select(map =>
                {
                    sites.TryGetValue(map.SiteId, out var site);
                    var siteLogs = modelLogs
                        .Where(l => l.TargetSiteId == map.SiteId)
                        .OrderByDescending(l => l.RequestedAt)
                        .Take(20)
                        .ToList();

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

    // 按模型名反查监控模型 ID，便于将 UsageLogs 归并回健康看板。
    private static Guid GetModelIdByName(Dictionary<Guid, ModelLibraryItem> models, string requestModel)
    {
        return models
            .Where(x => string.Equals(x.Value.ModelName, requestModel, StringComparison.Ordinal))
            .Select(x => x.Key)
            .FirstOrDefault();
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
