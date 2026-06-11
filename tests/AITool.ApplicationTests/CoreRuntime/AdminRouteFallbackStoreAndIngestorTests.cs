using System.Text.Json;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;

namespace AITool.ApplicationTests.CoreRuntime;

/// <summary>
/// 验证 Admin 侧路由回退事件存储和消费器的完整行为。
/// </summary>
public sealed class AdminRouteFallbackStoreTests
{
    /// <summary>
    /// 单条添加后应能通过 List 查询到。
    /// </summary>
    [Fact]
    public void Add_single_event_is_listable()
    {
        var store = new AdminRouteFallbackStore();
        var evt = CreateFallbackEvent("model-a", reason: "timeout");

        store.Add(evt);

        store.Count.Should().Be(1);
        var list = store.List();
        list.Should().HaveCount(1);
        list[0].RequestModel.Should().Be("model-a");
        list[0].Reason.Should().Be("timeout");
    }

    /// <summary>
    /// 批量添加多条记录。
    /// </summary>
    [Fact]
    public void AddRange_adds_multiple_events()
    {
        var store = new AdminRouteFallbackStore();
        var events = Enumerable.Range(0, 5)
            .Select(i => CreateFallbackEvent($"model-{i}", reason: $"error-{i}"))
            .ToList();

        store.AddRange(events);

        store.Count.Should().Be(5);
    }

    /// <summary>
    /// 超过最大容量（200 条）时自动裁剪旧记录。
    /// </summary>
    [Fact]
    public void Add_trims_entries_beyond_max_capacity()
    {
        var store = new AdminRouteFallbackStore();

        // 依次添加 205 条，时间递增确保不会因为过期被清理
        for (int i = 0; i < 205; i++)
        {
            store.Add(CreateFallbackEvent($"model-{i}", occurredAt: DateTimeOffset.UtcNow.AddMinutes(i)));
        }

        store.Count.Should().Be(200);
        // 最新记录在前，第一条应为 model-204
        var list = store.List();
        list[0].RequestModel.Should().Be("model-204");
        // 最旧记录应为 model-5
        list[^1].RequestModel.Should().Be("model-5");
    }

    /// <summary>
    /// 过期记录（超过 6 小时）在下次操作时被清理。
    /// </summary>
    [Fact]
    public void Add_purges_expired_entries()
    {
        var store = new AdminRouteFallbackStore();

        // 添加一条已过期 7 小时的记录
        var expired = CreateFallbackEvent("expired", occurredAt: DateTimeOffset.UtcNow.AddHours(-7));
        store.Add(expired);
        store.Count.Should().Be(1);

        // 添加一条新记录，触发过期清理
        var fresh = CreateFallbackEvent("fresh", occurredAt: DateTimeOffset.UtcNow);
        store.Add(fresh);

        // 过期记录被清理，只保留新记录
        var list = store.List();
        list.Should().HaveCount(1);
        list[0].RequestModel.Should().Be("fresh");
    }

    /// <summary>
    /// GetSummary 返回正确的统计信息。
    /// </summary>
    [Fact]
    public void GetSummary_returns_correct_counts()
    {
        var store = new AdminRouteFallbackStore();
        var siteA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var siteB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var siteC = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        store.Add(CreateFallbackEvent("model-1", fromSiteId: siteA, toSiteId: siteB));
        store.Add(CreateFallbackEvent("model-2", fromSiteId: siteA, toSiteId: siteC));
        store.Add(CreateFallbackEvent("model-3", fromSiteId: siteB, toSiteId: siteC));

        var (totalCount, uniqueFromSites, uniqueToSites) = store.GetSummary();
        totalCount.Should().Be(3);
        uniqueFromSites.Should().Be(2); // siteA, siteB
        uniqueToSites.Should().Be(2);   // siteB, siteC
    }

    /// <summary>
    /// List 返回的是深拷贝，修改不影响内部状态。
    /// </summary>
    [Fact]
    public void List_returns_defensive_copy()
    {
        var store = new AdminRouteFallbackStore();
        store.Add(CreateFallbackEvent("model-a"));

        // 修改返回的列表不影响内部状态
        var list = store.List();
        var mutableList = list.ToList();
        mutableList.Clear();

        store.Count.Should().Be(1);
    }

