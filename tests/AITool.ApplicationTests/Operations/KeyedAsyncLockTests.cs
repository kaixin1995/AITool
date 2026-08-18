using AITool.Infrastructure.Common;
using FluentAssertions;

namespace AITool.ApplicationTests.Operations;

/// <summary>
/// 验证按键异步锁的串行化和释放后复用行为。
/// </summary>
public sealed class KeyedAsyncLockTests
{
    /// <summary>
    /// 同一键必须串行，不同键不应互相阻塞；租约释放后同一键可以再次获取。
    /// </summary>
    [Fact]
    public async Task Same_key_is_serialized_and_reusable_after_release()
    {
        var keyedLock = new KeyedAsyncLock();
        using var firstLease = await keyedLock.WaitAsync("same", CancellationToken.None);

        var waitingLeaseTask = keyedLock.WaitAsync("same", CancellationToken.None);
        waitingLeaseTask.IsCompleted.Should().BeFalse();

        using (await keyedLock.WaitAsync("other", CancellationToken.None))
        {
        }

        firstLease.Dispose();
        using var secondLease = await waitingLeaseTask.WaitAsync(TimeSpan.FromSeconds(1));
        secondLease.Should().NotBeNull();
    }
}
