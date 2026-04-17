using AITool.Infrastructure.Proxy;
using FluentAssertions;

namespace AITool.ApplicationTests.Proxy;

// 熔断状态存储测试，验证屏蔽与过期自动恢复机制
public sealed class RouteCircuitStateStoreTests
{
    // 站点被屏蔽后应在窗口期内处于熔断状态
    [Fact]
    public void IsBlocked_returns_true_within_block_window()
    {
        var store = new RouteCircuitStateStore(TimeSpan.FromMinutes(2));
        var siteId = Guid.NewGuid();

        store.Block(siteId);

        store.IsBlocked(siteId).Should().BeTrue();
    }

    // 超过屏蔽窗口后站点应自动恢复为可用
    [Fact]
    public void IsBlocked_returns_false_after_block_expires()
    {
        var store = new RouteCircuitStateStore(TimeSpan.FromMilliseconds(100));
        var siteId = Guid.NewGuid();

        store.Block(siteId);
        Thread.Sleep(150);

        store.IsBlocked(siteId).Should().BeFalse();
    }

    // 未被屏蔽的站点不应处于熔断状态
    [Fact]
    public void IsBlocked_returns_false_for_never_blocked_site()
    {
        var store = new RouteCircuitStateStore();
        var siteId = Guid.NewGuid();

        store.IsBlocked(siteId).Should().BeFalse();
    }

    // 多次屏蔽同一站点应刷新过期时间
    [Fact]
    public void Block_refreshes_expiration_on_repeated_calls()
    {
        var store = new RouteCircuitStateStore(TimeSpan.FromMilliseconds(200));
        var siteId = Guid.NewGuid();

        store.Block(siteId);
        Thread.Sleep(100);
        store.Block(siteId);
        Thread.Sleep(120);

        // 第二次 Block 刷新了窗口，仍然应处于熔断状态
        store.IsBlocked(siteId).Should().BeTrue();
    }
}
