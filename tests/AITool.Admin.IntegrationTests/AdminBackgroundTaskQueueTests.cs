using AITool.Admin.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.Admin.IntegrationTests.Services;

/// <summary>
/// 验证管理后台长任务由宿主队列执行，并且单个任务异常不会阻塞后续任务。
/// </summary>
public sealed class AdminBackgroundTaskQueueTests
{
    [Fact]
    public async Task Queue_executes_work_and_continues_after_failure()
    {
        var queue = new AdminBackgroundTaskQueue(NullLogger<AdminBackgroundTaskQueue>.Instance);
        await queue.StartAsync(CancellationToken.None);

        try
        {
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            queue.TryQueue(_ => throw new InvalidOperationException("expected test failure")).Should().BeTrue();
            queue.TryQueue(_ =>
            {
                completed.TrySetResult(true);
                return Task.CompletedTask;
            }).Should().BeTrue();

            (await completed.Task.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
        }
    }
}
