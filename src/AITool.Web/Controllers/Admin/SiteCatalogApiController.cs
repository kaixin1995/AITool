using System.Collections.Concurrent;
using AITool.Application.SiteCatalog;
using AITool.Domain.Models;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// 远端模型信息项，表示从站点抓取到的一个模型及其导入状态。
/// </summary>
public sealed class RemoteModelInfo
{
    /// <summary>
    /// 远端模型名称。
    /// </summary>
    public string RemoteModelName { get; set; } = string.Empty;
    /// <summary>
    /// 已有映射标识。
    /// </summary>
    public Guid? ExistingMappingId { get; set; }
    /// <summary>
    /// 是否启用。
    /// </summary>
    public bool IsEnabled { get; set; }
    /// <summary>
    /// 已导入的显示名称。
    /// </summary>
    public string? ExistingDisplayName { get; set; }
}

/// <summary>
/// 单个站点的抓取结果，包含站点信息和抓取到的模型列表。
/// </summary>
public sealed class SiteFetchResult
{
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; set; }
    /// <summary>
    /// 站点名称。
    /// </summary>
    public string SiteName { get; set; } = string.Empty;
    /// <summary>
    /// 抓取状态。
    /// </summary>
    public string Status { get; set; } = "pending";
    /// <summary>
    /// 错误信息。
    /// </summary>
    public string? Error { get; set; }
    /// <summary>
    /// 模型列表。
    /// </summary>
    public List<RemoteModelInfo> Models { get; set; } = [];
}

/// <summary>
/// 批量抓取进度，用于前端轮询展示所有站点的抓取状态和结果。
/// </summary>
public sealed class FetchAllProgress
{
    /// <summary>
    /// 任务标识。
    /// </summary>
    public string TaskId { get; set; } = string.Empty;
    /// <summary>
    /// 站点总数。
    /// </summary>
    public int TotalSites { get; set; }
    /// <summary>
    /// 已完成站点数。
    /// </summary>
    public int CompletedSites { get; set; }
    /// <summary>
    /// 是否已完成。
    /// </summary>
    public bool IsCompleted { get; set; }
    /// <summary>
    /// 各站点抓取进度。
    /// </summary>
    public List<SiteFetchResult> Sites { get; set; } = [];
}

/// <summary>
/// 模型导入选择项，用于前端勾选要导入或取消导入的远端模型。
/// </summary>
public sealed class ModelSelectionItem
{
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; set; }
    /// <summary>
    /// 远端模型名称。
    /// </summary>
    public string RemoteModelName { get; set; } = string.Empty;
    /// <summary>
    /// 显示名称。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;
    /// <summary>
    /// 是否选中。
    /// </summary>
    public bool Selected { get; set; }
}

/// <summary>
/// 导入选中模型的请求参数，包含前端勾选的所有模型选择项。
/// </summary>
public sealed class ImportSelectedRequest
{
    /// <summary>
    /// 选中的模型项。
    /// </summary>
    public List<ModelSelectionItem> Selections { get; set; } = [];
}

/// <summary>
/// 站点目录管理控制器，提供远端模型抓取、批量抓取进度查询和模型导入功能。
/// </summary>
[ApiController]
[Route("api/admin/site-catalog")]
public sealed class SiteCatalogApiController : ControllerBase
{
    /// <summary>
    /// 数据库上下文。
    /// </summary>
    private readonly AppDbContext _dbContext;
    /// <summary>
    /// 服务作用域工厂。
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;
    /// <summary>
    /// 代理元数据缓存。
    /// </summary>
    private readonly ProxyRequestMetadataCache _metadataCache;

    /// <summary>
    /// 批量抓取进度缓存。
    /// </summary>
    private static readonly ConcurrentDictionary<string, FetchAllProgress> ProgressStore = new();

    /// <summary>
    /// 创建站点目录控制器。
    /// </summary>
    public SiteCatalogApiController(AppDbContext dbContext, IServiceScopeFactory scopeFactory, ProxyRequestMetadataCache metadataCache)
    {
        _dbContext = dbContext;
        _scopeFactory = scopeFactory;
        _metadataCache = metadataCache;
    }

