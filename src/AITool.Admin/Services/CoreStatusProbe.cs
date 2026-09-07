using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using Microsoft.Extensions.Logging;

namespace AITool.Admin.Services;

/// <summary>
/// Core 宿主状态提供者抽象：Admin 宿主用真实握手探测（<see cref="CoreStatusProbe"/>），
/// AllInOne 单进程形态用本地模式实现（恒在线文案，无跨宿主语义）。
/// </summary>
public interface ICoreStatusProvider
{
    Task<(string Status, string Sync, string Detail)> ProbeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Core 宿主状态探测器：握手探测 + 内存缓存（TTL 8s）。
/// 目的：前端 10s 轮询仪表盘时，Core 离线的握手超时（2s）不会反复阻塞 stats 响应。
/// </summary>
public sealed class CoreStatusProbe : ICoreStatusProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly CoreAdminClient _coreClient;
    private readonly CoreSyncStatusStore _syncStatusStore;
    private readonly ILogger<CoreStatusProbe> _logger;
    private readonly object _gate = new();
    private (DateTimeOffset At, string Status, string Sync, string Detail)? _cache;

    public CoreStatusProbe(
        CoreAdminClient coreClient,
        CoreSyncStatusStore syncStatusStore,
        ILogger<CoreStatusProbe> logger)
    {
        _coreClient = coreClient;
        _syncStatusStore = syncStatusStore;
        _logger = logger;
    }

    public async Task<(string Status, string Sync, string Detail)> ProbeAsync(CancellationToken cancellationToken)
    {
        var snapshot = _syncStatusStore.GetSnapshot();
        var syncPrefix = snapshot.LastSuccessAt.HasValue
            ? $"最近配置同步成功：{snapshot.LastSuccessAt:HH:mm:ss}"
            : "尚未完成配置同步";
        var syncSuffix = snapshot.LastFailureAt.HasValue
            ? $"，最近失败：{snapshot.LastFailureAt:HH:mm:ss}（{snapshot.LastError}）"
            : string.Empty;
        var syncText = syncPrefix + syncSuffix;

        lock (_gate)
        {
            if (_cache is { } cached && DateTimeOffset.UtcNow - cached.At < CacheTtl)
            {
                return (cached.Status, cached.Sync, cached.Detail);
            }
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(ProbeTimeout);
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
            CacheResult(result.Item1, result.Item2, result.Item3);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Core 状态探测失败（仪表盘降级为离线文案）");
            var result = ($"Core 离线（{ex.GetType().Name}）", syncText, $"最近同步详情：{snapshot.LastStatus} {snapshot.LastError}");
            CacheResult(result.Item1, result.Item2, result.Item3);
            return result;
        }
    }

    private void CacheResult(string status, string sync, string detail)
    {
        lock (_gate)
        {
            _cache = (DateTimeOffset.UtcNow, status, sync, detail);
        }
    }
}

/// <summary>
/// AllInOne 单进程形态的状态实现：恒在线文案（无跨宿主语义）。
/// </summary>
public sealed class LocalModeStatusProbe : ICoreStatusProvider
{
    public Task<(string Status, string Sync, string Detail)> ProbeAsync(CancellationToken cancellationToken)
        => Task.FromResult(("单进程模式 · 管理面与代理面同进程", "配置直读写库", "无跨宿主组件"));
}