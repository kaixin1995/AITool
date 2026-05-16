using AITool.Infrastructure.Proxy;
using FluentAssertions;

namespace AITool.ApplicationTests.Proxy;

/// <summary>
/// 验证熔断状态存储在失败累计、过期恢复和成功重置时的行为是否符合预期。
/// </summary>
public sealed class RouteCircuitStateStoreTests
{
    /// <summary>
    /// 失败累计达到阈值后，同一个站点应立即进入屏蔽状态。
    /// </summary>
    [Fact]
    public void IsBlocked_returns_true_when_failure_threshold_is_reached()
    {
        // 将阈值设为 2，便于直接验证第二次失败触发熔断。
        var store = new RouteCircuitStateStore(TimeSpan.FromMinutes(2), failThreshold: 2);
        // 使用独立站点标识，避免不同测试之间互相影响。
        var siteId = Guid.NewGuid();

        // 第一次失败只累计次数，还不应真正屏蔽。
        store.Block(siteId);
        store.IsBlocked(siteId).Should().BeFalse();

        // 第二次失败达到阈值，站点应被标记为熔断。
        store.Block(siteId);

        store.IsBlocked(siteId).Should().BeTrue();
    }

    /// <summary>
    /// 屏蔽时间窗结束后，站点应自动恢复为可用状态。
    /// </summary>
    [Fact]
    public void IsBlocked_returns_false_after_block_expires()
    {
        // 使用很短的过期时间，减少测试等待时长。
        var store = new RouteCircuitStateStore(TimeSpan.FromMilliseconds(100), failThreshold: 1);
        // 这里的站点标识只用于本次过期恢复验证。
        var siteId = Guid.NewGuid();

        store.Block(siteId);
        // 等待超过屏蔽时长，模拟熔断自然过期。
        Thread.Sleep(150);

        store.IsBlocked(siteId).Should().BeFalse();
    }

    /// <summary>
    /// 从未记录失败的站点，不应被误判为熔断中。
    /// </summary>
    [Fact]
    public void IsBlocked_returns_false_for_never_blocked_site()
    {
        // 使用默认配置即可验证初始状态。
        var store = new RouteCircuitStateStore();
        // 选择一个全新的站点标识，确认查询结果只取决于存储状态。
        var siteId = Guid.NewGuid();

        store.IsBlocked(siteId).Should().BeFalse();
    }

    /// <summary>
    /// 成功调用后应清空失败累计，后续失败需要重新从头计算。
    /// </summary>
    [Fact]
    public void Succeed_clears_failure_count_before_threshold_is_reached_again()
    {
        // 阈值仍设为 2，用于观察成功是否打断原有失败链路。
        var store = new RouteCircuitStateStore(TimeSpan.FromMinutes(2), failThreshold: 2);
        // 当前测试只跟踪一个站点的失败计数。
        var siteId = Guid.NewGuid();

        // 先累计一次失败。
        store.Block(siteId);
        // 成功返回后，应把前面的失败记录视为已恢复。
        store.Succeed(siteId);
        // 再次失败时应重新从第一次数起，因此不会立刻熔断。
        store.Block(siteId);

        store.IsBlocked(siteId).Should().BeFalse();
    }

    /// <summary>
    /// 站点已经熔断时，再次记录失败不应延长原有过期时间。
    /// </summary>
    [Fact]
    public void Block_does_not_refresh_expiration_for_already_blocked_site()
    {
        // 单次失败即可熔断，便于观察重复 Block 是否刷新过期时间。
        var store = new RouteCircuitStateStore(TimeSpan.FromMilliseconds(100), failThreshold: 1);
        // 使用单独站点，确保断言只受本次调用影响。
        var siteId = Guid.NewGuid();

        store.Block(siteId);
        // 先等待一半时间，再次写入失败记录。
        Thread.Sleep(50);
        // 如果实现错误刷新了过期时间，后面的断言就会失败。
        store.Block(siteId);
        // 总等待时间已经超过第一次熔断的有效期。
        Thread.Sleep(70);

        store.IsBlocked(siteId).Should().BeFalse();
    }

    /// <summary>
    /// 熔断自然过期后，新的失败应重新开启一轮累计，而不是沿用旧次数。
    /// </summary>
    [Fact]
    public void Block_requires_a_new_failure_streak_after_block_expires()
    {
        // 阈值设为 2，方便验证过期后第一次失败不会直接再次熔断。
        var store = new RouteCircuitStateStore(TimeSpan.FromMilliseconds(100), failThreshold: 2);
        // 该标识代表需要经历两次失败才能被屏蔽的同一个站点。
        var siteId = Guid.NewGuid();

        store.Block(siteId);
        store.Block(siteId);
        // 等待此前熔断状态过期。
        Thread.Sleep(150);

        store.IsBlocked(siteId).Should().BeFalse();

        // 过期后的第一次失败应视为新的失败序列起点。
        store.Block(siteId);

        store.IsBlocked(siteId).Should().BeFalse();
    }
}
