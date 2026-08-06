namespace AITool.Application.Proxy;

/// <summary>
/// 代理调用统一记录服务，从一份 <see cref="ProxyCallContext"/> 派发数据到
/// UsageLog 和 DeveloperInvocationTrace 两个存储，
/// 避免代理管道中分散地多次重复采集。
/// </summary>
public interface IProxyCallRecorder
{
    /// <summary>
    /// 创建一次请求级的开发者追踪记录。
    /// 在代理管道入口处调用一次，返回追踪标识，后续尝试用此标识关联。
    /// 当开发者功能未启用时返回 null。
    /// </summary>
    Guid? BeginTrace(ProxyCallContext context);

    /// <summary>
    /// 为当前追踪追加一次路由尝试记录。
    /// 在每轮路由尝试开始时调用，返回尝试标识用于后续完成。
    /// </summary>
    Guid BeginTraceAttempt(Guid? traceId, ProxyCallContext context);

    /// <summary>
    /// 完成一次路由尝试的开发者追踪记录。
    /// </summary>
    void CompleteTraceAttempt(Guid? traceId, Guid traceAttemptId, ProxyCallContext context);

    /// <summary>
    /// 客户端断开时强制取消一条 pending 追踪记录。
    /// 仅在追踪记录仍处于 pending 状态时生效。
    /// </summary>
    void CancelTrace(Guid? traceId, string reason);

    /// <summary>
    /// 写入用量日志（ProxyUsageLog），记录一次路由尝试的完整统计信息。
    /// </summary>
    Task RecordUsageAsync(ProxyCallContext context, CancellationToken cancellationToken = default);
}
