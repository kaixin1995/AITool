using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Admin;

/// <summary>
/// ModelsApiController。
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
    /// 创建模型管理控制器。
    /// </summary>
    public ModelsApiController(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
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
}
