using System.Collections.Concurrent;
using System.Threading;
using AITool.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Web.Services;

/// <summary>
/// 按 SiteId + RemoteModelName 粒度控制最大并发数。
/// 限制为 0 表示不限制，请求直接通过；大于 0 时排队等待。
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
    /// 获取指定站点模型的并发许可，如果已达到上限则排队等待。
    /// </summary>
    public async ValueTask<IDisposable> AcquireAsync(
        IServiceProvider serviceProvider,
        Guid siteId,
        string remoteModelName,
        CancellationToken cancellationToken)
    {
        var limits = GetOrRefreshLimits(serviceProvider);
        var key = BuildKey(siteId, remoteModelName);

        if (!limits.TryGetValue(key, out var maxConcurrency) || maxConcurrency <= 0)
        {
            return NoopDisposable.Instance;
        }

        var semaphore = _semaphores.GetOrAdd(key, _ => new SemaphoreSlim(maxConcurrency, maxConcurrency));
        await semaphore.WaitAsync(cancellationToken);

        return new SemaphoreReleaser(semaphore);
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

    /// <summary>
    /// 信号量释放器。
    /// </summary>
    private sealed class SemaphoreReleaser(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() => semaphore.Release();
    }

    /// <summary>
    /// 空操作占位，用于不限制并发时直接返回。
    /// </summary>
    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
