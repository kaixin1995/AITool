namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 路由级熔断状态存储，单条路由连续失败达到阈值后被临时屏蔽。
/// </summary>
public sealed class RouteCircuitStateStore
{
    private readonly object _syncRoot = new();
    private long _blockDurationTicks;
    private int _failThreshold;
    private long _restVersion;
    // 保留各站点最近一次配额休息版本，即使休息被清理或重置，旧查询也不能解除同站点的其他休息。
    private readonly Dictionary<Guid, long> _siteQuotaRestVersions = [];
    // 同一站点的配额耗尽写入与异步账号回启串行化；不得在持有 _syncRoot 时等待。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> _siteQuotaGates = [];

    private SemaphoreSlim QuotaGate(Guid siteId) => _siteQuotaGates.GetOrAdd(siteId, static _ => new SemaphoreSlim(1, 1));
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, int> _failCounts = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTimeOffset> _blockedRoutes = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, CircuitRouteMeta> _routeMeta = [];

    // 每种原因/物理站点独立保留期限与元数据；短限流不能覆盖长配额休息，恢复配额也不能清掉限流。
    // 所有读写均由 _syncRoot 保护，与恢复、过期清理、手动重置保持原子性。
    private readonly Dictionary<Guid, Dictionary<RestOwner, RestEntry>> _restedRoutes = [];
    private readonly record struct RestOwner(RouteRestKind Kind, Guid? SiteId);
    private sealed record RestEntry(DateTimeOffset UntilUtc, string Reason, CircuitRouteMeta? Meta, long Version);

    public RouteCircuitStateStore(TimeSpan? blockDuration = null, int failThreshold = 5)
    {
        _blockDurationTicks = (blockDuration ?? TimeSpan.FromMinutes(2)).Ticks;
        _failThreshold = failThreshold;
    }

    /// <summary>动态更新熔断参数，新配置应尽快影响后续请求。</summary>
    public void UpdateOptions(TimeSpan blockDuration, int failThreshold)
    {
        lock (_syncRoot)
        {
            Volatile.Write(ref _blockDurationTicks, blockDuration.Ticks);
            Volatile.Write(ref _failThreshold, failThreshold);
        }
    }

    /// <summary>记录一次失败，连续失败达到阈值时触发熔断。</summary>
    public void Block(Guid routeId, CircuitRouteMeta? meta = null)
    {
        lock (_syncRoot)
        {
            if (IsBlocked(routeId)) return;

            var count = _failCounts.AddOrUpdate(routeId, 1, (_, current) => current + 1);
            if (meta is not null)
            {
                _routeMeta[routeId] = meta;
            }

            if (count >= Volatile.Read(ref _failThreshold))
            {
                _blockedRoutes[routeId] = DateTimeOffset.UtcNow.AddTicks(Volatile.Read(ref _blockDurationTicks));
            }
        }
    }

    /// <summary>
    /// 分级冷却，不走失败计数，同时清空失败计数。同类/同归属休息保留最晚期限，
    /// 查询时与其他休息及普通熔断取最大值；kind/siteId 缺省兼容旧调用，但不参与配额恢复。
    /// 每次实际写入或更新休息时单调推进版本，确保健康查询期间新到的配额休息不会被旧查询结果解除。
    /// </summary>
    public void Rest(Guid routeId, DateTimeOffset untilUtc, string reason, CircuitRouteMeta? meta = null,
        RouteRestKind kind = RouteRestKind.Other, Guid? siteId = null)
    {
        var gate = kind == RouteRestKind.Quota && siteId.HasValue ? QuotaGate(siteId.Value) : null;
        gate?.Wait();
        try { RestCore(routeId, untilUtc, reason, meta, kind, siteId); }
        finally { gate?.Release(); }
    }

