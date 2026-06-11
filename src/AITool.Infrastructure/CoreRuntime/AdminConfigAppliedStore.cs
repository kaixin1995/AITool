using AITool.Application.CoreRuntime;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Admin 侧配置变更应用事件内存存储。
/// <para>
/// 消费 Core 发布的 config-applied 事件后，将配置变更记录缓存在内存中，
/// 供 Admin 的配置变更审计页面查询展示。
/// </para>
/// <para>
/// 数据特性：最多保留 100 条，24 小时过期，线程安全。
/// 配置变更事件属于运维审计数据，不需要持久化到数据库。
/// </para>
/// </summary>
public sealed class AdminConfigAppliedStore
{
    /// <summary>
    /// 最大保留记录数。
    /// </summary>
    private const int MaxEntryCount = 100;

    /// <summary>
    /// 记录保留时长。
    /// </summary>
    private static readonly TimeSpan EntryRetention = TimeSpan.FromHours(24);

    /// <summary>
    /// 并发访问锁对象。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// 按发生时间倒序排列的配置变更记录列表（最新在前）。
    /// </summary>
    private readonly LinkedList<CoreConfigAppliedEvent> _entries = [];

    /// <summary>
    /// 添加一条配置变更应用事件记录。
    /// </summary>
    public void Add(CoreConfigAppliedEvent configEvent)
    {
        ArgumentNullException.ThrowIfNull(configEvent);

        lock (_gate)
        {
            PurgeExpiredUnsafe();

            _entries.AddFirst(configEvent);
            TrimUnsafe();
        }
    }

    /// <summary>
    /// 获取所有配置变更记录的快照（按发生时间倒序）。
    /// 返回的是深拷贝列表，调用方可以安全地在任意线程使用。
    /// </summary>
    public IReadOnlyList<CoreConfigAppliedEvent> List()
    {
        lock (_gate)
        {
            PurgeExpiredUnsafe();
            return _entries.ToList();
        }
    }

    /// <summary>
    /// 当前存储的记录总数。
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// 获取最近一次配置变更事件，如果没有记录则返回 null。
    /// </summary>
    public CoreConfigAppliedEvent? GetLatest()
    {
        lock (_gate)
        {
            PurgeExpiredUnsafe();
            return _entries.First?.Value;
        }
    }

    /// <summary>
    /// 清理过期记录（在锁内部调用）。
    /// </summary>
    private void PurgeExpiredUnsafe()
    {
        var expireBefore = DateTimeOffset.UtcNow - EntryRetention;
        while (_entries.Last is { } last && last.Value.OccurredAt < expireBefore)
        {
            _entries.RemoveLast();
        }
    }

    /// <summary>
    /// 裁剪超出上限的记录（在锁内部调用）。
    /// </summary>
    private void TrimUnsafe()
    {
        while (_entries.Count > MaxEntryCount)
        {
            _entries.RemoveLast();
        }
    }
}
