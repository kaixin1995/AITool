using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;

namespace AITool.IntegrationTests.Analytics;

/// <summary>
/// 验证 Analytics 后台队列在达到容量后能明确拒绝任务，避免丢弃任务后永久 Pending。
/// </summary>
public sealed class AnalyticsBackgroundQueryExecutorTests
{
    /// <summary>
    /// 队列已满时应返回 QueueFull，而不是把未执行的任务标记为在途任务。
    /// </summary>
    [Fact]
    public async Task EnqueueOrGet_returns_queue_full_when_bounded_queue_is_full()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var executor = new AnalyticsBackgroundQueryExecutor(cache);
        var releaseWorker = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workerStarted = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        await executor.StartAsync(CancellationToken.None);

        var results = new List<AnalyticsQueueResult<object>>();
        results.Add(await executor.EnqueueOrGetAsync(
            "queue-test-0",
            _ =>
            {
                workerStarted.TrySetResult(new object());
                return releaseWorker.Task;
            },
            TimeSpan.Zero,
            CancellationToken.None));
        await workerStarted.Task;

        for (var index = 1; index < 6; index++)
        {
            results.Add(await executor.EnqueueOrGetAsync(
                $"queue-test-{index}",
                _ => releaseWorker.Task,
                TimeSpan.Zero,
                CancellationToken.None));
        }

        results.Take(5).Should().OnlyContain(x => x.Status == AnalyticsQueueStatus.Pending);
        results[5].Status.Should().Be(AnalyticsQueueStatus.QueueFull);

        releaseWorker.TrySetResult(new object());
        await executor.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
    }
}
