using System.Collections.Concurrent;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using SqlSugar;

namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 内存中维护"Site → 最近一次被代理使用的时间"映射，供 Codex 巡检判断账号是否被使用，
/// 替代每次巡检都回查 ProxyUsageLogs 表（N 个账号即 N 次索引查询）。
/// <para>
/// 数据来源：主链路每次产生代理用量日志入队时调 <see cref="RecordUsage"/> 增量更新；
/// 服务启动时由 <see cref="WarmupAsync"/> 从 DB 预热一次，避免重启后历史丢失。
/// </para>
/// <para>
/// 设计为单例：映射是纯内存的累加状态，多线程读写由 ConcurrentDictionary 保证安全。
/// </para>
/// </summary>
public sealed class SiteUsageTracker
{
    /// <summary>
    /// SiteId → 最近一次代理使用时间（UTC）。
    /// 用 ConcurrentDictionary 是因为代理请求线程（写入）与巡检线程（读取）会并发访问。
    /// </summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastUsedAt = new();

    /// <summary>
    /// 记录某个 Site 被使用。由代理用量日志入队时调用，取较新时间避免乱序请求覆盖最新值。
    /// </summary>
    public void RecordUsage(Guid siteId, DateTimeOffset? usedAt = null)
    {
        if (siteId == Guid.Empty) return;

        var ts = usedAt ?? DateTimeOffset.UtcNow;
        // 多线程并发写同一 SiteId 时，取较新时间，避免乱序到达的旧日志覆盖最新使用时间。
        _lastUsedAt.AddOrUpdate(siteId, ts, (_, existing) => ts > existing ? ts : existing);
    }

    /// <summary>
    /// 读取某个 Site 的最近使用时间。无记录返回 null（巡检据此判定"自上次刷新后是否被使用过"）。
    /// </summary>
    public DateTimeOffset? GetLastUsedAt(Guid siteId)
    {
        return _lastUsedAt.TryGetValue(siteId, out var ts) ? ts : null;
    }

    /// <summary>
    /// 服务启动时从 DB 预热：按 SiteId 分组取 Max(RequestedAt)，避免重启后近期未使用的 Site 被误判。
    /// 仅启动期执行一次，失败不阻塞启动（预热失败时映射为空，会在后续日志入队时逐步补全）。
    /// </summary>
    public async Task WarmupAsync(AppDbContext dbContext, CancellationToken cancellationToken = default)
    {
        try
        {
            // 只取最近 7 天，避免全表扫描（日志表可能有数百万行）
            var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
            var recent = await dbContext.ProxyUsageLogs
                .Where(l => l.TargetSiteId != Guid.Empty && l.RequestedAt >= cutoff)
                .GroupBy(l => l.TargetSiteId)
                .Select(group => new
                {
                    TargetSiteId = group.TargetSiteId,
                    RequestedAt = SqlFunc.AggregateMax(group.RequestedAt)
                })
                .ToListAsync(cancellationToken);

            foreach (var item in recent)
            {
                // 用 AddOrUpdate 与 RecordUsage 同样的"取较新"语义，避免预热值被空 Guid 误覆盖。
                _lastUsedAt.AddOrUpdate(
                    item.TargetSiteId,
                    item.RequestedAt,
                    (_, existing) => item.RequestedAt > existing ? item.RequestedAt : existing);
            }
        }
        catch
        {
            // 预热失败不影响服务启动，映射会在后续日志入队时逐步补全。
        }
    }
}