    private void RestCore(Guid routeId, DateTimeOffset untilUtc, string reason, CircuitRouteMeta? meta,
        RouteRestKind kind, Guid? siteId)
    {
        lock (_syncRoot)
        {
            if (untilUtc <= DateTimeOffset.UtcNow) return;

            var version = ++_restVersion;
            if (kind == RouteRestKind.Quota && siteId is { } quotaSiteId)
            {
                _siteQuotaRestVersions[quotaSiteId] = version;
            }

            if (!_restedRoutes.TryGetValue(routeId, out var rests))
            {
                rests = [];
                _restedRoutes[routeId] = rests;
            }

            var owner = new RestOwner(kind, siteId);
            rests.TryGetValue(owner, out var previous);
            if (previous is null || untilUtc >= previous.UntilUtc)
            {
                rests[owner] = new RestEntry(untilUtc,
                    string.IsNullOrWhiteSpace(reason) ? previous?.Reason ?? string.Empty : reason,
                    meta ?? previous?.Meta,
                    version);
            }
            else
            {
                rests[owner] = previous with { Version = version };
            }

            _failCounts.TryRemove(routeId, out _);
        }
    }

    /// <summary>
    /// 捕获用于解除配额休息的版本令牌。必须在健康查询发起前捕获，
    /// 确保查询期间新写入或刷新的配额休息拥有更高版本而得以保留。
    /// </summary>
    public QuotaRestReleaseToken CaptureQuotaRestReleaseToken(Guid siteId)
    {
        lock (_syncRoot)
        {
            return new(siteId, _restVersion);
        }
    }

    /// <summary>在同站点配额耗尽写入的互斥区内校验版本并执行异步回启。</summary>
    public async Task<bool> TryRunQuotaRecoveryAsync(QuotaRestReleaseToken token, Func<Task> recovery, CancellationToken cancellationToken)
    {
        var gate = QuotaGate(token.SiteId);
        await gate.WaitAsync(cancellationToken);
        try
        {
            lock (_syncRoot)
            {
                if (_siteQuotaRestVersions.GetValueOrDefault(token.SiteId) > token.Version) return false;
            }
            await recovery();
            return true;
        }
        finally { gate.Release(); }
    }

    /// <summary>
    /// 仅在新鲜配额查询确认恢复后调用：如果查询期间该站点出现新的配额休息，
    /// 则不释放该站点的任何配额休息。不触碰限流/其他休息、普通熔断或失败计数。
    /// 返回移除的配额休息数。
    /// </summary>
    public int ReleaseQuotaRests(QuotaRestReleaseToken token)
    {
        lock (_syncRoot)
        {
            if (_siteQuotaRestVersions.GetValueOrDefault(token.SiteId) > token.Version) return 0;

            var removed = 0;
            var owner = new RestOwner(RouteRestKind.Quota, token.SiteId);
            foreach (var pair in _restedRoutes.ToArray())
            {
                if (pair.Value.TryGetValue(owner, out var entry) && entry.Version <= token.Version)
                {
                    pair.Value.Remove(owner);
                    removed++;
                    if (pair.Value.Count == 0) _restedRoutes.Remove(pair.Key);
                }
            }

            return removed;
        }
    }

    /// <summary>查询有效休息信息，解除时间与普通熔断取更晚者。</summary>
    public (DateTimeOffset UntilUtc, string Reason)? GetRest(Guid routeId)
    {
        lock (_syncRoot)
        {
            var rest = GetActiveRest(routeId, DateTimeOffset.UtcNow);
            if (rest is null) return null;
            var until = _blockedRoutes.TryGetValue(routeId, out var blocked) && blocked > rest.UntilUtc
                ? blocked : rest.UntilUtc;
            return (until, rest.Reason);
        }
    }

    // 调用方持有 _syncRoot；原因与元数据始终来自最晚的有效休息，而非最后一次较短写入。
    private RestEntry? GetActiveRest(Guid routeId, DateTimeOffset now)
    {
        if (!_restedRoutes.TryGetValue(routeId, out var rests)) return null;
        RestEntry? latest = null;
        foreach (var pair in rests.ToArray())
        {
            if (pair.Value.UntilUtc <= now)
            {
                rests.Remove(pair.Key);
            }
            else if (latest is null || pair.Value.UntilUtc > latest.UntilUtc)
            {
                latest = pair.Value;
            }
        }

        if (rests.Count == 0) _restedRoutes.Remove(routeId);
        return latest;
    }

