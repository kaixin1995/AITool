using AITool.Admin.Services;
using AITool.Domain.Models;
using AITool.Domain.SiteCatalog;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Admin.Controllers.Admin;

/// <summary>
/// 模型管理控制器，提供模型相关数据的批量清空操作。
/// </summary>
[ApiController]
[Route("api/admin/models")]
public sealed class ModelsApiController : ControllerBase
{
    /// <summary>
    /// 数据库上下文。
    /// </summary>
    private readonly AppDbContext _dbContext;
    /// <summary>
    /// 后台缓存失效服务。
    /// </summary>
    private readonly AdminCacheInvalidationService _cacheInvalidationService;

    /// <summary>
    /// 创建模型管理控制器。
    /// </summary>
    public ModelsApiController(
        AppDbContext dbContext,
        AdminCacheInvalidationService cacheInvalidationService)
    {
        _dbContext = dbContext;
        _cacheInvalidationService = cacheInvalidationService;
    }

    /// <summary>
    /// 清空模型相关数据。
    /// </summary>
    [HttpPost("clear-all")]
    public async Task<IActionResult> ClearAll(CancellationToken cancellationToken)
    {
        // 按依赖顺序删除：映射 → 监控 → 模型
        var mappingCount = _dbContext.SiteModelMappings.Count();
        var monitorCount = _dbContext.ModelHealthMonitors.Count();
        var modelCount = _dbContext.ModelLibraryItems.Count();

        await _dbContext.Client.Deleteable<SiteModelMapping>().ExecuteCommandAsync(cancellationToken);
        await _dbContext.Client.Deleteable<ModelHealthMonitor>().ExecuteCommandAsync(cancellationToken);
        await _dbContext.Client.Deleteable<ModelLibraryItem>().ExecuteCommandAsync(cancellationToken);

        await _cacheInvalidationService.InvalidateModelMetadataAsync(cancellationToken);
        await _cacheInvalidationService.InvalidateRouteTargetsAsync(cancellationToken);

        return Ok(new
        {
            deletedModels = modelCount,
            deletedMappings = mappingCount,
            deletedMonitors = monitorCount
        });
    }

    /// <summary>
    /// 更新站点模型映射的最大并发数。
    /// </summary>
    [HttpPut("mappings/{mappingId:guid}/concurrency")]
    public async Task<IActionResult> UpdateConcurrency(
        Guid mappingId,
        [FromBody] UpdateConcurrencyRequest request,
        CancellationToken cancellationToken)
    {
        var mapping = await _dbContext.SiteModelMappings.InSingleAsync(mappingId);
        if (mapping is null)
        {
            return NotFound(new { message = "站点模型映射不存在" });
        }

        mapping.MaxConcurrency = Math.Max(0, request.MaxConcurrency);
        await _dbContext.UpdateAsync(mapping, cancellationToken);

        // 并发限制属于 SiteModelMappings，同步 ModelMetadata 将 MaxConcurrency 推送到 Core。
        await _cacheInvalidationService.InvalidateModelMetadataAsync(cancellationToken);

        return Ok(new { mapping.MaxConcurrency });
    }
}

/// <summary>
/// 更新并发数请求体。
/// </summary>
public sealed class UpdateConcurrencyRequest
{
    /// <summary>
    /// 最大并发数，0 表示不限制。
    /// </summary>
    public int MaxConcurrency { get; set; }
}
