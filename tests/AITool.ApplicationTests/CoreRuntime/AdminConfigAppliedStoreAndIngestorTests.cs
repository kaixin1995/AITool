using System.Text.Json;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;

namespace AITool.ApplicationTests.CoreRuntime;

/// <summary>
/// 验证 Admin 侧配置变更应用事件存储的完整行为。
/// </summary>
public sealed class AdminConfigAppliedStoreTests
{
    /// <summary>
    /// 单条添加后应能通过 List 查询到。
    /// </summary>
    [Fact]
    public void Add_single_event_is_listable()
    {
        var store = new AdminConfigAppliedStore();
        var evt = CreateConfigAppliedEvent(configVersion: 42, syncMode: "full");

        store.Add(evt);

        store.Count.Should().Be(1);
        var list = store.List();
        list.Should().HaveCount(1);
        list[0].ConfigVersion.Should().Be(42);
        list[0].SyncMode.Should().Be("full");
    }

    /// <summary>
    /// 按发生时间倒序排列，最新记录在前。
    /// </summary>
    [Fact]
    public void Add_orders_newest_first()
    {
        var store = new AdminConfigAppliedStore();
        var old = CreateConfigAppliedEvent(configVersion: 1, occurredAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var mid = CreateConfigAppliedEvent(configVersion: 2, occurredAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var newest = CreateConfigAppliedEvent(configVersion: 3, occurredAt: DateTimeOffset.UtcNow);

        store.Add(old);
        store.Add(mid);
        store.Add(newest);

        var list = store.List();
        list[0].ConfigVersion.Should().Be(3);
        list[1].ConfigVersion.Should().Be(2);
        list[2].ConfigVersion.Should().Be(1);
    }

    /// <summary>
    /// 超过最大容量（100 条）时自动裁剪旧记录。
    /// </summary>
    [Fact]
    public void Add_trims_entries_beyond_max_capacity()
    {
        var store = new AdminConfigAppliedStore();

        // 依次添加 105 条，时间递增确保不会因为过期被清理
        for (int i = 0; i < 105; i++)
        {
            store.Add(CreateConfigAppliedEvent(configVersion: i, occurredAt: DateTimeOffset.UtcNow.AddMinutes(i)));
        }

        store.Count.Should().Be(100);
        // 最新记录在前，第一条应为 version 104
        var list = store.List();
        list[0].ConfigVersion.Should().Be(104);
        // 最旧记录应为 version 5
        list[^1].ConfigVersion.Should().Be(5);
    }

    /// <summary>
    /// 过期记录（超过 24 小时）在下次操作时被清理。
    /// </summary>
    [Fact]
    public void Add_purges_expired_entries()
    {
        var store = new AdminConfigAppliedStore();

        // 添加一条已过期 25 小时的记录
        var expired = CreateConfigAppliedEvent(configVersion: 1, occurredAt: DateTimeOffset.UtcNow.AddHours(-25));
        store.Add(expired);
        store.Count.Should().Be(1);

        // 添加一条新记录，触发过期清理
        var fresh = CreateConfigAppliedEvent(configVersion: 2, occurredAt: DateTimeOffset.UtcNow);
        store.Add(fresh);

        // 过期记录被清理，只保留新记录
        var list = store.List();
        list.Should().HaveCount(1);
        list[0].ConfigVersion.Should().Be(2);
    }

    /// <summary>
    /// GetLatest 返回最近一条配置变更事件。
    /// </summary>
    [Fact]
    public void GetLatest_returns_most_recent_event()
    {
        var store = new AdminConfigAppliedStore();
        store.Add(CreateConfigAppliedEvent(configVersion: 10, occurredAt: DateTimeOffset.UtcNow.AddMinutes(-5)));
        store.Add(CreateConfigAppliedEvent(configVersion: 20, occurredAt: DateTimeOffset.UtcNow));

        var latest = store.GetLatest();
        latest.Should().NotBeNull();
        latest!.ConfigVersion.Should().Be(20);
    }

    /// <summary>
    /// 空存储时 GetLatest 返回 null。
    /// </summary>
    [Fact]
    public void GetLatest_returns_null_when_empty()
    {
        var store = new AdminConfigAppliedStore();

        store.GetLatest().Should().BeNull();
    }

    /// <summary>
    /// GetLatest 排除已过期的记录。
    /// </summary>
    [Fact]
    public void GetLatest_excludes_expired_entries()
    {
        var store = new AdminConfigAppliedStore();
        // 只有过期记录
        store.Add(CreateConfigAppliedEvent(configVersion: 1, occurredAt: DateTimeOffset.UtcNow.AddHours(-25)));

        store.GetLatest().Should().BeNull();
    }

    /// <summary>
    /// List 返回的是深拷贝，修改不影响内部状态。
    /// </summary>
    [Fact]
    public void List_returns_defensive_copy()
    {
        var store = new AdminConfigAppliedStore();
        store.Add(CreateConfigAppliedEvent(configVersion: 1));

        // 修改返回的列表不影响内部状态
        var list = store.List();
        var mutableList = list.ToList();
        mutableList.Clear();

        store.Count.Should().Be(1);
    }

    /// <summary>
    /// 辅助方法：创建一条测试用配置变更应用事件。
    /// </summary>
    private static CoreConfigAppliedEvent CreateConfigAppliedEvent(
        long configVersion = 1,
        string configHash = "test-hash",
        string syncMode = "full",
        List<string>? changedCategories = null,
        long previousConfigVersion = 0,
        string previousConfigHash = "",
        DateTimeOffset? occurredAt = null)
    {
        return new CoreConfigAppliedEvent
        {
            ConfigVersion = configVersion,
            ConfigHash = configHash,
            SyncMode = syncMode,
            ChangedCategories = changedCategories ?? [],
            PreviousConfigVersion = previousConfigVersion,
            PreviousConfigHash = previousConfigHash,
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow
        };
    }
}

/// <summary>
/// 验证 Admin 侧配置变更应用事件消费器的分发与反序列化行为。
/// </summary>
public sealed class AdminConfigAppliedEventIngestorTests
{
    /// <summary>
    /// 混合事件批次中只消费 config-applied 类型。
    /// </summary>
    [Fact]
    public async Task IngestConfigAppliedEventsAsync_filters_by_event_type()
    {
        var store = new AdminConfigAppliedStore();
        var ingestor = new AdminConfigAppliedEventIngestor(
            store, LoggerStub.Create<AdminConfigAppliedEventIngestor>());

        var envelopes = new List<CoreAdminEventEnvelope>
        {
            new() { SequenceId = 1, EventType = "usage-log", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
            new() { SequenceId = 2, EventType = "config-applied", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = CreateConfigAppliedPayload(configVersion: 100) },
            new() { SequenceId = 3, EventType = "developer-trace", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
            new() { SequenceId = 4, EventType = "config-applied", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = CreateConfigAppliedPayload(configVersion: 200) },
        };

        var maxSeq = await ingestor.IngestConfigAppliedEventsAsync(envelopes);

        maxSeq.Should().Be(4);
        store.Count.Should().Be(2);
        var list = store.List();
        list.Should().Contain(e => e.ConfigVersion == 100);
        list.Should().Contain(e => e.ConfigVersion == 200);
    }

    /// <summary>
    /// 空批次返回 0，不触发任何写入。
    /// </summary>
    [Fact]
    public async Task IngestConfigAppliedEventsAsync_returns_zero_for_empty_batch()
    {
        var store = new AdminConfigAppliedStore();
        var ingestor = new AdminConfigAppliedEventIngestor(
            store, LoggerStub.Create<AdminConfigAppliedEventIngestor>());

        var maxSeq = await ingestor.IngestConfigAppliedEventsAsync([]);

        maxSeq.Should().Be(0);
        store.Count.Should().Be(0);
    }

    /// <summary>
    /// 批次中没有 config-applied 类型时返回 0。
    /// </summary>
    [Fact]
    public async Task IngestConfigAppliedEventsAsync_returns_zero_when_no_matching_events()
    {
        var store = new AdminConfigAppliedStore();
        var ingestor = new AdminConfigAppliedEventIngestor(
            store, LoggerStub.Create<AdminConfigAppliedEventIngestor>());

        var envelopes = new List<CoreAdminEventEnvelope>
        {
            new() { SequenceId = 1, EventType = "usage-log", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
            new() { SequenceId = 2, EventType = "conversation-turn", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
        };

        var maxSeq = await ingestor.IngestConfigAppliedEventsAsync(envelopes);

        maxSeq.Should().Be(0);
        store.Count.Should().Be(0);
    }

    /// <summary>
    /// 无法反序列化的负载应被静默跳过，不影响其他有效事件。
    /// </summary>
    [Fact]
    public async Task IngestConfigAppliedEventsAsync_skips_malformed_payloads()
    {
        var store = new AdminConfigAppliedStore();
        var ingestor = new AdminConfigAppliedEventIngestor(
            store, LoggerStub.Create<AdminConfigAppliedEventIngestor>());

        var envelopes = new List<CoreAdminEventEnvelope>
        {
            new() { SequenceId = 1, EventType = "config-applied", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "not-valid-json" },
            new() { SequenceId = 2, EventType = "config-applied", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = CreateConfigAppliedPayload(configVersion: 42) },
            new() { SequenceId = 3, EventType = "config-applied", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{ \"incomplete\": true" },
        };

        var maxSeq = await ingestor.IngestConfigAppliedEventsAsync(envelopes);

        // 只有第 2 条有效，最大序号应为 2
        maxSeq.Should().Be(2);
        store.Count.Should().Be(1);
        store.List()[0].ConfigVersion.Should().Be(42);
    }

    /// <summary>
    /// 辅助方法：创建序列化的配置变更应用事件负载。
    /// </summary>
    private static string CreateConfigAppliedPayload(long configVersion = 1, string syncMode = "full")
    {
        var evt = new CoreConfigAppliedEvent
        {
            ConfigVersion = configVersion,
            ConfigHash = $"hash-{configVersion}",
            SyncMode = syncMode,
            ChangedCategories = syncMode == "full" ? [] : ["Sites"],
            PreviousConfigVersion = configVersion - 1,
            PreviousConfigHash = $"hash-{configVersion - 1}",
            OccurredAt = DateTimeOffset.UtcNow
        };
        return JsonSerializer.Serialize(evt, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
