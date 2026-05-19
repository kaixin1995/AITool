using System.Collections.Concurrent;
using System.Threading;
using AITool.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Web.Services;

/// <summary>
/// 并发打满时的处理策略。
/// </summary>
public enum ConcurrencyAcquireMode
{
    /// <summary>
    /// 跳到下一顺位模型。
    /// </summary>
    SkipOnFull = 0,
    /// <summary>
    /// 排队等待直到释放或超时。
    /// </summary>
    WaitForSlot = 1
}

/// <summary>
/// 并发获取结果，表示是否成功拿到许可，以及用于释放的句柄。
/// </summary>
public sealed class ConcurrencyAcquireResult : IDisposable
{
    /// <summary>
    /// 是否成功获取到并发许可。
    /// </summary>
    public bool Acquired { get; }

    private readonly SemaphoreSlim? _semaphore;
    private readonly Action? _releaseAction;
    private int _disposed;

    private ConcurrencyAcquireResult(bool acquired, SemaphoreSlim? semaphore, Action? releaseAction)
    {
        Acquired = acquired;
        _semaphore = semaphore;
        _releaseAction = releaseAction;
    }

    /// <summary>
    /// 未获取到许可的实例。
    /// </summary>
    public static ConcurrencyAcquireResult NotAcquired { get; } = new(false, null, null);

    /// <summary>
    /// 成功获取许可的实例。
    /// </summary>
    public static ConcurrencyAcquireResult AcquiredSlot(SemaphoreSlim? semaphore, Action? releaseAction = null) => new(true, semaphore, releaseAction);

    /// <summary>
    /// 释放并发许可，无论成功或异常都会正确释放。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _semaphore?.Release();
        _releaseAction?.Invoke();
    }
}

/// <summary>
/// 模型并发快照，用于调试页面展示最近出现过的站点模型及其实时并发数。
/// </summary>
public sealed class ActiveModelConcurrencyEntry
{
    /// <summary>
    /// 站点标识。
    /// </summary>
    public Guid SiteId { get; init; }
    /// <summary>
    /// 站点模型名称。
    /// </summary>
    public string SiteModelName { get; init; } = string.Empty;
    /// <summary>
    /// 当前活跃并发数。
    /// </summary>
    public int ActiveCount { get; init; }
    /// <summary>
    /// 最近一次进入或离开活跃态的时间。
    /// </summary>
    public DateTimeOffset LastSeenAt { get; init; }
}

/// <summary>
/// 按 SiteId + RemoteModelName 粒度控制最大并发数。
/// 限制为 0 表示不限制，请求直接通过；大于 0 时根据模式跳过或排队。
/// </summary>
public sealed class ModelConcurrencyLimiter
{
    /// <summary>
    /// 每个 SiteId + RemoteModelName 组合对应的信号量。
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new(StringComparer.Ordinal);

    /// <summary>
    /// 当前活跃中的真实并发计数，只在成功拿到槽位后递增，请求结束时递减。
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _activeCounts = new(StringComparer.Ordinal);

    /// <summary>
    /// 最近出现过的模型并发展示元数据，归零后仍会保留一段时间，避免调试页瞬间清空。
    /// </summary>
    private readonly ConcurrentDictionary<string, ActiveModelConcurrencyEntry> _activeEntries = new(StringComparer.Ordinal);

    /// <summary>
    /// 缓存每个 SiteId + RemoteModelName 组合的最大并发数。
    /// </summary>
    private volatile Dictionary<string, int> _limits = [];

    /// <summary>
    /// 上次刷新时间的 UTC Ticks，用 long 支持 Interlocked 原子操作。
    /// </summary>
    private long _lastRefreshedAtTicks = DateTimeOffset.MinValue.UtcTicks;

    /// <summary>
    /// 刷新间隔。
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 调试页默认保留最近 6 小时内出现过的模型并发记录。
    /// </summary>
    public static readonly TimeSpan RecentRetention = TimeSpan.FromHours(6);

    /// <summary>
    /// 按模式获取指定站点模型的并发许可。
    /// SkipOnFull：打满立即返回 NotAcquired；
    /// WaitForSlot：排队等待直到释放或超时。
    /// </summary>
    public async ValueTask<ConcurrencyAcquireResult> AcquireAsync(
        IServiceProvider serviceProvider,
        Guid siteId,
        string remoteModelName,
        ConcurrencyAcquireMode mode,
        TimeSpan queueTimeout,
        CancellationToken cancellationToken)
    {
        var limits = GetOrRefreshLimits(serviceProvider);
        var key = BuildKey(siteId, remoteModelName);

        if (!limits.TryGetValue(key, out var maxConcurrency) || maxConcurrency <= 0)
        {
            return CreateTrackedAcquireResult(key, siteId, remoteModelName, null);
        }

        var semaphore = _semaphores.GetOrAdd(key, _ => new SemaphoreSlim(maxConcurrency, maxConcurrency));

        if (mode == ConcurrencyAcquireMode.SkipOnFull)
        {
            // 打满则立即跳过，不排队。
            var acquired = await semaphore.WaitAsync(0, cancellationToken);
            return acquired
                ? CreateTrackedAcquireResult(key, siteId, remoteModelName, semaphore)
                : ConcurrencyAcquireResult.NotAcquired;
        }

        // WaitForSlot：排队等待，超时后返回 NotAcquired。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(queueTimeout);

        try
        {
            await semaphore.WaitAsync(cts.Token);
            return CreateTrackedAcquireResult(key, siteId, remoteModelName, semaphore);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 排队超时，但原始请求未被取消。
            return ConcurrencyAcquireResult.NotAcquired;
        }
    }

