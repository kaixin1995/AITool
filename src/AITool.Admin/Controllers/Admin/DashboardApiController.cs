using AITool.Admin.Services;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Admin.Controllers.Admin;

/// <summary>
/// 仪表盘 API，提供与历史首页一致的启用数量和运行状态摘要。
/// Core 状态为真实探测：握手成功 = 在线（含配置版本/就绪度/事件积压），失败 = 离线（含原因）。
/// 探测与缓存逻辑在 <see cref="CoreStatusProbe"/>（可单测），本控制器仅做委托。
/// </summary>
[ApiController]
[Route("api/admin/dashboard")]
public sealed class DashboardApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly CoreAdminClient _coreClient;
    private readonly CoreSyncStatusStore _syncStatusStore;
    private readonly ICoreStatusProvider _statusProbe;
    private readonly ILogger<DashboardApiController> _logger;

    public DashboardApiController(
        AppDbContext dbContext,
        CoreAdminClient coreClient,
        CoreSyncStatusStore syncStatusStore,
        ICoreStatusProvider statusProbe,
        ILogger<DashboardApiController> logger)
    {
        _dbContext = dbContext;
        _coreClient = coreClient;
        _syncStatusStore = syncStatusStore;
        _statusProbe = statusProbe;
        _logger = logger;
    }

    [HttpGet("stats")]
    public async Task<ActionResult<DashboardStatsDto>> GetStats(CancellationToken cancellationToken)
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var (statusText, syncText, detailText) = await _statusProbe.ProbeAsync(cancellationToken);

        return Ok(new DashboardStatsDto
        {
            SiteCount = await _dbContext.Sites.CountAsync(x => x.IsEnabled, cancellationToken),
            ModelCount = await _dbContext.ModelLibraryItems.CountAsync(cancellationToken),
            MappingCount = await _dbContext.SiteModelMappings.CountAsync(cancellationToken),
            RouteCount = await _dbContext.ProxyRouteRules.CountAsync(cancellationToken),
            AccessKeyCount = await _dbContext.ProxyAccessKeys.CountAsync(x => x.IsEnabled, cancellationToken),
            DetectionTaskCount = await _dbContext.DetectionTasks.CountAsync(x => x.IsEnabled, cancellationToken),
            CoreBaseUrl = baseUrl,
            CoreStatusText = statusText,
            CoreSyncStatusText = syncText,
            CoreSyncDetailText = detailText
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