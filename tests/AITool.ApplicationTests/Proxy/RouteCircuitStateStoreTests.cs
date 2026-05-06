using AITool.Infrastructure.Proxy;
using FluentAssertions;

namespace AITool.ApplicationTests.Proxy;

// 熔断状态存储测试，验证连续失败阈值、过期恢复与成功清零逻辑
public sealed class RouteCircuitStateStoreTests
{
    // 连续失败达到阈值后站点应进入熔断状态
    [Fact]
    public void IsBlocked_returns_true_when_failure_threshold_is_reached()
    {
        var store = new RouteCircuitStateStore(TimeSpan.FromMinutes(2), failThreshold: 2);
        var siteId = Guid.NewGuid();

        store.Block(siteId);
        store.IsBlocked(siteId).Should().BeFalse();

        store.Block(siteId);

        store.IsBlocked(siteId).Should().BeTrue();
    }

    // 超过屏蔽窗口后站点应自动恢复为可用
    [Fact]
    public void IsBlocked_returns_false_after_block_expires()
    {
        var store = new RouteCircuitStateStore(TimeSpan.FromMilliseconds(100), failThreshold: 1);
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

    // 成功后应清除连续失败计数，后续失败需重新累计到阈值
    [Fact]
    public void Succeed_clears_failure_count_before_threshold_is_reached_again()
    {
        var store = new RouteCircuitStateStore(TimeSpan.FromMinutes(2), failThreshold: 2);
        var siteId = Guid.NewGuid();

        store.Block(siteId);
        store.Succeed(siteId);
        store.Block(siteId);

        store.IsBlocked(siteId).Should().BeFalse();
    }

    // 已熔断时重复失败不应刷新过期时间
    [Fact]
    public void Block_does_not_refresh_expiration_for_already_blocked_site()
    {
        var store = new RouteCircuitStateStore(TimeSpan.FromMilliseconds(100), failThreshold: 1);
        var siteId = Guid.NewGuid();

        store.Block(siteId);
        Thread.Sleep(50);
        store.Block(siteId);
        Thread.Sleep(70);

        store.IsBlocked(siteId).Should().BeFalse();
    }

    // 熔断过期后应清空失败计数，后续失败需要重新累计。
    [Fact]
    public void Block_requires_a_new_failure_streak_after_block_expires()
    {
        var store = new RouteCircuitStateStore(TimeSpan.FromMilliseconds(100), failThreshold: 2);
        var siteId = Guid.NewGuid();

        store.Block(siteId);
        store.Block(siteId);
        Thread.Sleep(150);

        store.IsBlocked(siteId).Should().BeFalse();

        store.Block(siteId);

        store.IsBlocked(siteId).Should().BeFalse();
    }
}
