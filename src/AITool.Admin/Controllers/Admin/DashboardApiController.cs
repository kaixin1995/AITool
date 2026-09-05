using AITool.Admin.Services;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Admin.Controllers.Admin;

/// <summary>
/// 仪表盘 API，提供与历史首页一致的启用数量和运行状态摘要。
/// Core 状态为真实探测：握手成功 = 在线（含配置版本/就绪度/事件积压），失败 = 离线（含原因）。
/// </summary>
[ApiController]
[Route("api/admin/dashboard")]
public sealed class DashboardApiController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly CoreAdminClient _coreClient;
    private readonly CoreSyncStatusStore _syncStatusStore;
    private readonly ILogger<DashboardApiController> _logger;

    public DashboardApiController(
        AppDbContext dbContext,
        CoreAdminClient coreClient,
        CoreSyncStatusStore syncStatusStore,
        ILogger<DashboardApiController> logger)
    {
        _dbContext = dbContext;
        _coreClient = coreClient;
        _syncStatusStore = syncStatusStore;
        _logger = logger;
    }

    [HttpGet("stats")]
    public async Task<ActionResult<DashboardStatsDto>> GetStats(CancellationToken cancellationToken)
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var (statusText, syncText, detailText) = await ProbeCoreStatusAsync(cancellationToken);

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

    /// <summary>
    /// 探测 Core 宿主：握手 + 本地最近同步状态。
    /// 结果内存缓存（TTL 8s），避免前端 10s 轮询下离线握手（2s 超时）反复阻塞 stats 响应。
    /// </summary>
    private static readonly object ProbeLock = new();
    private static (DateTimeOffset At, string Status, string Sync, string Detail)? _probeCache;

    private async Task<(string Status, string Sync, string Detail)> ProbeCoreStatusAsync(CancellationToken cancellationToken)
    {
        var snapshot = _syncStatusStore.GetSnapshot();
        var syncPrefix = snapshot.LastSuccessAt.HasValue
            ? $"最近配置同步成功：{snapshot.LastSuccessAt:HH:mm:ss}"
            : "尚未完成配置同步";
        var syncSuffix = snapshot.LastFailureAt.HasValue
            ? $"，最近失败：{snapshot.LastFailureAt:HH:mm:ss}（{snapshot.LastError}）"
            : string.Empty;
        var syncText = syncPrefix + syncSuffix;

        lock (ProbeLock)
        {
            if (_probeCache is { } cached && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromSeconds(8))
            {
                return (cached.Status, cached.Sync, cached.Detail);
            }
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var handshake = await _coreClient.HandshakeAsync(new CoreAdminHandshakeRequest
            {
                AdminInstanceId = $"admin-{Environment.MachineName}-{Environment.ProcessId}",
                AdminStartedAt = DateTimeOffset.UtcNow,
                CurrentConfigVersion = 0,
                CurrentConfigHash = string.Empty,
                LastAckedSequenceId = 0
            }, cts.Token);

            var backlogText = handshake.HasSpoolBacklog ? "（事件有积压）" : string.Empty;
            var status = handshake.Ready
                ? $"Core 在线 · 配置版本 {handshake.AppliedConfigVersion}{backlogText}"
                : "Core 运行中但未就绪（等待配置下发）";
            var detail = $"Core 实例 {handshake.CoreInstanceId} · 启动于 {handshake.CoreStartedAt:HH:mm:ss} · 已应用配置 {handshake.AppliedConfigVersion}";
            var result = (status, syncText, detail);
            lock (ProbeLock)
            {
                _probeCache = (DateTimeOffset.UtcNow, status, syncText, detail);
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Core 状态探测失败（仪表盘降级为离线文案）");
            var result = ($"Core 离线（{ex.GetType().Name}）", syncText, $"最近同步详情：{snapshot.LastStatus} {snapshot.LastError}");
            lock (ProbeLock)
            {
                _probeCache = (DateTimeOffset.UtcNow, result.Item1, result.Item2, result.Item3);
            }

            return result;
        }
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