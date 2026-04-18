using System.Collections.Concurrent;
using AITool.Application.SiteCatalog;
using AITool.Domain.Models;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Admin;

// 单个站点的拉取进度
public sealed class SitePullStatus
{
    // 站点名称
    public string SiteName { get; set; } = string.Empty;
    // 拉取状态：pending / running / success / fail
    public string Status { get; set; } = "pending";
    // 导入模型数量
    public int ImportedCount { get; set; }
    // 错误信息
    public string? Error { get; set; }
}

// 全局拉取任务的进度跟踪
public sealed class PullAllProgress
{
    // 任务ID
    public string TaskId { get; set; } = string.Empty;
    // 总站点数
    public int TotalSites { get; set; }
    // 已完成站点数
    public int CompletedSites { get; set; }
    // 是否完成
    public bool IsCompleted { get; set; }
    // 各站点进度明细
    public List<SitePullStatus> Sites { get; set; } = [];
    // 总导入模型数
    public int TotalImported { get; set; }
    // 失败站点数
    public int FailCount { get; set; }
}

// 站点模型拉取 API，提供异步并发拉取与实时进度查询
[ApiController]
[Route("api/admin/site-catalog")]
public sealed class SiteCatalogApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ISiteCatalogClient _catalogClient;

    // 全局进度存储，按任务ID索引
    private static readonly ConcurrentDictionary<string, PullAllProgress> _progressStore = new();

    public SiteCatalogApiController(AppDbContext dbContext, ISiteCatalogClient catalogClient)
    {
        _dbContext = dbContext;
        _catalogClient = catalogClient;
    }

    // 启动异步并发拉取全部站点任务
    [HttpPost("pull-all")]
    public async Task<IActionResult> PullAll(CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites
            .Where(s => s.IsEnabled)
            .OrderBy(s => s.Name)
            .ToListAsync(cancellationToken);

        if (sites.Count == 0)
        {
            return Ok(new { taskId = "", message = "没有启用的站点" });
        }

        // 初始化进度
        var taskId = Guid.NewGuid().ToString("N")[..8];
        var progress = new PullAllProgress
        {
            TaskId = taskId,
            TotalSites = sites.Count,
            Sites = sites.Select(s => new SitePullStatus { SiteName = s.Name }).ToList()
        };
        _progressStore[taskId] = progress;

        // 后台异步并发执行拉取
        _ = Task.Run(async () =>
        {
            var tasks = sites.Select((site, index) => PullSingleSiteAsync(taskId, index, site, cancellationToken));
            await Task.WhenAll(tasks);

            // 标记全部完成
            if (_progressStore.TryGetValue(taskId, out var p))
            {
                p.IsCompleted = true;
                p.CompletedSites = p.Sites.Count(s => s.Status is "success" or "fail");
                p.TotalImported = p.Sites.Sum(s => s.ImportedCount);
                p.FailCount = p.Sites.Count(s => s.Status == "fail");
            }
        }, cancellationToken);

        return Ok(new { taskId });
    }

    // 查询拉取任务进度
    [HttpGet("pull-progress/{taskId}")]
    public IActionResult GetProgress(string taskId)
    {
        if (!_progressStore.TryGetValue(taskId, out var progress))
        {
            return NotFound(new { message = "任务不存在" });
        }

        return Ok(progress);
    }

    // 并发拉取单个站点并更新进度
    private async Task PullSingleSiteAsync(string taskId, int siteIndex, Site site, CancellationToken cancellationToken)
    {
        if (!_progressStore.TryGetValue(taskId, out var progress)) return;

        // 标记开始
        progress.Sites[siteIndex].Status = "running";

        try
        {
            var remoteModels = await _catalogClient.GetModelsAsync(site, cancellationToken);

            // 使用独立的 DbContext 实例处理并发写入
            using var scope = HttpContext.RequestServices.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var existingMappings = await db.SiteModelMappings
                .Where(m => m.SiteId == site.Id)
                .ToListAsync(cancellationToken);

            var importedCount = 0;
            foreach (var remoteName in remoteModels)
            {
                if (existingMappings.Any(m => m.RemoteModelName == remoteName)) continue;

                var modelItem = await db.ModelLibraryItems
                    .FirstOrDefaultAsync(m => m.ModelName == remoteName, cancellationToken);

                if (modelItem is null)
                {
                    modelItem = new Domain.Models.ModelLibraryItem
                    {
                        ModelName = remoteName,
                        DisplayName = remoteName
                    };
                    db.ModelLibraryItems.Add(modelItem);
                }

                db.SiteModelMappings.Add(new SiteModelMapping
                {
                    SiteId = site.Id,
                    ModelLibraryItemId = modelItem.Id,
                    RemoteModelName = remoteName,
                    LastStatus = "imported"
                });

                importedCount++;
            }

            await db.SaveChangesAsync(cancellationToken);

            progress.Sites[siteIndex].Status = "success";
            progress.Sites[siteIndex].ImportedCount = importedCount;
        }
        catch (Exception ex)
        {
            progress.Sites[siteIndex].Status = "fail";
            progress.Sites[siteIndex].Error = ex.Message;
        }

        // 更新已完成计数
        progress.CompletedSites = progress.Sites.Count(s => s.Status is "success" or "fail");
    }
}
