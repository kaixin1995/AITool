using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Caching.Memory;

namespace AITool.Web.Services;

public enum AnalyticsQueueStatus
{
    Ready,
    Pending,
    QueueFull
}

public sealed class AnalyticsQueueResult<T>
{
    public AnalyticsQueueStatus Status { get; set; }
    public T? Result { get; set; }
}

// 将可视化统计限制为单消费者后台队列，避免请求线程直接承载长时间聚合。
public sealed class AnalyticsBackgroundQueryExecutor : BackgroundService
{
    private readonly IMemoryCache _memoryCache;
    private readonly Channel<AnalyticsJob> _queue;
    private readonly ConcurrentDictionary<string, AnalyticsJobState> _inflightJobs = new(StringComparer.Ordinal);

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

    public async Task<AnalyticsQueueResult<T>> EnqueueOrGetAsync<T>(
        string cacheKey,
        Func<CancellationToken, Task<T>> worker,
        TimeSpan waitBudget,
        CancellationToken cancellationToken)
    {
        if (_memoryCache.TryGetValue(cacheKey, out T? cached) && cached is not null)
        {
            return new AnalyticsQueueResult<T>
            {
                Status = AnalyticsQueueStatus.Ready,
                Result = cached
            };
        }

        var state = _inflightJobs.GetOrAdd(cacheKey, _ => new AnalyticsJobState());
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
                        CacheKey = cacheKey,
                        State = state
                    }))
                    {
                        _inflightJobs.TryRemove(cacheKey, out _);
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

    private sealed class AnalyticsJob
    {
        public string CacheKey { get; set; } = string.Empty;
        public AnalyticsJobState State { get; set; } = new();
    }

    private sealed class AnalyticsJobState
    {
        public object SyncRoot { get; } = new();
        public bool IsQueued { get; set; }
        public Func<CancellationToken, Task<object>>? Worker { get; set; }
        public TaskCompletionSource<object> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
