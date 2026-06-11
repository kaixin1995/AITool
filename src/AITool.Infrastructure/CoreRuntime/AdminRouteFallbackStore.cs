using AITool.Application.CoreRuntime;

namespace AITool.Infrastructure.CoreRuntime;

/// <summary>
/// Admin 侧路由回退事件内存存储。
/// <para>
/// 消费 Core 发布的 route-fallback 事件后，将回退记录缓存在内存中，
/// 供 Admin 的路由健康监控页面查询展示。
/// </para>
/// <para>
/// 数据特性：最多保留 200 条，6 小时过期，线程安全。
/// 回退事件属于运行时诊断数据，不需要持久化到数据库。
/// </para>
/// </summary>
public sealed class AdminRouteFallbackStore
{
    /// <summary>
    /// 最大保留记录数。
    /// </summary>
    private const int MaxEntryCount = 200;

    /// <summary>
    /// 记录保留时长。
    /// </summary>
    private static readonly TimeSpan EntryRetention = TimeSpan.FromHours(6);

    /// <summary>
    /// 并发访问锁对象。
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// 按发生时间倒序排列的回退记录列表（最新在前）。
    /// </summary>
    private readonly LinkedList<CoreRouteFallbackEvent> _entries = [];

    /// <summary>
    /// 添加一条回退事件记录。
    /// </summary>
    public void Add(CoreRouteFallbackEvent fallbackEvent)
    {
        ArgumentNullException.ThrowIfNull(fallbackEvent);

        lock (_gate)
        {
            PurgeExpiredUnsafe();

            _entries.AddFirst(fallbackEvent);
            TrimUnsafe();
        }
    }

    /// <summary>
    /// 批量添加回退事件记录。
    /// </summary>
    public void AddRange(IEnumerable<CoreRouteFallbackEvent> events)
    {
        foreach (var evt in events)
        {
            Add(evt);
        }
    }

    /// <summary>
    /// 获取所有回退记录的快照（按发生时间倒序）。
    /// 返回的是深拷贝列表，调用方可以安全地在任意线程使用。
    /// </summary>
    public IReadOnlyList<CoreRouteFallbackEvent> List()
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
    /// 获取统计摘要信息，供 Admin 监控页面使用。
    /// </summary>
    public (int TotalCount, int UniqueFromSites, int UniqueToSites) GetSummary()
    {
        lock (_gate)
        {
            PurgeExpiredUnsafe();
            var totalCount = _entries.Count;
            var uniqueFromSites = _entries.Select(e => e.FromSiteId).Distinct().Count();
            var uniqueToSites = _entries.Select(e => e.ToSiteId).Distinct().Count();
            return (totalCount, uniqueFromSites, uniqueToSites);
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
