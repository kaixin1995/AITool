namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 路由级熔断状态存储，单条路由连续失败达到阈值后被临时屏蔽
/// </summary>
public sealed class RouteCircuitStateStore
{
    /// <summary>
    /// 保护熔断参数读写的同步锁
    /// </summary>
    private readonly object _syncRoot = new();
    /// <summary>
    /// 触发熔断后路由被屏蔽的持续时间
    /// </summary>
    private long _blockDurationTicks;
    /// <summary>
    /// 连续失败达到该次数时触发熔断
    /// </summary>
    private int _failThreshold;
    /// <summary>
    /// 路由连续失败次数记录
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _failCounts = [];
    /// <summary>
    /// 被熔断的路由及其解除时间
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _blockedRoutes = [];
    /// <summary>
    /// 熔断键对应的站点/模型归属（面板在候选移除后仍能展示其归属；成功/解除/过期时一并清理）。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CircuitRouteMeta> _routeMeta = [];
    /// <summary>
    /// 分级冷却（休息）路由及其解除时间：配额耗尽休到重置点、限流按 Retry-After 等。
    /// 与熔断（计数触发）互不影响；两者取更晚的解除时间生效。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _restedRoutes = [];
    /// <summary>
    /// 分级冷却原因（面板展示用）。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> _restReasons = [];

    /// <summary>
    /// 注入熔断屏蔽时长和连续失败阈值
    /// </summary>
    public RouteCircuitStateStore(TimeSpan? blockDuration = null, int failThreshold = 5)
    {
        _blockDurationTicks = (blockDuration ?? TimeSpan.FromMinutes(2)).Ticks;
        _failThreshold = failThreshold;
    }

    /// <summary>
    /// 动态更新熔断参数，新配置应尽快影响后续请求。
    /// </summary>
    public void UpdateOptions(TimeSpan blockDuration, int failThreshold)
    {
        lock (_syncRoot)
        {
            Volatile.Write(ref _blockDurationTicks, blockDuration.Ticks);
            Volatile.Write(ref _failThreshold, failThreshold);
        }
    }

    /// <summary>
    /// 记录一次失败，连续失败达到阈值时触发熔断。
    /// meta 携带站点/模型归属（仅在提供时覆盖旧值），供面板在候选移除后仍能展示归属。
    /// </summary>
    public void Block(Guid routeId, CircuitRouteMeta? meta = null)
    {
        // 如果已经被熔断，不再重复计数
        if (IsBlocked(routeId)) return;

        var count = _failCounts.AddOrUpdate(routeId, 1, (_, current) => current + 1);
        if (meta is not null)
        {
            _routeMeta[routeId] = meta;
        }
        var failThreshold = Volatile.Read(ref _failThreshold);
        var blockDuration = TimeSpan.FromTicks(Volatile.Read(ref _blockDurationTicks));

        if (count >= failThreshold)
        {
            _blockedRoutes[routeId] = DateTimeOffset.UtcNow.Add(blockDuration);
        }
    }

    /// <summary>
    /// 分级冷却：让路由直接休息到指定时刻（配额耗尽→重置点、限流→Retry-After），
    /// 不走失败计数。已有更晚的熔断/休息时间时保留更晚者；同时清空失败计数。
    /// </summary>
    public void Rest(Guid routeId, DateTimeOffset untilUtc, string reason, CircuitRouteMeta? meta = null)
    {
        if (untilUtc <= DateTimeOffset.UtcNow)
        {
            return;
        }

        // 读最大-再写入必须原子：并发 Rest 交错会让较短休息覆盖较长（丢最长冷却）。
        lock (_syncRoot)
        {
            var effective = untilUtc;
            if (_blockedRoutes.TryGetValue(routeId, out var blocked) && blocked > effective)
            {
                effective = blocked;
            }

            if (_restedRoutes.TryGetValue(routeId, out var rested) && rested > effective)
            {
                effective = rested;
            }

            _restedRoutes[routeId] = effective;
            _failCounts.TryRemove(routeId, out _);
            if (meta is not null)
            {
                _routeMeta[routeId] = meta;
            }

            if (!string.IsNullOrWhiteSpace(reason))
            {
                _restReasons[routeId] = reason;
            }
        }
    }

    /// <summary>
    /// 查询分级冷却信息（未休息或已过期返回 null），供面板展示休息原因与解除时间。
    /// </summary>
    public (DateTimeOffset UntilUtc, string Reason)? GetRest(Guid routeId)
    {
        // 清理与 IsBlocked 同口径入锁：防止清掉并发写入的新休息。
        lock (_syncRoot)
        {
            if (_restedRoutes.TryGetValue(routeId, out var until))
            {
                if (until > DateTimeOffset.UtcNow)
                {
                    return (until, _restReasons.TryGetValue(routeId, out var reason) ? reason : string.Empty);
                }

                _restedRoutes.TryRemove(routeId, out _);
                _restReasons.TryRemove(routeId, out _);
            }

            return null;
        }
    }

    /// <summary>
    /// 记录一次成功，清除该路由的连续失败计数
    /// </summary>
    public void Succeed(Guid routeId)
    {
        _failCounts.TryRemove(routeId, out _);
        _routeMeta.TryRemove(routeId, out _);
        _restedRoutes.TryRemove(routeId, out _);
        _restReasons.TryRemove(routeId, out _);
    }

