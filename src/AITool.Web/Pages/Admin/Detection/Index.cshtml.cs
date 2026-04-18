using AITool.Application.Detection;
using AITool.Domain.SiteCatalog;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.Detection;

// 按模型分组的检测视图模型，每个模型下展示各站点的检测状态
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

// 模型检测页面模型，按模型分组展示各站点检测状态
public class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly IModelProbeService _probeService;

    public IndexModel(AppDbContext dbContext, IModelProbeService probeService)
    {
        _dbContext = dbContext;
        _probeService = probeService;
    }

    // 按模型分组的检测数据
    public List<DetectionModelGroupViewModel> ModelGroups { get; set; } = [];

    // 检测结果提示信息
    public string? ProbeMessage { get; set; }
    public bool ProbeSuccess { get; set; }

    // 加载所有映射按模型分组展示
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        // 加载全部检测日志，客户端分组取最新记录
        var allLogs = await _dbContext.DetectionLogs.ToListAsync(cancellationToken);
        var latestLogs = allLogs
            .GroupBy(d => (d.SiteId, d.ModelLibraryItemId))
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(d => d.CheckedAt).First());

        var mappings = await _dbContext.SiteModelMappings.ToListAsync(cancellationToken);
        var modelIds = mappings.Select(m => m.ModelLibraryItemId).Distinct().ToList();
        var models = await _dbContext.ModelLibraryItems
            .Where(m => modelIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m, cancellationToken);

        var siteIds = mappings.Select(m => m.SiteId).Distinct().ToList();
        var sites = await _dbContext.Sites
            .Where(s => siteIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s, cancellationToken);

        // 按模型分组构建视图数据
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
                        latestLogs.TryGetValue((m.SiteId, m.ModelLibraryItemId), out var log);
                        sites.TryGetValue(m.SiteId, out var site);
                        return new DetectionSiteStatusViewModel
                        {
                            MappingId = m.Id,
                            SiteName = site?.Name ?? "(未知站点)",
                            RemoteModelName = m.RemoteModelName,
                            LastStatus = log?.Status ?? m.LastStatus,
                            LastCheckedAt = log?.CheckedAt,
                            LastDurationMs = log?.DurationMs
                        };
                    }).OrderBy(s => s.SiteName).ToList()
                };
            })
            .OrderBy(g => g.DisplayName)
            .ToList();
    }

    // 对指定映射执行模型可用性检测
    public async Task<IActionResult> OnPostProbeAsync(Guid mappingId, CancellationToken cancellationToken)
    {
        try
        {
            var mapping = await _dbContext.SiteModelMappings.FindAsync([mappingId], cancellationToken);
            if (mapping is null) return RedirectToPage();

            var site = await _dbContext.Sites.FindAsync([mapping.SiteId], cancellationToken);
            var model = await _dbContext.ModelLibraryItems.FindAsync([mapping.ModelLibraryItemId], cancellationToken);

            if (site is null || model is null) return RedirectToPage();

            var result = await _probeService.ProbeAsync(site, model, cancellationToken);

            // 记录检测日志
            var log = new Domain.Detection.DetectionLog
            {
                SiteId = mapping.SiteId,
                ModelLibraryItemId = mapping.ModelLibraryItemId,
                Status = result.Success ? "success" : "fail",
                DurationMs = result.DurationMs,
                ErrorMessage = result.ErrorMessage,
                CheckedAt = DateTimeOffset.UtcNow
            };
            _dbContext.DetectionLogs.Add(log);

            // 更新映射最近状态
            mapping.LastStatus = result.Success ? "success" : "fail";
            await _dbContext.SaveChangesAsync(cancellationToken);

            ProbeSuccess = result.Success;
            ProbeMessage = result.Success
                ? $"检测成功，耗时 {result.DurationMs} ms"
                : $"检测失败：{result.ErrorMessage}";
        }
        catch (Exception ex)
        {
            ProbeMessage = $"检测异常：{ex.Message}";
            ProbeSuccess = false;
        }
        await OnGetAsync(cancellationToken);
        return Page();
    }

    // 检测指定模型在所有站点上的映射
    public async Task<IActionResult> OnPostProbeModelAsync(Guid modelId, CancellationToken cancellationToken)
    {
        try
        {
            var mappings = await _dbContext.SiteModelMappings
                .Where(m => m.ModelLibraryItemId == modelId)
                .ToListAsync(cancellationToken);

            var successCount = 0;
            var failCount = 0;

            foreach (var mapping in mappings)
            {
                var site = await _dbContext.Sites.FindAsync([mapping.SiteId], cancellationToken);
                var model = await _dbContext.ModelLibraryItems.FindAsync([mapping.ModelLibraryItemId], cancellationToken);
                if (site is null || model is null) continue;

                var result = await _probeService.ProbeAsync(site, model, cancellationToken);

                var log = new Domain.Detection.DetectionLog
                {
                    SiteId = mapping.SiteId,
                    ModelLibraryItemId = mapping.ModelLibraryItemId,
                    Status = result.Success ? "success" : "fail",
                    DurationMs = result.DurationMs,
                    ErrorMessage = result.ErrorMessage,
                    CheckedAt = DateTimeOffset.UtcNow
                };
                _dbContext.DetectionLogs.Add(log);
                mapping.LastStatus = result.Success ? "success" : "fail";

                if (result.Success) successCount++;
                else failCount++;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            ProbeSuccess = failCount == 0;
            ProbeMessage = $"模型检测完成：{successCount} 成功，{failCount} 失败";
        }
        catch (Exception ex)
        {
            ProbeMessage = $"检测异常：{ex.Message}";
            ProbeSuccess = false;
        }
        await OnGetAsync(cancellationToken);
        return Page();
    }

    // 检测所有模型在所有站点上的映射
    public async Task<IActionResult> OnPostProbeAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            var mappings = await _dbContext.SiteModelMappings.ToListAsync(cancellationToken);
            var successCount = 0;
            var failCount = 0;

            foreach (var mapping in mappings)
            {
                var site = await _dbContext.Sites.FindAsync([mapping.SiteId], cancellationToken);
                var model = await _dbContext.ModelLibraryItems.FindAsync([mapping.ModelLibraryItemId], cancellationToken);
                if (site is null || model is null) continue;

                var result = await _probeService.ProbeAsync(site, model, cancellationToken);

                var log = new Domain.Detection.DetectionLog
                {
                    SiteId = mapping.SiteId,
                    ModelLibraryItemId = mapping.ModelLibraryItemId,
                    Status = result.Success ? "success" : "fail",
                    DurationMs = result.DurationMs,
                    ErrorMessage = result.ErrorMessage,
                    CheckedAt = DateTimeOffset.UtcNow
                };
                _dbContext.DetectionLogs.Add(log);
                mapping.LastStatus = result.Success ? "success" : "fail";

                if (result.Success) successCount++;
                else failCount++;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            ProbeSuccess = failCount == 0;
            ProbeMessage = $"全部检测完成：{successCount} 成功，{failCount} 失败";
        }
        catch (Exception ex)
        {
            ProbeMessage = $"检测异常：{ex.Message}";
            ProbeSuccess = false;
        }
        await OnGetAsync(cancellationToken);
        return Page();
    }
}
