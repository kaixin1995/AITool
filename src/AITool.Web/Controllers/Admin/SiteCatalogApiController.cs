using System.Collections.Concurrent;
using AITool.Application.SiteCatalog;
using AITool.Domain.Models;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Admin;

// 远程模型信息，用于前端展示拉取结果
public sealed class RemoteModelInfo
{
    // 远程模型名称
    public string RemoteModelName { get; set; } = string.Empty;
    // 已有映射ID，不存在则为空
    public Guid? ExistingMappingId { get; set; }
    // 是否已启用
    public bool IsEnabled { get; set; }
    // 已有的显示别名
    public string? ExistingDisplayName { get; set; }
}

// 单个站点的拉取结果
public sealed class SiteFetchResult
{
    // 站点ID
    public Guid SiteId { get; set; }
    // 站点名称
    public string SiteName { get; set; } = string.Empty;
    // 拉取状态：pending / running / success / fail
    public string Status { get; set; } = "pending";
    // 错误信息
    public string? Error { get; set; }
    // 拉取到的模型列表
    public List<RemoteModelInfo> Models { get; set; } = [];
}

// 全局拉取进度跟踪
public sealed class FetchAllProgress
{
    // 任务ID
    public string TaskId { get; set; } = string.Empty;
    // 总站点数
    public int TotalSites { get; set; }
    // 已完成站点数
    public int CompletedSites { get; set; }
    // 是否全部完成
    public bool IsCompleted { get; set; }
    // 各站点拉取结果
    public List<SiteFetchResult> Sites { get; set; } = [];
}

// 用户勾选的单个模型导入项
public sealed class ModelSelectionItem
{
    public Guid SiteId { get; set; }
    public string RemoteModelName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Selected { get; set; }
}

// 批量导入请求
public sealed class ImportSelectedRequest
{
    public List<ModelSelectionItem> Selections { get; set; } = [];
}