    /// <summary>
    /// 辅助方法：创建一条测试用路由回退事件。
    /// </summary>
    private static CoreRouteFallbackEvent CreateFallbackEvent(
        string requestModel = "test-model",
        string reason = "test-error",
        Guid? fromSiteId = null,
        Guid? toSiteId = null,
        DateTimeOffset? occurredAt = null)
    {
        return new CoreRouteFallbackEvent
        {
            RequestId = Guid.NewGuid(),
            RequestModel = requestModel,
            FromRouteId = Guid.NewGuid(),
            FromSiteId = fromSiteId ?? Guid.NewGuid(),
            FromSiteModelName = $"from-{requestModel}",
            ToRouteId = Guid.NewGuid(),
            ToSiteId = toSiteId ?? Guid.NewGuid(),
            ToSiteModelName = $"to-{requestModel}",
            Reason = reason,
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// 验证 Admin 侧路由回退事件消费器的分发与反序列化行为。
/// </summary>
public sealed class AdminRouteFallbackEventIngestorTests
{
    /// <summary>
    /// 混合事件批次中只消费 route-fallback 类型。
    /// </summary>
    [Fact]
    public async Task IngestRouteFallbackEventsAsync_filters_by_event_type()
    {
        var store = new AdminRouteFallbackStore();
        var ingestor = new AdminRouteFallbackEventIngestor(
            store, LoggerStub.Create<AdminRouteFallbackEventIngestor>());

        var envelopes = new List<CoreAdminEventEnvelope>
        {
            new() { SequenceId = 1, EventType = "usage-log", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
            new() { SequenceId = 2, EventType = "route-fallback", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = CreateFallbackPayload("model-a") },
            new() { SequenceId = 3, EventType = "developer-trace", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
            new() { SequenceId = 4, EventType = "route-fallback", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = CreateFallbackPayload("model-b") },
        };

        var maxSeq = await ingestor.IngestRouteFallbackEventsAsync(envelopes);

        maxSeq.Should().Be(4);
        store.Count.Should().Be(2);
        var list = store.List();
        list.Should().Contain(e => e.RequestModel == "model-a");
        list.Should().Contain(e => e.RequestModel == "model-b");
    }

    /// <summary>
    /// 空批次返回 0，不触发任何写入。
    /// </summary>
    [Fact]
    public async Task IngestRouteFallbackEventsAsync_returns_zero_for_empty_batch()
    {
        var store = new AdminRouteFallbackStore();
        var ingestor = new AdminRouteFallbackEventIngestor(
            store, LoggerStub.Create<AdminRouteFallbackEventIngestor>());

        var maxSeq = await ingestor.IngestRouteFallbackEventsAsync([]);

        maxSeq.Should().Be(0);
        store.Count.Should().Be(0);
    }

    /// <summary>
    /// 批次中没有 route-fallback 类型时返回 0。
    /// </summary>
    [Fact]
    public async Task IngestRouteFallbackEventsAsync_returns_zero_when_no_matching_events()
    {
        var store = new AdminRouteFallbackStore();
        var ingestor = new AdminRouteFallbackEventIngestor(
            store, LoggerStub.Create<AdminRouteFallbackEventIngestor>());

        var envelopes = new List<CoreAdminEventEnvelope>
        {
            new() { SequenceId = 1, EventType = "usage-log", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
            new() { SequenceId = 2, EventType = "conversation-turn", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
        };

        var maxSeq = await ingestor.IngestRouteFallbackEventsAsync(envelopes);

        maxSeq.Should().Be(0);
        store.Count.Should().Be(0);
    }

    /// <summary>
    /// 无法反序列化的负载应被静默跳过，不影响其他有效事件。
    /// </summary>
    [Fact]
    public async Task IngestRouteFallbackEventsAsync_skips_malformed_payloads()
    {
        var store = new AdminRouteFallbackStore();
        var ingestor = new AdminRouteFallbackEventIngestor(
            store, LoggerStub.Create<AdminRouteFallbackEventIngestor>());

        var envelopes = new List<CoreAdminEventEnvelope>
        {
            new() { SequenceId = 1, EventType = "route-fallback", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "not-valid-json" },
            new() { SequenceId = 2, EventType = "route-fallback", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = CreateFallbackPayload("good-model") },
            new() { SequenceId = 3, EventType = "route-fallback", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{ \"incomplete\": true" },
        };

        var maxSeq = await ingestor.IngestRouteFallbackEventsAsync(envelopes);

        // 只有第 2 条有效，最大序号应为 2
        maxSeq.Should().Be(2);
        store.Count.Should().Be(1);
        store.List()[0].RequestModel.Should().Be("good-model");
    }

    /// <summary>
    /// 辅助方法：创建序列化的路由回退事件负载。
    /// </summary>
    private static string CreateFallbackPayload(string requestModel)
    {
        var evt = new CoreRouteFallbackEvent
        {
            RequestId = Guid.NewGuid(),
            RequestModel = requestModel,
            FromRouteId = Guid.NewGuid(),
            FromSiteId = Guid.NewGuid(),
            FromSiteModelName = $"from-{requestModel}",
            ToRouteId = Guid.NewGuid(),
            ToSiteId = Guid.NewGuid(),
            ToSiteModelName = $"to-{requestModel}",
            Reason = "test-reason",
            OccurredAt = DateTimeOffset.UtcNow
        };
        return JsonSerializer.Serialize(evt, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