    /// <summary>
    /// 判断路由当前是否仍处于熔断窗口内
    /// </summary>
    public bool IsBlocked(Guid routeId)
    {
        // 过期判定+清理入锁：防止「读到过期值→并发写入新休息→清理误删新休息」的丢更新。
        lock (_syncRoot)
        {
            if (_blockedRoutes.TryGetValue(routeId, out var until))
            {
                if (until > DateTimeOffset.UtcNow) return true;
                _blockedRoutes.TryRemove(routeId, out _);
                _failCounts.TryRemove(routeId, out _);
                _routeMeta.TryRemove(routeId, out _);
            }

            if (_restedRoutes.TryGetValue(routeId, out var rested))
            {
                if (rested > DateTimeOffset.UtcNow) return true;
                _restedRoutes.TryRemove(routeId, out _);
                _restReasons.TryRemove(routeId, out _);
                _failCounts.TryRemove(routeId, out _);
                _routeMeta.TryRemove(routeId, out _);
            }

            return false;
        }
    }

    /// <summary>
    /// 返回所有当前被熔断的路由（routeId → 解除时间）和正在累计失败但尚未熔断的路由。
    /// 分级冷却中的路由同样以 IsBlocked=true 呈现，BlockedUntil 取熔断与休息中更晚者。
    /// </summary>
    public IReadOnlyDictionary<Guid, CircuitRouteInfo> GetAllCircuitStates()
    {
        var now = DateTimeOffset.UtcNow;
        var result = new Dictionary<Guid, CircuitRouteInfo>();

        // 先清理已过期的熔断与休息（入锁：与 Rest/IsBlocked 同口径，防清掉并发新写入）
        lock (_syncRoot)
        {
            foreach (var pair in _blockedRoutes)
            {
                if (pair.Value <= now)
                {
                    _blockedRoutes.TryRemove(pair.Key, out _);
                    _failCounts.TryRemove(pair.Key, out _);
                    _routeMeta.TryRemove(pair.Key, out _);
                }
            }

            foreach (var pair in _restedRoutes)
            {
                if (pair.Value <= now)
                {
                    _restedRoutes.TryRemove(pair.Key, out _);
                    _restReasons.TryRemove(pair.Key, out _);
                }
            }
        }

        // 收集当前被熔断/休息的路由（休息路由合并进同一视图，取更晚的解除时间）
        foreach (var pair in _blockedRoutes.Concat(_restedRoutes))
        {
            if (result.ContainsKey(pair.Key)) continue;
            var blockedUntil = _blockedRoutes.TryGetValue(pair.Key, out var blocked) ? blocked : (DateTimeOffset?)null;
            var restedUntil = _restedRoutes.TryGetValue(pair.Key, out var rested) ? rested : (DateTimeOffset?)null;
            var until = blockedUntil.HasValue && restedUntil.HasValue
                ? (blockedUntil > restedUntil ? blockedUntil : restedUntil)
                : blockedUntil ?? restedUntil;
            if (until is not { } untilValue) continue;

            var restReason = _restReasons.GetValueOrDefault(pair.Key, string.Empty);
            var failCount = _failCounts.GetValueOrDefault(pair.Key, 0);
            var effectiveUntil = untilValue > now ? untilValue : now;
            result[pair.Key] = new CircuitRouteInfo(
                IsBlocked: true,
                FailureCount: failCount,
                BlockedUntil: effectiveUntil,
                RemainingTime: effectiveUntil - now,
                Meta: _routeMeta.GetValueOrDefault(pair.Key),
                RestReason: string.IsNullOrWhiteSpace(restReason) ? null : restReason);
        }

        // 收集正在累计失败但尚未熔断的路由
        var failThreshold = Volatile.Read(ref _failThreshold);
        foreach (var pair in _failCounts)
        {
            if (result.ContainsKey(pair.Key)) continue;
            result[pair.Key] = new CircuitRouteInfo(
                IsBlocked: false,
                FailureCount: pair.Value,
                BlockedUntil: null,
                RemainingTime: null,
                Meta: _routeMeta.GetValueOrDefault(pair.Key));
        }

        return result;
    }

    /// <summary>
    /// 手动解除指定路由的熔断状态（同时清除失败计数、分级冷却与归属元数据）。
    /// </summary>
    public bool Reset(Guid routeId)
    {
        var removed = false;
        removed |= _blockedRoutes.TryRemove(routeId, out _);
        removed |= _restedRoutes.TryRemove(routeId, out _);
        removed |= _restReasons.TryRemove(routeId, out _);
        removed |= _failCounts.TryRemove(routeId, out _);
        removed |= _routeMeta.TryRemove(routeId, out _);
        return removed;
    }

    /// <summary>
    /// 解除所有路由的熔断状态。
    /// </summary>
    public int ResetAll()
    {
        var count = _blockedRoutes.Keys.Concat(_restedRoutes.Keys).Distinct().Count();
        _blockedRoutes.Clear();
        _restedRoutes.Clear();
        _restReasons.Clear();
        _failCounts.Clear();
        _routeMeta.Clear();
        return count;
    }
}

/// <summary>
/// 单条路由的熔断状态信息。
/// </summary>
public sealed record CircuitRouteInfo(
    bool IsBlocked,
    int FailureCount,
    DateTimeOffset? BlockedUntil,
    TimeSpan? RemainingTime,
    CircuitRouteMeta? Meta = null,
    string? RestReason = null);

/// <summary>
/// 熔断键对应的站点/模型归属，供面板在候选移除后仍能展示归属。
/// </summary>
public sealed record CircuitRouteMeta(string SiteName, string SiteModelName);
