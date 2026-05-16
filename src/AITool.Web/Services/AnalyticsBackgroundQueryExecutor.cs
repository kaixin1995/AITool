using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;

namespace AITool.Web.Services;

/// <summary>
/// 后台统计查询任务的状态。
/// </summary>
public enum AnalyticsQueueStatus
{
    /// <summary>
    /// 已拿到结果，可以直接返回。
    /// </summary>
    Ready,

    /// <summary>
    /// 任务仍在排队或执行中。
    /// </summary>
    Pending,

    /// <summary>
    /// 队列已满，本次任务未能入队。
    /// </summary>
    QueueFull
}

/// <summary>
/// 后台统计查询的统一返回结构。
/// </summary>
public sealed class AnalyticsQueueResult<T>
{
    /// <summary>
    /// 当前任务状态。
    /// </summary>
    public AnalyticsQueueStatus Status { get; set; }

    /// <summary>
    /// 查询结果，只有在状态为 Ready 时才会有值。
    /// </summary>
    public T? Result { get; set; }
}

/// <summary>
/// 将可视化统计查询限制为单消费者后台队列，避免请求线程直接承载长时间聚合。
/// </summary>
public sealed class AnalyticsBackgroundQueryExecutor : BackgroundService
{
    /// <summary>
    /// 统计缓存键前缀。
    /// </summary>
    private const string CacheKeyPrefix = "analytics-dashboard:";

    /// <summary>
    /// 内存缓存，用于短时间复用统计结果。
    /// </summary>
    private readonly IMemoryCache _memoryCache;

    /// <summary>
    /// 后台统计任务队列。
    /// </summary>
    private readonly Channel<AnalyticsJob> _queue;

    /// <summary>
    /// 当前正在排队或执行的任务集合，避免同一查询重复入队。
    /// </summary>
    private readonly ConcurrentDictionary<string, AnalyticsJobState> _inflightJobs = new(StringComparer.Ordinal);

    /// <summary>
    /// 缓存版本号，批量失效时通过递增版本实现逻辑隔离。
    /// </summary>
    private int _cacheVersion;

    /// <summary>
    /// 初始化后台统计查询执行器。
    /// </summary>
    public AnalyticsBackgroundQueryExecutor(IMemoryCache memoryCache)
    {
        _memoryCache = memoryCache;
        _queue = Channel.CreateBounded<AnalyticsJob>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    }

    /// <summary>
    /// 优先返回缓存结果；没有缓存时尝试复用在途任务或将查询加入后台队列。
    /// </summary>
    public async Task<AnalyticsQueueResult<T>> EnqueueOrGetAsync<T>(
        string cacheKey,
        Func<CancellationToken, Task<T>> worker,
        TimeSpan waitBudget,
        CancellationToken cancellationToken)
    {
        var versionedCacheKey = BuildVersionedCacheKey(cacheKey);
        if (_memoryCache.TryGetValue(versionedCacheKey, out T? cached) && cached is not null)
        {
            return new AnalyticsQueueResult<T>
            {
                Status = AnalyticsQueueStatus.Ready,
                Result = cached
            };
        }

        var state = _inflightJobs.GetOrAdd(versionedCacheKey, _ => new AnalyticsJobState());
        if (!state.IsQueued)
        {
            lock (state.SyncRoot)
            {
                if (!state.IsQueued)
                {
                    state.IsQueued = true;
                    state.Worker = async ct => (object)(await worker(ct))!;

                    if (!_queue.Writer.TryWrite(new AnalyticsJob
                    {
                        CacheKey = versionedCacheKey,
                        State = state
                    }))
                    {
                        _inflightJobs.TryRemove(versionedCacheKey, out _);
                        state.IsQueued = false;
                        return new AnalyticsQueueResult<T>
                        {
                            Status = AnalyticsQueueStatus.QueueFull
                        };
                    }
                }
            }
        }

        if (waitBudget <= TimeSpan.Zero)
        {
            return new AnalyticsQueueResult<T> { Status = AnalyticsQueueStatus.Pending };
        }

        // 为同步等待设置独立超时，超时后返回 Pending，但不取消后台任务本身。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(waitBudget);

        try
        {
            var result = await state.Completion.Task.WaitAsync(timeoutCts.Token);
            if (result is T typed)
            {
                return new AnalyticsQueueResult<T>
                {
                    Status = AnalyticsQueueStatus.Ready,
                    Result = typed
                };
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AnalyticsQueueResult<T> { Status = AnalyticsQueueStatus.Pending };
        }

        return new AnalyticsQueueResult<T> { Status = AnalyticsQueueStatus.Pending };
    }

    /// <summary>
    /// 使所有已缓存的统计结果整体失效。
    /// </summary>
    public void InvalidateAll()
    {
        Interlocked.Increment(ref _cacheVersion);
    }

    /// <summary>
    /// 持续消费后台查询队列，并将结果写入缓存和等待对象。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var result = await job.State.Worker!(stoppingToken);
                _memoryCache.Set(job.CacheKey, result, TimeSpan.FromSeconds(20));
                job.State.Completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                job.State.Completion.TrySetException(ex);
            }
            finally
            {
                _inflightJobs.TryRemove(job.CacheKey, out _);
            }
        }
    }

    /// <summary>
    /// 基于当前缓存版本生成实际缓存键。
    /// </summary>
    private string BuildVersionedCacheKey(string cacheKey)
    {
        return $"{CacheKeyPrefix}{Volatile.Read(ref _cacheVersion)}:{cacheKey}";
    }

    /// <summary>
    /// 队列中的单个统计任务描述。
    /// </summary>
    private sealed class AnalyticsJob
    {
        /// <summary>
        /// 实际缓存键。
        /// </summary>
        public string CacheKey { get; set; } = string.Empty;

        /// <summary>
        /// 共享任务状态。
        /// </summary>
        public AnalyticsJobState State { get; set; } = new();
    }

    /// <summary>
    /// 同一统计任务在排队和执行期间共享的状态对象。
    /// </summary>
    private sealed class AnalyticsJobState
    {
        /// <summary>
        /// 控制任务是否已入队的同步锁对象。
        /// </summary>
        public object SyncRoot { get; } = new();

        /// <summary>
        /// 标记任务是否已经进入队列。
        /// </summary>
        public bool IsQueued { get; set; }

        /// <summary>
        /// 实际执行查询的委托。
        /// </summary>
        public Func<CancellationToken, Task<object>>? Worker { get; set; }

        /// <summary>
        /// 用于通知等待方任务完成。
        /// </summary>
        public TaskCompletionSource<object> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
