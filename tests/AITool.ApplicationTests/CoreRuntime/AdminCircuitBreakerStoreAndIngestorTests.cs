using System.Text.Json;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;

namespace AITool.ApplicationTests.CoreRuntime;

/// <summary>
/// 验证 Admin 侧熔断状态变更事件存储的完整行为。
/// </summary>
public sealed class AdminCircuitBreakerStoreTests
{
    /// <summary>
    /// 单条添加后应能通过 List 查询到。
    /// </summary>
    [Fact]
    public void Add_single_event_is_listable()
    {
        var store = new AdminCircuitBreakerStore();
        var routeId = Guid.NewGuid();
        var evt = CreateCircuitBreakerEvent(routeId: routeId, failureCount: 5);

        store.Add(evt);

        store.Count.Should().Be(1);
        var list = store.List();
        list.Should().HaveCount(1);
        list[0].RouteId.Should().Be(routeId);
        list[0].FailureCount.Should().Be(5);
    }

    /// <summary>
    /// 按发生时间倒序排列，最新记录在前。
    /// </summary>
    [Fact]
    public void Add_orders_newest_first()
    {
        var store = new AdminCircuitBreakerStore();
        var old = CreateCircuitBreakerEvent(routeId: Guid.NewGuid(), failureCount: 1, occurredAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var mid = CreateCircuitBreakerEvent(routeId: Guid.NewGuid(), failureCount: 2, occurredAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var newest = CreateCircuitBreakerEvent(routeId: Guid.NewGuid(), failureCount: 3, occurredAt: DateTimeOffset.UtcNow);

        store.Add(old);
        store.Add(mid);
        store.Add(newest);

        var list = store.List();
        list[0].FailureCount.Should().Be(3);
        list[1].FailureCount.Should().Be(2);
        list[2].FailureCount.Should().Be(1);
    }

    /// <summary>
    /// 超过最大容量（200 条）时自动裁剪旧记录。
    /// </summary>
    [Fact]
    public void Add_trims_entries_beyond_max_capacity()
    {
        var store = new AdminCircuitBreakerStore();

        // 依次添加 205 条，时间递增确保不会因为过期被清理
        for (int i = 0; i < 205; i++)
        {
            store.Add(CreateCircuitBreakerEvent(routeId: Guid.NewGuid(), failureCount: i, occurredAt: DateTimeOffset.UtcNow.AddMinutes(i)));
        }

        store.Count.Should().Be(200);
        // 最新记录在前，第一条应为 failureCount=204
        var list = store.List();
        list[0].FailureCount.Should().Be(204);
        // 最旧记录应为 failureCount=5
        list[^1].FailureCount.Should().Be(5);
    }

    /// <summary>
    /// 过期记录（超过 6 小时）在下次操作时被清理。
    /// </summary>
    [Fact]
    public void Add_purges_expired_entries()
    {
        var store = new AdminCircuitBreakerStore();

        // 添加一条已过期 7 小时的记录
        var expired = CreateCircuitBreakerEvent(routeId: Guid.NewGuid(), failureCount: 1, occurredAt: DateTimeOffset.UtcNow.AddHours(-7));
        store.Add(expired);
        store.Count.Should().Be(1);

        // 添加一条新记录，触发过期清理
        var fresh = CreateCircuitBreakerEvent(routeId: Guid.NewGuid(), failureCount: 2, occurredAt: DateTimeOffset.UtcNow);
        store.Add(fresh);

        // 过期记录被清理，只保留新记录
        var list = store.List();
        list.Should().HaveCount(1);
        list[0].FailureCount.Should().Be(2);
    }

    /// <summary>
    /// GetLatest 返回最近一条熔断事件。
    /// </summary>
    [Fact]
    public void GetLatest_returns_most_recent_event()
    {
        var store = new AdminCircuitBreakerStore();
        store.Add(CreateCircuitBreakerEvent(routeId: Guid.NewGuid(), failureCount: 3, occurredAt: DateTimeOffset.UtcNow.AddMinutes(-5)));
        store.Add(CreateCircuitBreakerEvent(routeId: Guid.NewGuid(), failureCount: 5, occurredAt: DateTimeOffset.UtcNow));

        var latest = store.GetLatest();
        latest.Should().NotBeNull();
        latest!.FailureCount.Should().Be(5);
    }

    /// <summary>
    /// 空存储时 GetLatest 返回 null。
    /// </summary>
    [Fact]
    public void GetLatest_returns_null_when_empty()
    {
        var store = new AdminCircuitBreakerStore();

        store.GetLatest().Should().BeNull();
    }

    /// <summary>
    /// GetLatest 排除已过期的记录。
    /// </summary>
    [Fact]
    public void GetLatest_excludes_expired_entries()
    {
        var store = new AdminCircuitBreakerStore();
        // 只有过期记录
        store.Add(CreateCircuitBreakerEvent(routeId: Guid.NewGuid(), failureCount: 1, occurredAt: DateTimeOffset.UtcNow.AddHours(-7)));

        store.GetLatest().Should().BeNull();
    }

    /// <summary>
    /// List 返回的是深拷贝，修改不影响内部状态。
    /// </summary>
    [Fact]
    public void List_returns_defensive_copy()
    {
        var store = new AdminCircuitBreakerStore();
        store.Add(CreateCircuitBreakerEvent(routeId: Guid.NewGuid(), failureCount: 1));

        // 修改返回的列表不影响内部状态
        var list = store.List();
        var mutableList = list.ToList();
        mutableList.Clear();

        store.Count.Should().Be(1);
    }

    /// <summary>
    /// 辅助方法：创建一条测试用熔断状态变更事件。
    /// </summary>
    private static CoreCircuitBreakerEvent CreateCircuitBreakerEvent(
        Guid? routeId = null,
        int failureCount = 3,
        int failThreshold = 3,
        TimeSpan? blockDuration = null,
        DateTimeOffset? occurredAt = null)
    {
        var now = occurredAt ?? DateTimeOffset.UtcNow;
        var duration = blockDuration ?? TimeSpan.FromMinutes(5);
        return new CoreCircuitBreakerEvent
        {
            RouteId = routeId ?? Guid.NewGuid(),
            FailureCount = failureCount,
            FailThreshold = failThreshold,
            BlockDuration = duration,
            RecoveryTime = now + duration,
            OccurredAt = now
        };
    }
}

/// <summary>
/// 验证 Admin 侧熔断状态变更事件消费器的分发与反序列化行为。
/// </summary>
public sealed class AdminCircuitBreakerEventIngestorTests
{
    /// <summary>
    /// 混合事件批次中只消费 circuit-breaker 类型。
    /// </summary>
    [Fact]
    public async Task IngestCircuitBreakerEventsAsync_filters_by_event_type()
    {
        var store = new AdminCircuitBreakerStore();
        var ingestor = new AdminCircuitBreakerEventIngestor(
            store, LoggerStub.Create<AdminCircuitBreakerEventIngestor>());

        var routeA = Guid.NewGuid();
        var routeB = Guid.NewGuid();
        var envelopes = new List<CoreAdminEventEnvelope>
        {
            new() { SequenceId = 1, EventType = "usage-log", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
            new() { SequenceId = 2, EventType = "circuit-breaker", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = CreateCircuitBreakerPayload(routeId: routeA) },
            new() { SequenceId = 3, EventType = "developer-trace", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
            new() { SequenceId = 4, EventType = "circuit-breaker", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = CreateCircuitBreakerPayload(routeId: routeB) },
        };

        var maxSeq = await ingestor.IngestCircuitBreakerEventsAsync(envelopes);

        maxSeq.Should().Be(4);
        store.Count.Should().Be(2);
        var list = store.List();
        list.Should().Contain(e => e.RouteId == routeA);
        list.Should().Contain(e => e.RouteId == routeB);
    }

    /// <summary>
    /// 空批次返回 0，不触发任何写入。
    /// </summary>
    [Fact]
    public async Task IngestCircuitBreakerEventsAsync_returns_zero_for_empty_batch()
    {
        var store = new AdminCircuitBreakerStore();
        var ingestor = new AdminCircuitBreakerEventIngestor(
            store, LoggerStub.Create<AdminCircuitBreakerEventIngestor>());

        var maxSeq = await ingestor.IngestCircuitBreakerEventsAsync([]);

        maxSeq.Should().Be(0);
        store.Count.Should().Be(0);
    }

    /// <summary>
    /// 批次中没有 circuit-breaker 类型时返回 0。
    /// </summary>
    [Fact]
    public async Task IngestCircuitBreakerEventsAsync_returns_zero_when_no_matching_events()
    {
        var store = new AdminCircuitBreakerStore();
        var ingestor = new AdminCircuitBreakerEventIngestor(
            store, LoggerStub.Create<AdminCircuitBreakerEventIngestor>());

        var envelopes = new List<CoreAdminEventEnvelope>
        {
            new() { SequenceId = 1, EventType = "usage-log", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
            new() { SequenceId = 2, EventType = "conversation-turn", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
        };

        var maxSeq = await ingestor.IngestCircuitBreakerEventsAsync(envelopes);

        maxSeq.Should().Be(0);
        store.Count.Should().Be(0);
    }

    /// <summary>
    /// 无法反序列化的负载应被静默跳过，不影响其他有效事件。
    /// </summary>
    [Fact]
    public async Task IngestCircuitBreakerEventsAsync_skips_malformed_payloads()
    {
        var store = new AdminCircuitBreakerStore();
        var ingestor = new AdminCircuitBreakerEventIngestor(
            store, LoggerStub.Create<AdminCircuitBreakerEventIngestor>());

        var goodRouteId = Guid.NewGuid();
        var envelopes = new List<CoreAdminEventEnvelope>
        {
            new() { SequenceId = 1, EventType = "circuit-breaker", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "not-valid-json" },
            new() { SequenceId = 2, EventType = "circuit-breaker", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = CreateCircuitBreakerPayload(routeId: goodRouteId) },
            new() { SequenceId = 3, EventType = "circuit-breaker", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{ \"incomplete\": true" },
        };

        var maxSeq = await ingestor.IngestCircuitBreakerEventsAsync(envelopes);

        // 只有第 2 条有效，最大序号应为 2
        maxSeq.Should().Be(2);
        store.Count.Should().Be(1);
        store.List()[0].RouteId.Should().Be(goodRouteId);
    }

    /// <summary>
    /// 辅助方法：创建序列化的熔断状态变更事件负载。
    /// </summary>
    private static string CreateCircuitBreakerPayload(Guid? routeId = null, int failureCount = 3)
    {
        var now = DateTimeOffset.UtcNow;
        var evt = new CoreCircuitBreakerEvent
        {
            RouteId = routeId ?? Guid.NewGuid(),
            FailureCount = failureCount,
            FailThreshold = 3,
            BlockDuration = TimeSpan.FromMinutes(5),
            RecoveryTime = now + TimeSpan.FromMinutes(5),
            OccurredAt = now
        };
        return JsonSerializer.Serialize(evt, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
