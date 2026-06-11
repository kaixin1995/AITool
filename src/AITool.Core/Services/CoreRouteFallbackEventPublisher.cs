using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;

namespace AITool.Core.Services;

/// <summary>
/// 路由回退事件发布器。
/// 当代理请求在某条路由上失败并回退到下一条路由时，将回退信息发布为
/// <c>route-fallback</c> 事件，Admin 侧消费后用于实时监控路由健康和分析回退模式。
/// <para>
/// 发布时机：控制器回退循环中，当前路由失败且有下一条候选路由时触发。
/// 每次路由切换产生一条事件，记录源路由/站点和目标路由/站点的标识，
/// 以及触发回退的具体原因（如上游超时、HTTP 错误等）。
/// </para>
/// </summary>
public sealed class CoreRouteFallbackEventPublisher
{
    private readonly CoreEventSequenceProvider _sequenceProvider;
    private readonly CoreAdminEventBus _eventBus;

    /// <summary>
    /// 初始化路由回退事件发布器。
    /// </summary>
    public CoreRouteFallbackEventPublisher(
        CoreEventSequenceProvider sequenceProvider,
        CoreAdminEventBus eventBus)
    {
        _sequenceProvider = sequenceProvider;
        _eventBus = eventBus;
    }

    /// <summary>
    /// 发布一条路由回退事件。
    /// </summary>
    /// <param name="requestId">关联的代理请求标识。</param>
    /// <param name="requestModel">请求模型名（路由前的原始模型名）。</param>
    /// <param name="fromRouteId">回退源路由标识。</param>
    /// <param name="fromSiteId">回退源站点标识。</param>
    /// <param name="fromSiteModelName">回退源站点上的模型名。</param>
    /// <param name="toRouteId">回退目标路由标识。</param>
    /// <param name="toSiteId">回退目标站点标识。</param>
    /// <param name="toSiteModelName">回退目标站点上的模型名。</param>
    /// <param name="reason">触发回退的原因。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task PublishAsync(
        Guid requestId,
        string requestModel,
        Guid fromRouteId,
        Guid fromSiteId,
        string fromSiteModelName,
        Guid toRouteId,
        Guid toSiteId,
        string toSiteModelName,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var payload = new CoreRouteFallbackEvent
        {
            RequestId = requestId,
            RequestModel = requestModel,
            FromRouteId = fromRouteId,
            FromSiteId = fromSiteId,
            FromSiteModelName = fromSiteModelName,
            ToRouteId = toRouteId,
            ToSiteId = toSiteId,
            ToSiteModelName = toSiteModelName,
            Reason = reason,
            OccurredAt = DateTimeOffset.UtcNow
        };

        var envelope = CoreAdminEventEnvelopeBuilder.CreateRouteFallbackEnvelope(_sequenceProvider.Next(), payload);
        await _eventBus.PublishAsync(envelope, cancellationToken);
    }
}