    /// <summary>
    /// 返回当前活跃的真实模型并发快照，只包含并发数大于 0 的项。
    /// </summary>
    public IReadOnlyList<ActiveModelConcurrencyEntry> ListActive()
    {
        return _activeEntries.Values
            .Where(x => x.ActiveCount > 0)
            .OrderByDescending(x => x.ActiveCount)
            .ThenBy(x => x.SiteModelName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SiteId)
            .ToList();
    }

    /// <summary>
    /// 返回最近保留窗口内出现过的模型并发快照，归零后的项也会保留显示。
    /// </summary>
    public IReadOnlyList<ActiveModelConcurrencyEntry> ListRecent(TimeSpan retention)
    {
        var cutoff = DateTimeOffset.UtcNow - retention;

        foreach (var pair in _activeEntries)
        {
            if (pair.Value.ActiveCount <= 0 && pair.Value.LastSeenAt < cutoff)
            {
                _activeEntries.TryRemove(pair.Key, out _);
            }
        }

        return _activeEntries.Values
            .Where(x => x.ActiveCount > 0 || x.LastSeenAt >= cutoff)
            .OrderByDescending(x => x.ActiveCount)
            .ThenByDescending(x => x.LastSeenAt)
            .ThenBy(x => x.SiteModelName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SiteId)
            .ToList();
    }

    /// <summary>
    /// 从数据库加载最新的并发配置，超过刷新间隔才真正查询。
    /// </summary>
    private Dictionary<string, int> GetOrRefreshLimits(IServiceProvider serviceProvider)
    {
        var lastTicks = Interlocked.Read(ref _lastRefreshedAtTicks);
        if (_limits.Count > 0 && DateTimeOffset.UtcNow.UtcTicks - lastTicks < RefreshInterval.Ticks)
        {
            return _limits;
        }

        try
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var mappings = db.SiteModelMappings
                .Where(m => m.IsEnabled && m.MaxConcurrency > 0)
                .ToList();

            var newLimits = new Dictionary<string, int>(mappings.Count, StringComparer.Ordinal);
            foreach (var mapping in mappings)
            {
                var key = BuildKey(mapping.SiteId, mapping.RemoteModelName);
                newLimits[key] = mapping.MaxConcurrency;
            }

            _limits = newLimits;
            Interlocked.Exchange(ref _lastRefreshedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
        }
        catch
        {
            // 查询失败时保留旧配置，不影响正常转发。
        }

        return _limits;
    }

    /// <summary>
    /// 创建带真实活跃计数的并发句柄，只在请求真正开始占用模型时才记数。
    /// </summary>
    private ConcurrencyAcquireResult CreateTrackedAcquireResult(string key, Guid siteId, string remoteModelName, SemaphoreSlim? semaphore)
    {
        var activeCount = _activeCounts.AddOrUpdate(key, 1, static (_, current) => current + 1);
        _activeEntries[key] = new ActiveModelConcurrencyEntry
        {
            SiteId = siteId,
            SiteModelName = remoteModelName,
            ActiveCount = activeCount,
            LastSeenAt = DateTimeOffset.UtcNow
        };

        return ConcurrencyAcquireResult.AcquiredSlot(semaphore, () => ReleaseActiveCount(key, siteId, remoteModelName));
    }

    /// <summary>
    /// 请求结束后回收当前活跃计数，归零时保留最近记录，便于调试页在 6 小时窗口内显示 0 并发。
    /// </summary>
    private void ReleaseActiveCount(string key, Guid siteId, string remoteModelName)
    {
        while (true)
        {
            if (!_activeCounts.TryGetValue(key, out var current))
            {
                _activeEntries[key] = new ActiveModelConcurrencyEntry
                {
                    SiteId = siteId,
                    SiteModelName = remoteModelName,
                    ActiveCount = 0,
                    LastSeenAt = DateTimeOffset.UtcNow
                };
                return;
            }

            if (current <= 1)
            {
                if (_activeCounts.TryRemove(key, out _))
                {
                    _activeEntries[key] = new ActiveModelConcurrencyEntry
                    {
                        SiteId = siteId,
                        SiteModelName = remoteModelName,
                        ActiveCount = 0,
                        LastSeenAt = DateTimeOffset.UtcNow
                    };
                    return;
                }

                continue;
            }

            var next = current - 1;
            if (_activeCounts.TryUpdate(key, next, current))
            {
                _activeEntries[key] = new ActiveModelConcurrencyEntry
                {
                    SiteId = siteId,
                    SiteModelName = remoteModelName,
                    ActiveCount = next,
                    LastSeenAt = DateTimeOffset.UtcNow
                };
                return;
            }
        }
    }

    /// <summary>
    /// 拼接 SiteId + RemoteModelName 为缓存键。
    /// </summary>
    private static string BuildKey(Guid siteId, string remoteModelName)
    {
        return $"{siteId:N}:{remoteModelName}";
    }
}
