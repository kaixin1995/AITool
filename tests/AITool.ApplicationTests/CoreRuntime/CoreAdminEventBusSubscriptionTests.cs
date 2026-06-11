using System.Threading.Channels;
using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;

namespace AITool.ApplicationTests.CoreRuntime;

/// <summary>
/// CoreAdminEventBus SSE 订阅机制单元测试。
/// 验证多订阅者广播、死引用清理、有界通道 DropOltest 行为。
/// </summary>
public sealed class CoreAdminEventBusSubscriptionTests
{
    /// <summary>
    /// Subscribe 应创建独立的订阅对象，每个订阅者拥有独立的通知通道。
    /// </summary>
    [Fact]
    public void Subscribe_creates_independent_subscription()
    {
        var bus = new CoreAdminEventBus();

        using var sub1 = bus.Subscribe();
        using var sub2 = bus.Subscribe();

        sub1.Should().NotBeNull();
        sub2.Should().NotBeNull();
        sub1.Should().NotBeSameAs(sub2);
    }

    /// <summary>
    /// NotifyNewEvents 应向所有活跃订阅者广播事件序号。
    /// 每个订阅者独立接收，互不影响。
    /// </summary>
    [Fact]
    public async Task NotifyNewEvents_broadcasts_to_all_subscribers()
    {
        var bus = new CoreAdminEventBus();
        using var sub1 = bus.Subscribe();
        using var sub2 = bus.Subscribe();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        bus.NotifyNewEvents(42);

        var result1 = await sub1.WaitNextAsync(cts.Token);
        var result2 = await sub2.WaitNextAsync(cts.Token);

        result1.Should().Be(42);
        result2.Should().Be(42);
    }

    /// <summary>
    /// 多次 NotifyNewEvents 应按序推送到订阅者通道。
    /// </summary>
    [Fact]
    public async Task NotifyNewEvents_delivers_multiple_notifications_in_order()
    {
        var bus = new CoreAdminEventBus();
        using var sub = bus.Subscribe();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        bus.NotifyNewEvents(1);
        bus.NotifyNewEvents(2);
        bus.NotifyNewEvents(3);

        (await sub.WaitNextAsync(cts.Token)).Should().Be(1);
        (await sub.WaitNextAsync(cts.Token)).Should().Be(2);
        (await sub.WaitNextAsync(cts.Token)).Should().Be(3);
    }

    /// <summary>
    /// 已 Dispose 的订阅者（被 GC 回收后），NotifyNewEvents 应自动清理死引用。
    /// 清理后不影响其他活跃订阅者。
    /// </summary>
    [Fact]
    public async Task NotifyNewEvents_cleans_up_dead_references()
    {
        var bus = new CoreAdminEventBus();

        // 创建一个弱订阅并立即释放
        CreateAndForgetSubscription(bus);

        // 强制 GC 回收已 Dispose 的订阅者
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // 活跃订阅者应不受影响
        using var activeSub = bus.Subscribe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        bus.NotifyNewEvents(100);

        var result = await activeSub.WaitNextAsync(cts.Token);
        result.Should().Be(100);
    }

    /// <summary>
    /// 有界通道深度为 64，超出后应丢弃最旧的通知（DropOldest）。
    /// 验证写入 65 条通知后，最早的通知被丢弃。
    /// </summary>
    [Fact]
    public async Task Bounded_channel_drops_oldest_when_full()
    {
        var bus = new CoreAdminEventBus();
        using var sub = bus.Subscribe();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // 写入 65 条通知，超出通道深度 64
        for (long i = 1; i <= 65; i++)
        {
            bus.NotifyNewEvents(i);
        }

        // 通道中应有 64 条，最早的一条（序号 1）被 DropOldest 丢弃
        var first = await sub.WaitNextAsync(cts.Token);
        first.Should().Be(2, "序号 1 应被 DropOldest 丢弃");

        // 继续读取剩余 63 条，最后一条应为 65
        for (var i = 3; i <= 65; i++)
        {
            var val = await sub.WaitNextAsync(cts.Token);
            val.Should().Be(i);
        }

        // 通道应为空，再次读取应超时
        using var emptyCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => sub.WaitNextAsync(emptyCts.Token).AsTask());
    }

    /// <summary>
    /// 没有订阅者时 NotifyNewEvents 不应抛异常。
    /// </summary>
    [Fact]
    public void NotifyNewEvents_does_not_throw_when_no_subscribers()
    {
        var bus = new CoreAdminEventBus();

        var act = () => bus.NotifyNewEvents(1);

        act.Should().NotThrow();
    }

    /// <summary>
    /// Dispose 后的订阅不应再收到通知，WaitNextAsync 应抛出 ChannelClosedException。
    /// </summary>
    [Fact]
    public async Task Disposed_subscription_stops_receiving()
    {
        var bus = new CoreAdminEventBus();
        var sub = bus.Subscribe();

        sub.Dispose();

        // 通道已关闭，WaitNextAsync 应抛出异常
        await Assert.ThrowsAsync<ChannelClosedException>(
            () => sub.WaitNextAsync(CancellationToken.None).AsTask());

        // 通知不应抛异常，只是写入失败被静默忽略
        var act = () => bus.NotifyNewEvents(42);
        act.Should().NotThrow();
    }

    /// <summary>
    /// 创建一个订阅但不保留引用，用于测试 GC 清理。
    /// </summary>
    private static void CreateAndForgetSubscription(CoreAdminEventBus bus)
    {
        var sub = bus.Subscribe();
        sub.Dispose();
        // sub 离开作用域后可被 GC 回收
    }
}
