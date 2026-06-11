using AITool.Application.CoreRuntime;
using AITool.Infrastructure.Proxy;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// 熔断状态变更事件发布器。
/// <para>
/// 订阅 <see cref="RouteCircuitStateStore.OnCircuitOpened"/> 事件，
/// 当某条路由因连续失败达到阈值被首次熔断时，将事件发布到 Core 事件总线。
/// Admin 侧消费后可实时监控路由健康状态和熔断模式。
/// </para>
/// </summary>
public sealed class CoreCircuitBreakerEventPublisher
{
    private readonly CoreEventSequenceProvider _sequenceProvider;
    private readonly CoreAdminEventBus _eventBus;

    /// <summary>
    /// 初始化熔断状态变更事件发布器。
    /// </summary>
    public CoreCircuitBreakerEventPublisher(
        CoreEventSequenceProvider sequenceProvider,
        CoreAdminEventBus eventBus)
    {
        _sequenceProvider = sequenceProvider;
        _eventBus = eventBus;
    }

    /// <summary>
    /// 发布一条熔断状态变更事件。
    /// </summary>
    /// <param name="args">熔断触发事件参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task PublishAsync(CircuitOpenedEventArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        var payload = new CoreCircuitBreakerEvent
        {
            RouteId = args.RouteId,
            FailureCount = args.FailureCount,
            FailThreshold = args.FailThreshold,
            BlockDuration = args.BlockDuration,
            RecoveryTime = args.RecoveryTime,
            OccurredAt = DateTimeOffset.UtcNow
        };

        var envelope = CoreAdminEventEnvelopeBuilder.CreateCircuitBreakerEnvelope(
            _sequenceProvider.Next(), payload);
        await _eventBus.PublishAsync(envelope, cancellationToken);
    }
}
