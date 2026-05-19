using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Admin;

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
    /// 代理元数据缓存。
    /// </summary>
    private readonly ProxyRequestMetadataCache _metadataCache;
    /// <summary>
    /// 模型并发限制器。
    /// </summary>
    private readonly ModelConcurrencyLimiter _concurrencyLimiter;

    /// <summary>
    /// 创建模型管理控制器。
    /// </summary>
    public ModelsApiController(
        AppDbContext dbContext,
        ProxyRequestMetadataCache metadataCache,
        ModelConcurrencyLimiter concurrencyLimiter)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
        _concurrencyLimiter = concurrencyLimiter;
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

        _dbContext.SiteModelMappings.RemoveRange(_dbContext.SiteModelMappings);
        _dbContext.ModelHealthMonitors.RemoveRange(_dbContext.ModelHealthMonitors);
        _dbContext.ModelLibraryItems.RemoveRange(_dbContext.ModelLibraryItems);

        await _dbContext.SaveChangesAsync(cancellationToken);
        _metadataCache.InvalidateModelMetadata();
        _metadataCache.InvalidateRouteTargets();

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
        var mapping = await _dbContext.SiteModelMappings.FindAsync([mappingId], cancellationToken);
        if (mapping is null)
        {
            return NotFound(new { message = "站点模型映射不存在" });
        }

        mapping.MaxConcurrency = Math.Max(0, request.MaxConcurrency);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // 配置保存后立即失效缓存，并同步更新运行中的限制器状态，仅影响后续新请求。
        _metadataCache.InvalidateRouteTargets();
        _concurrencyLimiter.UpdateLimit(mapping.SiteId, mapping.RemoteModelName, mapping.MaxConcurrency);

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
