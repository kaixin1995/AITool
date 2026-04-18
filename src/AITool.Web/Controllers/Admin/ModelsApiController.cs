using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AITool.Web.Controllers.Admin;

// 模型库管理 API
[ApiController]
[Route("api/admin/models")]
public sealed class ModelsApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public ModelsApiController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // 清空所有模型及关联数据（映射、检测日志、健康监控）
    [HttpPost("clear-all")]
    public async Task<IActionResult> ClearAll(CancellationToken cancellationToken)
    {
        // 按依赖顺序删除：日志 → 映射 → 监控 → 模型
        var logCount = _dbContext.DetectionLogs.Count();
        var mappingCount = _dbContext.SiteModelMappings.Count();
        var monitorCount = _dbContext.ModelHealthMonitors.Count();
        var modelCount = _dbContext.ModelLibraryItems.Count();

        _dbContext.DetectionLogs.RemoveRange(_dbContext.DetectionLogs);
        _dbContext.SiteModelMappings.RemoveRange(_dbContext.SiteModelMappings);
        _dbContext.ModelHealthMonitors.RemoveRange(_dbContext.ModelHealthMonitors);
        _dbContext.ModelLibraryItems.RemoveRange(_dbContext.ModelLibraryItems);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            deletedModels = modelCount,
            deletedMappings = mappingCount,
            deletedLogs = logCount,
            deletedMonitors = monitorCount
        });
    }
}
