using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Admin;

// 模型库管理 API
[ApiController]
[Route("api/admin/models")]
public sealed class ModelsApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ProxyRequestMetadataCache _metadataCache;

    public ModelsApiController(AppDbContext dbContext, ProxyRequestMetadataCache metadataCache)
    {
        _dbContext = dbContext;
        _metadataCache = metadataCache;
    }

    // 清空所有模型及关联数据（映射、健康监控）
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
