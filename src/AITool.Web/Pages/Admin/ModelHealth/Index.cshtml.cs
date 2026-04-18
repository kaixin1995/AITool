using AITool.Infrastructure.Persistence;
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

// 模型下拉选项
public class ModelSelectItem
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
}

// 模型健康看板页面模型
public class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // 所有可选模型列表（用于下拉框）
    public List<ModelSelectItem> Models { get; set; } = [];

    // 当前选中的模型 ID
    public Guid? SelectedModelId { get; set; }

    // 当前模型的显示名称
    public string SelectedModelName { get; set; } = string.Empty;

    // 当前模型在各站点的健康数据
    public List<ModelHealthSiteViewModel> SiteHealths { get; set; } = [];

    // 加载模型列表和选中模型的健康数据
    public async Task OnGetAsync(Guid? modelId, CancellationToken cancellationToken)
    {
        // 加载所有模型供下拉框选择
        Models = await _dbContext.ModelLibraryItems
            .OrderBy(m => m.DisplayName)
            .Select(m => new ModelSelectItem
            {
                Id = m.Id,
                DisplayName = m.DisplayName,
                ModelName = m.ModelName
            })
            .ToListAsync(cancellationToken);

        SelectedModelId = modelId;
        if (modelId is null || modelId == Guid.Empty) return;

        // 获取选中模型的信息
        var selectedModel = await _dbContext.ModelLibraryItems
            .FirstOrDefaultAsync(m => m.Id == modelId, cancellationToken);
        if (selectedModel is null) return;
        SelectedModelName = selectedModel.DisplayName;

        // 加载该模型的所有站点映射
        var mappings = await _dbContext.SiteModelMappings
            .Where(m => m.ModelLibraryItemId == modelId)
            .ToListAsync(cancellationToken);

        // 批量加载站点信息
        var siteIds = mappings.Select(m => m.SiteId).Distinct().ToList();
        var sites = await _dbContext.Sites
            .Where(s => siteIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s, cancellationToken);

        // 加载该模型最近的检测日志（每个站点最多取 20 条）
        var recentCutoff = DateTimeOffset.UtcNow.AddDays(-7);
        var allLogs = await _dbContext.DetectionLogs
            .Where(d => d.ModelLibraryItemId == modelId)
            .ToListAsync(cancellationToken);
        var recentLogs = allLogs
            .Where(d => d.CheckedAt >= recentCutoff)
            .ToList();

        // 按站点分组构建健康视图
        SiteHealths = mappings.Select(m =>
        {
            sites.TryGetValue(m.SiteId, out var site);
            var siteLogs = recentLogs
                .Where(l => l.SiteId == m.SiteId)
                .OrderByDescending(l => l.CheckedAt)
                .Take(20)
                .ToList();

            var latestLog = siteLogs.FirstOrDefault();
            var successCount = siteLogs.Count(l => l.Status == "success");
            var totalLogs = siteLogs.Count;

            return new ModelHealthSiteViewModel
            {
                SiteName = site?.Name ?? "(未知站点)",
                RemoteModelName = m.RemoteModelName,
                LastStatus = latestLog?.Status ?? m.LastStatus,
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
    }
}
