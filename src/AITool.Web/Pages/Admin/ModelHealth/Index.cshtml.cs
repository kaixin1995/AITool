using AITool.Domain.Models;
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
                .Where(m => monitoredModelIds.Contains(m.ModelLibraryItemId))
                .ToListAsync(cancellationToken);

            var siteIds = mappings.Select(m => m.SiteId).Distinct().ToList();
            var sites = await _dbContext.Sites
                .Where(s => siteIds.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s, cancellationToken);

            var recentCutoff = DateTimeOffset.UtcNow.AddDays(-7);
            var allLogs = await _dbContext.DetectionLogs
                .Where(d => monitoredModelIds.Contains(d.ModelLibraryItemId) && d.CheckedAt >= recentCutoff)
                .ToListAsync(cancellationToken);

            /* 按模型分组 */
            foreach (var modelId in monitoredModelIds)
            {
                var modelMappings = mappings.Where(m => m.ModelLibraryItemId == modelId).ToList();
                var modelLogs = allLogs.Where(l => l.ModelLibraryItemId == modelId).ToList();

                var healthList = modelMappings.Select(map =>
                {
                    sites.TryGetValue(map.SiteId, out var site);
                    var siteLogs = modelLogs
                        .Where(l => l.SiteId == map.SiteId)
                        .OrderByDescending(l => l.CheckedAt)
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
                        LastCheckedAt = latestLog?.CheckedAt,
                        LastDurationMs = latestLog?.DurationMs,
                        RecentLogs = siteLogs.Select(l => new ModelHealthLogEntry
                        {
                            Status = l.Status,
                            DurationMs = l.DurationMs,
                            CheckedAt = l.CheckedAt,
                            ErrorMessage = l.ErrorMessage
                        }).ToList(),
                        SuccessRate = totalLogs > 0 ? (double)successCount / totalLogs : 0
                    };
                })
                .OrderBy(s => s.SiteName)
                .ToList();

                HealthData[modelId] = healthList;
            }
        }
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
            StatusMessage = $"添加监控失败：{ex.Message}";
            StatusSuccess = false;
        }
        await OnGetAsync(cancellationToken);
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
            StatusMessage = $"移除监控失败：{ex.Message}";
            StatusSuccess = false;
        }
        await OnGetAsync(cancellationToken);
        return Page();
    }
}
