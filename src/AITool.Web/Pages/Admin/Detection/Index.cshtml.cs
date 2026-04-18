using AITool.Application.Detection;
using AITool.Domain.SiteCatalog;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Pages.Admin.Detection;

// 检测列表视图模型，包含每条映射的最新检测状态
public class DetectionMappingViewModel
{
    public Guid MappingId { get; set; }
    public string SiteName { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string LastStatus { get; set; } = string.Empty;
    public DateTimeOffset? LastCheckedAt { get; set; }
    public int? LastDurationMs { get; set; }
}

// 模型检测页面模型，提供检测触发与结果展示
public class IndexModel : PageModel
{
    private readonly AppDbContext _dbContext;
    private readonly IModelProbeService _probeService;

    public IndexModel(AppDbContext dbContext, IModelProbeService probeService)
    {
        _dbContext = dbContext;
        _probeService = probeService;
    }

    // 站点模型映射列表及最新检测状态
    public List<DetectionMappingViewModel> Mappings { get; set; } = [];

    // 检测结果提示信息
    public string? ProbeMessage { get; set; }
    public bool ProbeSuccess { get; set; }

    // 加载所有映射及其最新检测状态
    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        // 先加载全部检测日志，客户端分组取最新记录（SQLite 不支持 DateTimeOffset 的 ORDER BY）
        var allLogs = await _dbContext.DetectionLogs.ToListAsync(cancellationToken);
        var latestLogs = allLogs
            .GroupBy(d => (d.SiteId, d.ModelLibraryItemId))
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(d => d.CheckedAt).First());

        var mappings = await _dbContext.SiteModelMappings
            .ToListAsync(cancellationToken);

        // 加载站点和模型信息用于展示
        var siteIds = mappings.Select(m => m.SiteId).Distinct().ToList();
        var sites = await _dbContext.Sites
            .Where(s => siteIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => s, cancellationToken);

        Mappings = mappings.Select(m =>
        {
            latestLogs.TryGetValue((m.SiteId, m.ModelLibraryItemId), out var log);
            sites.TryGetValue(m.SiteId, out var site);
            return new DetectionMappingViewModel
            {
                MappingId = m.Id,
                SiteName = site?.Name ?? "(未知站点)",
                ModelName = m.RemoteModelName,
                LastStatus = log?.Status ?? m.LastStatus,
                LastCheckedAt = log?.CheckedAt,
                LastDurationMs = log?.DurationMs
            };
        }).ToList();
    }

    // 对指定映射执行模型可用性检测
    public async Task<IActionResult> OnPostProbeAsync(Guid mappingId, CancellationToken cancellationToken)
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

        await OnGetAsync(cancellationToken);
        return Page();
    }
}
