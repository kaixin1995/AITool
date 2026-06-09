using AITool.Application.CoreRuntime;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Core 事件序号提供器。
/// 当前阶段先在单进程内用原子递增生成全局事件序号，后续再接入本地持久化与 replay 状态。
/// </summary>
public sealed class CoreEventSequenceProvider
{
    private long _current;

    /// <summary>
    /// 生成下一个事件序号。
    /// </summary>
    public long Next()
    {
        return Interlocked.Increment(ref _current);
    }

    /// <summary>
    /// 返回当前已分配到的最新事件序号。
    /// </summary>
    public long Current => Interlocked.Read(ref _current);
}
