using AITool.Application.CoreRuntime;
using AITool.Infrastructure.Conversations;
using AITool.Infrastructure.CoreRuntime;
using Microsoft.Extensions.Logging;

namespace AITool.Admin.Services;

/// <summary>
/// Admin 侧从 Core 宿主拉取事件并消费入库的核心服务。
/// 从 <see cref="CoreEventPullHostedService"/> 提取出来的可独立测试的逻辑单元。
/// <para>
/// 单次处理流程：Replay（拉取）→ Ingest（按事件类型分别消费入库）→ Ack（确认）。
/// 当前支持的事件类型：
/// <list type="bullet">
///   <item><c>usage-log</c> — 代理使用日志，写入 Admin 数据库</item>
///   <item><c>conversation-turn</c> — 对话记录，写入 Admin 本地 JSONL 文件</item>
///   <item><c>developer-trace</c> — 开发者调用追踪，写入 Admin 内存缓存</item>
///   <item><c>route-fallback</c> — 路由回退事件，写入 Admin 内存缓存</item>
///   <item><c>config-applied</c> — 配置变更应用确认，写入 Admin 内存缓存</item>
///   <item><c>circuit-breaker</c> — 熔断状态变更事件，写入 Admin 内存缓存</item>
/// </list>
/// </para>
/// <para>
/// ack 状态通过 <see cref="CoreEventAckStateStore"/> 持久化到本地文件，
/// 确保 Admin 重启后能从正确的序号位置继续拉取，避免重复消费已入库的历史事件。
/// </para>
/// </summary>
public sealed class CoreEventPullService
{
    private readonly CoreAdminClient _coreClient;
    private readonly AdminUnifiedProxyEventIngestor _unifiedIngestor;
    private readonly AdminConversationTurnEventIngestor _conversationTurnIngestor;
    private readonly AdminRouteFallbackEventIngestor _routeFallbackIngestor;
    private readonly AdminConfigAppliedEventIngestor _configAppliedIngestor;
    private readonly AdminCircuitBreakerEventIngestor _circuitBreakerIngestor;
    private readonly CoreEventAckStateStore _ackStateStore;
    private readonly ILogger<CoreEventPullService> _logger;

    /// <summary>
    /// Admin 实例标识，用于 ack 请求中区分不同 Admin 实例。
    /// </summary>
    private readonly string _adminInstanceId;

    /// <summary>
    /// 当前已确认的最大事件序号。
    /// 跨轮次保持，确保每次 replay 只拉取增量事件。
    /// </summary>
    private long _ackedSequenceId;

    /// <summary>
    /// 初始化事件拉取服务，从 ack 持久化文件恢复上次的确认序号。
    /// </summary>
    public CoreEventPullService(
        CoreAdminClient coreClient,
        AdminUnifiedProxyEventIngestor unifiedIngestor,
        AdminConversationTurnEventIngestor conversationTurnIngestor,
        AdminRouteFallbackEventIngestor routeFallbackIngestor,
        AdminConfigAppliedEventIngestor configAppliedIngestor,
        AdminCircuitBreakerEventIngestor circuitBreakerIngestor,
        CoreEventAckStateStore ackStateStore,
        ILogger<CoreEventPullService> logger,
        string? adminInstanceId = null)
    {
        _coreClient = coreClient;
        _unifiedIngestor = unifiedIngestor;
        _conversationTurnIngestor = conversationTurnIngestor;
        _routeFallbackIngestor = routeFallbackIngestor;
        _configAppliedIngestor = configAppliedIngestor;
        _circuitBreakerIngestor = circuitBreakerIngestor;
        _ackStateStore = ackStateStore;
        _logger = logger;
        _adminInstanceId = adminInstanceId ?? $"admin-{Environment.MachineName}-{Environment.ProcessId}";

        // 从持久化文件恢复上次的确认序号，避免重启后重复消费
        _ackedSequenceId = _ackStateStore.LoadAckedSequenceId();
        if (_ackedSequenceId > 0)
        {
            _logger.LogDebug("已从持久化恢复 ack 序号：{AckedSequenceId}", _ackedSequenceId);
        }
    }

    /// <summary>
    /// 当前已确认的事件序号，用于测试和诊断。
    /// </summary>
    public long AckedSequenceId => _ackedSequenceId;

    /// <summary>
    /// 执行一次完整的拉取 → 消费 → 确认流程。
    /// 返回本批次处理的事件数量；如果没有积压事件则返回 0。
    /// </summary>
    public async Task<int> PullAndProcessAsync(CancellationToken cancellationToken)
    {
        // 从 Core 拉取 _ackedSequenceId 之后的所有积压事件
        var envelopes = await _coreClient.ReplayAsync(
            _ackedSequenceId,
            cancellationToken);

        if (envelopes.Count == 0)
        {
            return 0;
        }

        _logger.LogDebug(
            "从 Core 拉取到 {EventCount} 条事件，AfterSequenceId={AfterSequenceId}",
            envelopes.Count,
            _ackedSequenceId);

        // 按事件类型分别消费入库
        var unifiedMax = await _unifiedIngestor.IngestUnifiedProxyEventsAsync(envelopes, cancellationToken);
        var conversationTurnMax = await _conversationTurnIngestor.IngestConversationTurnEventsAsync(envelopes, cancellationToken);
        var routeFallbackMax = await _routeFallbackIngestor.IngestRouteFallbackEventsAsync(envelopes, cancellationToken);
        var configAppliedMax = await _configAppliedIngestor.IngestConfigAppliedEventsAsync(envelopes, cancellationToken);
        var circuitBreakerMax = await _circuitBreakerIngestor.IngestCircuitBreakerEventsAsync(envelopes, cancellationToken);

        // ack 序号始终推进到本批次最大值，确保 spool 中所有事件都被确认（包括无法消费的未知类型）
        var maxAcked = envelopes.Max(e => e.SequenceId);

        // 向 Core 提交确认，清理已成功处理的事件
        var ackResult = await _coreClient.AckAsync(
            new CoreAdminAckRequest
            {
                AdminInstanceId = _adminInstanceId,
                AckedSequenceId = maxAcked,
                AckedAt = DateTimeOffset.UtcNow
            },
            cancellationToken);

        // 更新本地已确认序号并持久化到磁盘
        _ackedSequenceId = maxAcked;
        _ackStateStore.SaveAckedSequenceId(maxAcked);

        _logger.LogDebug(
            "事件确认完成。AckedSequenceId={AckedSequenceId}，事件处理 {EventCount} 条",
            ackResult.AckedSequenceId,
            envelopes.Count);

        return envelopes.Count;
    }
}
