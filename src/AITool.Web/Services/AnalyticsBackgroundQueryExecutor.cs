using Microsoft.Extensions.Caching.Memory;

namespace AITool.Web.Services;

// 将可视化查询串行放到后台长任务线程执行，避免高频统计抢占前台线程池。
public sealed class AnalyticsBackgroundQueryExecutor
{
    private readonly IMemoryCache _memoryCache;
    private readonly SemaphoreSlim _queryGate = new(1, 1);

    public AnalyticsBackgroundQueryExecutor(IMemoryCache memoryCache)
    {
        _memoryCache = memoryCache;
    }

    public async Task<T> ExecuteAsync<T>(
        string cacheKey,
        Func<CancellationToken, Task<T>> worker,
        CancellationToken cancellationToken)
    {
        if (_memoryCache.TryGetValue(cacheKey, out T? cached) && cached is not null)
        {
            return cached;
        }

        await _queryGate.WaitAsync(cancellationToken);
        try
        {
            if (_memoryCache.TryGetValue(cacheKey, out cached) && cached is not null)
            {
                return cached;
            }

            var result = await Task.Factory.StartNew(
                    () => worker(cancellationToken),
                    cancellationToken,
                    TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)
                .Unwrap();

            // 给统计结果一个较短缓存，减少同筛选条件反复重算的开销。
            _memoryCache.Set(cacheKey, result, TimeSpan.FromSeconds(20));
            return result;
        }
        finally
        {
            _queryGate.Release();
        }
    }
}
