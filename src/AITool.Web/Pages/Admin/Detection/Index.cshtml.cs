using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.Detection;

// 按模型分组的检测视图模型
public class DetectionModelGroupViewModel
{
    public Guid ModelLibraryItemId { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<DetectionSiteStatusViewModel> Sites { get; set; } = [];
}

// 单个站点在某个模型上的检测状态
public class DetectionSiteStatusViewModel
{
    public Guid MappingId { get; set; }
    public string SiteName { get; set; } = string.Empty;
    public string RemoteModelName { get; set; } = string.Empty;
    public string LastStatus { get; set; } = string.Empty;
    public DateTimeOffset? LastCheckedAt { get; set; }
    public int? LastDurationMs { get; set; }
}

// 搜索过滤用的模型项
public class DetectionFilterModelItem
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

// 模型检测页面模型，按模型分组展示各站点检测状态
public class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;

    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // 按模型分组的检测数据
    public List<DetectionModelGroupViewModel> ModelGroups { get; set; } = [];

    // 用于搜索过滤的模型下拉列表
    public List<DetectionFilterModelItem> FilterModels { get; set; } = [];

    // 加载所有映射按模型分组展示
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var allLogs = await _dbContext.ProxyUsageLogs
            .Where(x => x.IsFinalResult)
            .ToListAsync(cancellationToken);
        var latestLogs = allLogs
            .GroupBy(d => (d.TargetSiteId, d.RequestModel))
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(d => d.RequestedAt).First());

        var mappings = await _dbContext.SiteModelMappings.ToListAsync(cancellationToken);
        var modelIds = mappings.Select(m => m.ModelLibraryItemId).Distinct().ToList();
        var models = await _dbContext.ModelLibraryItems
            .Where(m => modelIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m, cancellationToken);

        var siteIds = mappings.Select(m => m.SiteId).Distinct().ToList();
        var sites = await _dbContext.Sites
            .Where(s => siteIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s, cancellationToken);

        ModelGroups = mappings
            .GroupBy(m => m.ModelLibraryItemId)
            .Select(g =>
            {
                models.TryGetValue(g.Key, out var model);
                return new DetectionModelGroupViewModel
                {
                    ModelLibraryItemId = g.Key,
                    ModelName = model?.ModelName ?? "(未知模型)",
                    DisplayName = model?.DisplayName ?? "(未知模型)",
                    Sites = g.Select(m =>
                    {
                        latestLogs.TryGetValue((m.SiteId, model?.ModelName ?? string.Empty), out var log);
                        sites.TryGetValue(m.SiteId, out var site);
                        return new DetectionSiteStatusViewModel
                        {
                            MappingId = m.Id,
                            SiteName = site?.Name ?? "(未知站点)",
                            RemoteModelName = m.RemoteModelName,
                            LastStatus = log?.Status ?? m.LastStatus,
                            LastCheckedAt = log?.RequestedAt,
                            LastDurationMs = log?.TotalDurationMs
                        };
                    }).OrderBy(s => s.SiteName).ToList()
                };
            })
            .OrderBy(g => g.DisplayName)
            .ToList();

        FilterModels = ModelGroups
            .Select(g => new DetectionFilterModelItem
            {
                Id = g.ModelLibraryItemId,
                DisplayName = g.DisplayName
            })
            .OrderBy(m => m.DisplayName)
            .ToList();
    }
}
