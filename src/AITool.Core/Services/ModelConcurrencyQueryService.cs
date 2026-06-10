namespace AITool.Core.Services;

/// <summary>
/// 模型并发状态只读查询服务。
/// 当前阶段先把后台页面对并发快照的读取动作与运行时并发控制对象分离，避免管理页直接依赖运行时获取/释放逻辑实现。
/// </summary>
public sealed class ModelConcurrencyQueryService
{
    /// <summary>
    /// 运行时模型并发限制器。
    /// </summary>
    private readonly ModelConcurrencyLimiter _concurrencyLimiter;

    /// <summary>
    /// 初始化模型并发查询服务。
    /// </summary>
    public ModelConcurrencyQueryService(ModelConcurrencyLimiter concurrencyLimiter)
    {
        _concurrencyLimiter = concurrencyLimiter;
    }

    /// <summary>
    /// 返回最近保留窗口内的模型并发快照。
    /// </summary>
    public IReadOnlyList<ActiveModelConcurrencyEntry> ListRecent(TimeSpan retention)
    {
        return _concurrencyLimiter.ListRecent(retention);
    }

    /// <summary>
    /// 调试页默认保留最近 6 小时内出现过的模型并发记录。
    /// 直接透传 <c>ModelConcurrencyLimiter.RecentRetention</c>，让管理侧无需引用运行时类型即可读取该常量。
    /// </summary>
    public static TimeSpan RecentRetention => ModelConcurrencyLimiter.RecentRetention;
}
