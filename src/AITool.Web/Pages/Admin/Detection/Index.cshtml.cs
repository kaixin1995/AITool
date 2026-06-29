using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AITool.Web.Pages.Admin.Detection;

/// <summary>
/// 检测页中的模型分组。
/// </summary>
public class DetectionModelGroupViewModel
{
    /// <summary>
    /// 模型库项标识。
    /// </summary>
    public Guid ModelLibraryItemId { get; set; }
    /// <summary>
    /// 模型名称。
    /// </summary>
    public string ModelName { get; set; } = string.Empty;
    /// <summary>
    /// 显示名称。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>
    /// 站点列表。
    /// </summary>
    public List<DetectionSiteStatusViewModel> Sites { get; set; } = [];
}

/// <summary>
/// 检测页中的站点状态。
/// </summary>
public class DetectionSiteStatusViewModel
{
    /// <summary>
    /// 关联标识。
    /// </summary>
    public Guid MappingId { get; set; }
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
}

/// <summary>
/// 检测页的模型筛选项。
/// </summary>
public class DetectionFilterModelItem
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
/// 模型检测页面模型。
/// </summary>
public class IndexModel : PageModel
{
    /// <summary>
    /// 数据库上下文。
    /// </summary>
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 模型检测页面模型。
    /// </summary>
    public IndexModel(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    /// <summary>
    /// 模型分组列表。
    /// </summary>
    public List<DetectionModelGroupViewModel> ModelGroups { get; set; } = [];

    /// <summary>
    /// 模型筛选项。
    /// </summary>
    public List<DetectionFilterModelItem> FilterModels { get; set; } = [];

    /// <summary>
    /// 处理页面加载请求。
    /// </summary>
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

        var models = await _dbContext.ModelLibraryItems
            .ToDictionaryAsync(m => m.Id, m => m, cancellationToken);
        var sites = await _dbContext.Sites
            .Where(s => s.IsEnabled)
            .ToDictionaryAsync(s => s.Id, s => s, cancellationToken);
        var mappings = await _dbContext.SiteModelMappings
            .Where(m => m.IsEnabled)
            .ToListAsync(cancellationToken);

        ModelGroups = mappings
            .Where(m => models.ContainsKey(m.ModelLibraryItemId) && sites.ContainsKey(m.SiteId))
            .GroupBy(m => m.ModelLibraryItemId)
            .Select(g =>
            {
                var model = models[g.Key];
                return new DetectionModelGroupViewModel
                {
                    ModelLibraryItemId = g.Key,
                    ModelName = model.ModelName,
                    DisplayName = model.DisplayName,
                    Sites = g.Select(m =>
                    {
                        latestLogs.TryGetValue((m.SiteId, model.ModelName), out var log);
                        var site = sites[m.SiteId];
                        return new DetectionSiteStatusViewModel
                        {
                            MappingId = m.Id,
                            SiteName = site.Name,
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