    /// <summary>
    /// 抓取单个站点的模型列表。
    /// </summary>
    [HttpGet("fetch-models/{siteId}")]
    public async Task<IActionResult> FetchModels(Guid siteId, CancellationToken cancellationToken)
    {
        var site = await _dbContext.Sites.InSingleAsync(siteId);
        if (site is null)
        {
            return NotFound(new { message = "站点不存在" });
        }

        using var scope = _scopeFactory.CreateScope();
        var catalogClient = scope.ServiceProvider.GetRequiredService<ISiteCatalogClient>();

        IReadOnlyList<string> remoteModels;
        try
        {
            remoteModels = await catalogClient.GetModelsAsync(site, cancellationToken);
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                success = false,
                message = ex.Message
            });
        }

        var existingMappings = await _dbContext.SiteModelMappings
            .Where(m => m.SiteId == siteId)
            .ToListAsync(cancellationToken);

        var modelNames = remoteModels.ToList();
        // 模型库的 ModelName 可能是自定义对外名（不等于远端名），需同时通过映射引用的 ModelLibraryItemId 加载。
        var mappingModelIds = existingMappings.Where(m => m.ModelLibraryItemId != Guid.Empty).Select(m => m.ModelLibraryItemId).Distinct().ToList();
        var modelItems = await _dbContext.ModelLibraryItems
            .Where(m => modelNames.Contains(m.ModelName) || mappingModelIds.Contains(m.Id))
            .ToListAsync(cancellationToken);

        var result = new List<RemoteModelInfo>();
        foreach (var remoteName in remoteModels)
        {
            var mapping = existingMappings.FirstOrDefault(m => m.RemoteModelName == remoteName);
            // 模型库的 ModelName 可能是用户自定义的对外名（不等于 remoteName），
            // 需要通过映射的 ModelLibraryItemId 查找对应的模型库记录。
            var modelItem = mapping is not null
                ? modelItems.FirstOrDefault(m => m.Id == mapping.ModelLibraryItemId)
                : modelItems.FirstOrDefault(m => m.ModelName == remoteName);
            var hasValidImport = mapping is not null && modelItem is not null && mapping.ModelLibraryItemId == modelItem.Id;

            result.Add(new RemoteModelInfo
            {
                RemoteModelName = remoteName,
                ExistingMappingId = hasValidImport ? mapping!.Id : null,
                IsEnabled = hasValidImport && mapping!.IsEnabled,
                // 返回模型库的对外名（可能是自定义名），供前端预填到输入框
                ExistingDisplayName = modelItem?.ModelName != remoteName ? modelItem?.ModelName : null
            });
        }

        return Ok(result);
    }

    /// <summary>
    /// 批量抓取所有启用站点的模型列表。
    /// </summary>
    [HttpPost("fetch-all-models")]
    public async Task<IActionResult> FetchAllModels(CancellationToken cancellationToken)
    {
        // 仅拉取用户自建站点；Codex 等托管 Site 的 baseUrl 不兼容 OpenAI catalog 接口，跳过避免误报。
        var sites = await _dbContext.Sites
            .Where(s => s.IsEnabled && string.IsNullOrEmpty(s.ManagedSource))
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
        ProgressStore[taskId] = progress;

        _ = Task.Run(async () =>
        {
            var tasks = sites.Select((site, index) => FetchSingleSiteModelsAsync(taskId, index, site, cancellationToken));
            await Task.WhenAll(tasks);

            if (ProgressStore.TryGetValue(taskId, out var current))
            {
                current.IsCompleted = true;
                current.CompletedSites = current.Sites.Count(s => s.Status is "success" or "fail");
            }
        }, cancellationToken);

        return Ok(new { taskId });
    }

    /// <summary>
    /// 获取批量抓取进度。
    /// </summary>
    [HttpGet("fetch-all-progress/{taskId}")]
    public IActionResult GetFetchAllProgress(string taskId)
    {
        if (!ProgressStore.TryGetValue(taskId, out var progress))
        {
            return NotFound(new { message = "任务不存在" });
        }

        return Ok(progress);
    }

    /// <summary>
    /// 导入选中的模型。
    /// </summary>
    [HttpPost("import-selected")]
    public async Task<IActionResult> ImportSelected([FromBody] ImportSelectedRequest request, CancellationToken cancellationToken)
    {
        if (request.Selections.Count == 0)
        {
            return BadRequest(new { message = "未收到任何模型选择数据" });
        }

        if (!request.Selections.Any(x => x.Selected))
        {
            return BadRequest(new { message = "请至少选择一个模型" });
        }

        var importedCount = 0;
        var allSiteIds = request.Selections.Select(s => s.SiteId).Distinct().ToList();
        var allExistingMappings = await _dbContext.SiteModelMappings
            .Where(m => allSiteIds.Contains(m.SiteId))
            .ToListAsync(cancellationToken);

        // 用户可自定义对外模型名（displayName），自定义名不为空且与原始名不同时用它作为 ModelLibraryItem.ModelName。
        // 没有自定义名时回退到原始名。模型库按"对外名"查重（而非原始名），确保同一个自定义名只创建一条模型库记录。
        var allPublicNames = request.Selections
            .Where(s => s.Selected)
            .Select(s => string.IsNullOrWhiteSpace(s.DisplayName) ? s.RemoteModelName : s.DisplayName.Trim())
            .Distinct()
            .ToList();
        var existingModelItems = await _dbContext.ModelLibraryItems
            .Where(m => allPublicNames.Contains(m.ModelName))
            .ToDictionaryAsync(m => m.ModelName, m => m, cancellationToken);

        var siteGroups = request.Selections.GroupBy(s => s.SiteId);
        foreach (var group in siteGroups)
        {
            var siteId = group.Key;
            var siteMappings = allExistingMappings.Where(m => m.SiteId == siteId).ToList();

            foreach (var item in group)
            {
                // 对外名：用户自定义优先，否则用原始名
                var publicName = string.IsNullOrWhiteSpace(item.DisplayName)
                    ? item.RemoteModelName
                    : item.DisplayName.Trim();
                var mapping = siteMappings.FirstOrDefault(m => m.RemoteModelName == item.RemoteModelName);

                if (item.Selected)
                {
                    if (!existingModelItems.TryGetValue(publicName, out var modelItem))
                    {
                        modelItem = new ModelLibraryItem
                        {
                            ModelName = publicName,
                            DisplayName = publicName
                        };
                        _dbContext.ModelLibraryItems.Add(modelItem);
                        existingModelItems[publicName] = modelItem;
                    }

                    if (mapping is null)
                    {
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
                        // 重新导入时同步修复旧映射指向，避免模型库记录已删但站点映射仍指向失效模型。
                        mapping.ModelLibraryItemId = modelItem.Id;
                        mapping.IsEnabled = true;
                        mapping.LastStatus = "updated";
                        await _dbContext.UpdateAsync(mapping, cancellationToken);
                    }

                    importedCount++;
                }
                else if (mapping is not null)
                {
                    mapping.IsEnabled = false;
                    mapping.LastStatus = "disabled";
                    await _dbContext.UpdateAsync(mapping, cancellationToken);
                }
            }
        }

        _metadataCache.InvalidateModelMetadata();
        _metadataCache.InvalidateRouteTargets();
        return Ok(new { importedCount });
    }

    /// <summary>
    /// 抓取单个站点的模型并更新进度。
    /// </summary>
    private async Task FetchSingleSiteModelsAsync(string taskId, int siteIndex, Site site, CancellationToken cancellationToken)
    {
        if (!ProgressStore.TryGetValue(taskId, out var progress))
        {
            return;
        }

        progress.Sites[siteIndex].Status = "running";

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var catalogClient = scope.ServiceProvider.GetRequiredService<ISiteCatalogClient>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var remoteModels = await catalogClient.GetModelsAsync(site, cancellationToken);
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
                var hasValidImport = mapping is not null && modelItem is not null && mapping.ModelLibraryItemId == modelItem.Id;

                models.Add(new RemoteModelInfo
                {
                    RemoteModelName = remoteName,
                    ExistingMappingId = hasValidImport ? mapping!.Id : null,
                    IsEnabled = hasValidImport && mapping!.IsEnabled,
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
