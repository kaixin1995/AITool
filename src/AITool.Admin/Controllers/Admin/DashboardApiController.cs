using AITool.Infrastructure.Proxy;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Admin.Controllers.Admin;

/// <summary>
/// 仪表盘 API，提供与历史首页一致的启用数量和运行状态摘要。
/// </summary>
[ApiController]
[Route("api/admin/dashboard")]
public sealed class DashboardApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public DashboardApiController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("stats")]
    public async Task<ActionResult<DashboardStatsDto>> GetStats(CancellationToken cancellationToken)
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        return Ok(new DashboardStatsDto
        {
            SiteCount = await _dbContext.Sites.CountAsync(x => x.IsEnabled, cancellationToken),
            ModelCount = await _dbContext.ModelLibraryItems.CountAsync(cancellationToken),
            MappingCount = await _dbContext.SiteModelMappings.CountAsync(cancellationToken),
            RouteCount = await _dbContext.ProxyRouteRules.CountAsync(cancellationToken),
            AccessKeyCount = await _dbContext.ProxyAccessKeys.CountAsync(x => x.IsEnabled, cancellationToken),
            DetectionTaskCount = await _dbContext.DetectionTasks.CountAsync(x => x.IsEnabled, cancellationToken),
            CoreBaseUrl = baseUrl,
            CoreStatusText = "Web API 已就绪",
            CoreSyncStatusText = "前后端直连",
            CoreSyncDetailText = "当前 SPA 直接通过 REST API 管理后台数据"
        });
    }
}

public sealed class DashboardStatsDto
{
    public int SiteCount { get; set; }
    public int ModelCount { get; set; }
    public int MappingCount { get; set; }
    public int RouteCount { get; set; }
    public int AccessKeyCount { get; set; }
    public int DetectionTaskCount { get; set; }
    public string CoreBaseUrl { get; set; } = string.Empty;
    public string CoreStatusText { get; set; } = string.Empty;
    public string CoreSyncStatusText { get; set; } = string.Empty;
    public string CoreSyncDetailText { get; set; } = string.Empty;
}
