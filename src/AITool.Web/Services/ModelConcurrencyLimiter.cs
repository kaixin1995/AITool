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

    private ConcurrencyAcquireResult(bool acquired, SemaphoreSlim? semaphore)
    {
        Acquired = acquired;
        _semaphore = semaphore;
    }

    /// <summary>
    /// 未获取到许可的实例。
    /// </summary>
    public static ConcurrencyAcquireResult NotAcquired { get; } = new(false, null);

    /// <summary>
    /// 成功获取许可的实例。
    /// </summary>
    public static ConcurrencyAcquireResult AcquiredSlot(SemaphoreSlim semaphore) => new(true, semaphore);

    /// <summary>
    /// 释放并发许可，无论成功或异常都会正确释放。
    /// </summary>
    public void Dispose()
    {
        _semaphore?.Release();
    }
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
            return ConcurrencyAcquireResult.AcquiredSlot(new SemaphoreSlim(1));
        }

        var semaphore = _semaphores.GetOrAdd(key, _ => new SemaphoreSlim(maxConcurrency, maxConcurrency));

        if (mode == ConcurrencyAcquireMode.SkipOnFull)
        {
            // 打满则立即跳过，不排队。
            var acquired = await semaphore.WaitAsync(0, cancellationToken);
            return acquired
                ? ConcurrencyAcquireResult.AcquiredSlot(semaphore)
                : ConcurrencyAcquireResult.NotAcquired;
        }

        // WaitForSlot：排队等待，超时后返回 NotAcquired。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(queueTimeout);

        try
        {
            await semaphore.WaitAsync(cts.Token);
            return ConcurrencyAcquireResult.AcquiredSlot(semaphore);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 排队超时，但原始请求未被取消。
            return ConcurrencyAcquireResult.NotAcquired;
        }
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
    /// 拼接 SiteId + RemoteModelName 为缓存键。
    /// </summary>
    private static string BuildKey(Guid siteId, string remoteModelName)
    {
        return $"{siteId:N}:{remoteModelName}";
    }
}