    /// <summary>记录成功，清除连续失败计数、休息与归属元数据（不解除已有普通熔断）。</summary>
    public void Succeed(Guid routeId)
    {
        lock (_syncRoot)
        {
            _failCounts.TryRemove(routeId, out _);
            _routeMeta.TryRemove(routeId, out _);
            _restedRoutes.Remove(routeId);
        }
    }

    /// <summary>判断路由当前是否仍处于熔断或休息窗口内。</summary>
    public bool IsBlocked(Guid routeId)
    {
        lock (_syncRoot)
        {
            var now = DateTimeOffset.UtcNow;
            if (_blockedRoutes.TryGetValue(routeId, out var until))
            {
                if (until > now) return true;
                _blockedRoutes.TryRemove(routeId, out _);
                _failCounts.TryRemove(routeId, out _);
                _routeMeta.TryRemove(routeId, out _);
            }

            return GetActiveRest(routeId, now) is not null;
        }
    }

    /// <summary>
    /// 返回熔断/休息及正在累计失败的路由快照。期限取普通熔断与全部休息的最大值。
    /// </summary>
    public IReadOnlyDictionary<Guid, CircuitRouteInfo> GetAllCircuitStates()
    {
        lock (_syncRoot)
        {
            var now = DateTimeOffset.UtcNow;
            var result = new Dictionary<Guid, CircuitRouteInfo>();
            foreach (var pair in _blockedRoutes)
            {
                if (pair.Value <= now)
                {
                    _blockedRoutes.TryRemove(pair.Key, out _);
                    _failCounts.TryRemove(pair.Key, out _);
                    _routeMeta.TryRemove(pair.Key, out _);
                }
            }

            foreach (var routeId in _blockedRoutes.Keys.Concat(_restedRoutes.Keys).Distinct().ToArray())
            {
                var rest = GetActiveRest(routeId, now);
                var until = _blockedRoutes.TryGetValue(routeId, out var blocked)
                    ? (DateTimeOffset?)blocked : null;
                if (rest is not null && (until is null || rest.UntilUtc > until)) until = rest.UntilUtc;
                if (until is null) continue;

                result[routeId] = new CircuitRouteInfo(
                    IsBlocked: true,
                    FailureCount: _failCounts.GetValueOrDefault(routeId, 0),
                    BlockedUntil: until,
                    RemainingTime: until.Value - now,
                    Meta: rest?.Meta ?? _routeMeta.GetValueOrDefault(routeId),
                    RestReason: string.IsNullOrWhiteSpace(rest?.Reason) ? null : rest.Reason);
            }

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
    }

    /// <summary>手动解除指定路由的全部熔断、休息、失败计数和元数据。</summary>
    public bool Reset(Guid routeId)
    {
        lock (_syncRoot)
        {
            var removed = _blockedRoutes.TryRemove(routeId, out _);
            removed |= _restedRoutes.Remove(routeId);
            removed |= _failCounts.TryRemove(routeId, out _);
            removed |= _routeMeta.TryRemove(routeId, out _);
            return removed;
        }
    }

    /// <summary>解除所有路由的熔断状态。</summary>
    public int ResetAll()
    {
        lock (_syncRoot)
        {
            var count = _blockedRoutes.Keys.Concat(_restedRoutes.Keys).Distinct().Count();
            _blockedRoutes.Clear();
            _restedRoutes.Clear();
            _failCounts.Clear();
            _routeMeta.Clear();
            return count;
        }
    }
}

/// <summary>休息类型。旧调用缺省 Other，不凭展示文案推断配额归属。</summary>
public enum RouteRestKind
{
    Other,
    Quota,
    RateLimit
}

/// <summary>配额休息解除令牌，记录目标站点及捕获时的全局休息版本栅栏。</summary>
public readonly record struct QuotaRestReleaseToken(Guid SiteId, long Version);

/// <summary>单条路由的熔断状态信息。</summary>
public sealed record CircuitRouteInfo(
    bool IsBlocked,
    int FailureCount,
    DateTimeOffset? BlockedUntil,
    TimeSpan? RemainingTime,
    CircuitRouteMeta? Meta = null,
    string? RestReason = null);

/// <summary>熔断键对应的站点/模型展示归属，不用于识别物理站点。</summary>
public sealed record CircuitRouteMeta(string SiteName, string SiteModelName);
