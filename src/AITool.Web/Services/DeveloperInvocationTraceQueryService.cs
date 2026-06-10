namespace AITool.Web.Services;

/// <summary>
/// 开发者调用跟踪只读查询服务。
/// 当前阶段先把后台页面对调用跟踪存储的读取动作包一层门面，避免页面直接依赖运行时写入存储对象。
/// </summary>
public sealed class DeveloperInvocationTraceQueryService
{
    /// <summary>
    /// 运行时调用跟踪存储。
    /// </summary>
    private readonly DeveloperInvocationTraceStore _traceStore;

    /// <summary>
    /// 初始化调用跟踪查询服务。
    /// </summary>
    public DeveloperInvocationTraceQueryService(DeveloperInvocationTraceStore traceStore)
    {
        _traceStore = traceStore;
    }

    /// <summary>
    /// 返回当前调用记录列表。
    /// </summary>
    public IReadOnlyList<DeveloperInvocationTraceEntry> List()
    {
        return _traceStore.List();
    }

    /// <summary>
    /// 按跟踪标识获取调用记录。
    /// </summary>
    public DeveloperInvocationTraceEntry? Get(Guid traceId)
    {
        return _traceStore.Get(traceId);
    }
}