// 站点模型拉取 API，分离拉取与导入流程
[ApiController]
[Route("api/admin/site-catalog")]
public sealed class SiteCatalogApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IServiceScopeFactory _scopeFactory;

    // 全局拉取进度存储
    private static readonly ConcurrentDictionary<string, FetchAllProgress> _progressStore = new();

    public SiteCatalogApiController(AppDbContext dbContext, IServiceScopeFactory scopeFactory)
    {
        _dbContext = dbContext;
        _scopeFactory = scopeFactory;
    }

    // 拉取单个站点的远程模型列表
    [HttpGet("fetch-models/{siteId}")]
    public async Task<IActionResult> FetchModels(Guid siteId, CancellationToken cancellationToken)
    {
        var site = await _dbContext.Sites.FindAsync([siteId], cancellationToken);
        if (site is null) return NotFound(new { message = "站点不存在" });

        // 使用独立作用域获取 ISiteCatalogClient
        using var scope = _scopeFactory.CreateScope();
        var catalogClient = scope.ServiceProvider.GetRequiredService<ISiteCatalogClient>();

        IReadOnlyList<string> remoteModels;
        try
        {
            remoteModels = await catalogClient.GetModelsAsync(site, cancellationToken);
        }
        catch (Exception ex)
        {
            return Ok(new List<RemoteModelInfo>()); ;
        }

        // 查询该站点已有的映射和模型库信息
        var existingMappings = await _dbContext.SiteModelMappings
            .Where(m => m.SiteId == siteId)
            .ToListAsync(cancellationToken);

        var modelNames = remoteModels.ToList();
        var modelItems = await _dbContext.ModelLibraryItems
            .Where(m => modelNames.Contains(m.ModelName))
            .ToListAsync(cancellationToken);

        var result = new List<RemoteModelInfo>();
        foreach (var remoteName in remoteModels)
        {
            var mapping = existingMappings.FirstOrDefault(m => m.RemoteModelName == remoteName);
            var modelItem = modelItems.FirstOrDefault(m => m.ModelName == remoteName);

            result.Add(new RemoteModelInfo
            {
                RemoteModelName = remoteName,
                ExistingMappingId = mapping?.Id,
                IsEnabled = mapping?.IsEnabled ?? true,
                ExistingDisplayName = modelItem?.DisplayName
            });
        }

        return Ok(result);
    }

    // 启动异步拉取全部站点模型任务
    [HttpPost("fetch-all-models")]
    public async Task<IActionResult> FetchAllModels(CancellationToken cancellationToken)
    {
        var sites = await _dbContext.Sites
            .Where(s => s.IsEnabled)
            .OrderBy(s => s.Name)
            .ToListAsync(cancellationToken);

        if (sites.Count == 0)
        {
            return Ok(new { taskId = "", message = "没有启用的站点" });
        }

        var taskId = Guid.NewGuid().ToString("N")[..8];
        var progress = new FetchAllProgress
        {
            TaskId = taskId,
            TotalSites = sites.Count,
            Sites = sites.Select(s => new SiteFetchResult
            {
                SiteId = s.Id,
                SiteName = s.Name
            }).ToList()
        };
        _progressStore[taskId] = progress;

        // 后台并发拉取各站点模型列表
        _ = Task.Run(async () =>
        {
            var tasks = sites.Select((site, index) => FetchSingleSiteModelsAsync(taskId, index, site, cancellationToken));
            await Task.WhenAll(tasks);

            if (_progressStore.TryGetValue(taskId, out var p))
            {
                p.IsCompleted = true;
                p.CompletedSites = p.Sites.Count(s => s.Status is "success" or "fail");
            }
        }, cancellationToken);

        return Ok(new { taskId });
    }

    // 查询全部站点拉取进度
    [HttpGet("fetch-all-progress/{taskId}")]
    public IActionResult GetFetchAllProgress(string taskId)
    {
        if (!_progressStore.TryGetValue(taskId, out var progress))
        {
            return NotFound(new { message = "任务不存在" });
        }

        return Ok(progress);
    }

    // 批量导入用户勾选的模型
    [HttpPost("import-selected")]
    public async Task<IActionResult> ImportSelected([FromBody] ImportSelectedRequest request, CancellationToken cancellationToken)
    {
        var importedCount = 0;

        // 预加载所有相关映射，减少数据库查询
        var allSiteIds = request.Selections.Select(s => s.SiteId).Distinct().ToList();
        var allExistingMappings = await _dbContext.SiteModelMappings
            .Where(m => allSiteIds.Contains(m.SiteId))
            .ToListAsync(cancellationToken);

        // 预加载所有涉及到的远程模型名对应的模型库条目
        var allRemoteNames = request.Selections.Where(s => s.Selected).Select(s => s.RemoteModelName).Distinct().ToList();
        var existingModelItems = await _dbContext.ModelLibraryItems
            .Where(m => allRemoteNames.Contains(m.ModelName))
            .ToDictionaryAsync(m => m.ModelName, m => m, cancellationToken);

        // 按站点分组处理
        var siteGroups = request.Selections.GroupBy(s => s.SiteId);

        foreach (var group in siteGroups)
        {
            var siteId = group.Key;
            var siteMappings = allExistingMappings.Where(m => m.SiteId == siteId).ToList();

            foreach (var item in group)
            {
                var mapping = siteMappings.FirstOrDefault(m => m.RemoteModelName == item.RemoteModelName);

                if (item.Selected)
                {
                    // 用字典查找或创建模型库条目，避免重复插入同名模型
                    if (!existingModelItems.TryGetValue(item.RemoteModelName, out var modelItem))
                    {
                        modelItem = new ModelLibraryItem
                        {
                            ModelName = item.RemoteModelName,
                            DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? item.RemoteModelName : item.DisplayName
                        };
                        _dbContext.ModelLibraryItems.Add(modelItem);
                        // 加入字典，后续同名的远程模型直接复用
                        existingModelItems[item.RemoteModelName] = modelItem;
                    }
                    else if (!string.IsNullOrWhiteSpace(item.DisplayName) && item.DisplayName != modelItem.DisplayName)
                    {
                        // 用户修改了别名，更新
                        modelItem.DisplayName = item.DisplayName;
                    }

                    if (mapping is null)
                    {
                        // 新建映射
                        _dbContext.SiteModelMappings.Add(new SiteModelMapping
                        {
                            SiteId = siteId,
                            ModelLibraryItemId = modelItem.Id,
                            RemoteModelName = item.RemoteModelName,
                            LastStatus = "imported",
                            IsEnabled = true
                        });
                    }
                    else
                    {
                        // 已有映射，确保启用
                        mapping.IsEnabled = true;
                        mapping.LastStatus = "updated";
                    }

                    importedCount++;
                }
                else if (mapping is not null)
                {
                    // 用户未勾选，禁用该映射
                    mapping.IsEnabled = false;
                    mapping.LastStatus = "disabled";
                }
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { importedCount });
    }

    // 后台拉取单个站点的模型列表
    private async Task FetchSingleSiteModelsAsync(string taskId, int siteIndex, Site site, CancellationToken cancellationToken)
    {
        if (!_progressStore.TryGetValue(taskId, out var progress)) return;

        progress.Sites[siteIndex].Status = "running";

        try
        {
            // 使用独立作用域避免 SSL 连接失败
            using var scope = _scopeFactory.CreateScope();
            var catalogClient = scope.ServiceProvider.GetRequiredService<ISiteCatalogClient>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var remoteModels = await catalogClient.GetModelsAsync(site, cancellationToken);

            // 查询已有映射
            var existingMappings = await db.SiteModelMappings
                .Where(m => m.SiteId == site.Id)
                .ToListAsync(cancellationToken);

            var modelNames = remoteModels.ToList();
            var modelItems = await db.ModelLibraryItems
                .Where(m => modelNames.Contains(m.ModelName))
                .ToListAsync(cancellationToken);

            var models = new List<RemoteModelInfo>();
            foreach (var remoteName in remoteModels)
            {
                var mapping = existingMappings.FirstOrDefault(m => m.RemoteModelName == remoteName);
                var modelItem = modelItems.FirstOrDefault(m => m.ModelName == remoteName);

                models.Add(new RemoteModelInfo
                {
                    RemoteModelName = remoteName,
                    ExistingMappingId = mapping?.Id,
                    IsEnabled = mapping?.IsEnabled ?? true,
                    ExistingDisplayName = modelItem?.DisplayName
                });
            }

            progress.Sites[siteIndex].Status = "success";
            progress.Sites[siteIndex].Models = models;
        }
        catch (Exception ex)
        {
            progress.Sites[siteIndex].Status = "fail";
            progress.Sites[siteIndex].Error = ex.Message;
        }

        progress.CompletedSites = progress.Sites.Count(s => s.Status is "success" or "fail");
    }
}
