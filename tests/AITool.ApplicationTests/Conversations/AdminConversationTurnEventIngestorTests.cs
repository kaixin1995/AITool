using System.Text.Json;
using AITool.Application.CoreRuntime;
using AITool.ApplicationTests.CoreRuntime;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Conversations;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace AITool.ApplicationTests.Conversations;

/// <summary>
/// 验证 Admin 侧对话记录事件消费器的核心逻辑：过滤、反序列化、去重、批量写入。
/// 使用 <see cref="StubConversationLogStore"/> 替身避免依赖真实文件系统。
/// </summary>
public sealed class AdminConversationTurnEventIngestorTests
{
    private readonly StubConversationLogStore _store;
    private readonly AdminConversationTurnEventIngestor _ingestor;

    public AdminConversationTurnEventIngestorTests()
    {
        _store = new StubConversationLogStore();
        _ingestor = new AdminConversationTurnEventIngestor(
            _store, LoggerStub.Create<AdminConversationTurnEventIngestor>());
    }

    /// <summary>
    /// 空事件列表应返回 0，不写入任何记录。
    /// </summary>
    [Fact]
    public async Task IngestConversationTurnEventsAsync_returns_zero_for_empty_list()
    {
        var result = await _ingestor.IngestConversationTurnEventsAsync([]);

        result.Should().Be(0);
        _store.WrittenLogs.Should().BeEmpty();
    }

    /// <summary>
    /// 全部为非 conversation-turn 类型时应返回 0。
    /// </summary>
    [Fact]
    public async Task IngestConversationTurnEventsAsync_returns_zero_for_non_matching_events()
    {
        var envelopes = new List<CoreAdminEventEnvelope>
        {
            new() { SequenceId = 1, EventType = "usage-log", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
            new() { SequenceId = 2, EventType = "detection", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" }
        };

        var result = await _ingestor.IngestConversationTurnEventsAsync(envelopes);

        result.Should().Be(0);
        _store.WrittenLogs.Should().BeEmpty();
    }

    /// <summary>
    /// 有效 conversation-turn 事件应被反序列化并写入存储。
    /// </summary>
    [Fact]
    public async Task IngestConversationTurnEventsAsync_writes_valid_events()
    {
        var payload = CreateSampleConversationTurnEvent();
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var envelopes = new List<CoreAdminEventEnvelope>
        {
            new() { SequenceId = 5, EventType = "conversation-turn", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = json }
        };

        var result = await _ingestor.IngestConversationTurnEventsAsync(envelopes);

        result.Should().Be(5);
        _store.WrittenLogs.Should().ContainSingle();
        var log = _store.WrittenLogs[0];
        log.RequestId.Should().Be(payload.RequestId);
        log.SessionId.Should().Be(payload.SessionId);
        log.SourceTool.Should().Be(payload.SourceTool);
        log.UserInputText.Should().Be(payload.UserInputText);
        log.AssistantOutputMarkdown.Should().Be(payload.AssistantOutputMarkdown);
        log.InputTokens.Should().Be(payload.InputTokens);
        log.OutputTokens.Should().Be(payload.OutputTokens);
        log.ConversationGroupKey.Should().Be(payload.ConversationGroupKey);
    }

    /// <summary>
    /// 相同 RequestId 的重复事件应被去重，只保留最早的一条。
    /// </summary>
    [Fact]
    public async Task IngestConversationTurnEventsAsync_deduplicates_by_request_id()
    {
        var payload = CreateSampleConversationTurnEvent();
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        // 三条相同 RequestId 的事件，SequenceId 递增
        var envelopes = new List<CoreAdminEventEnvelope>
        {
            new() { SequenceId = 1, EventType = "conversation-turn", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = json },
            new() { SequenceId = 2, EventType = "conversation-turn", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = json },
            new() { SequenceId = 3, EventType = "conversation-turn", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = json }
        };

        var result = await _ingestor.IngestConversationTurnEventsAsync(envelopes);

        // 返回的最大序号应为 3（全部 conversation-turn 事件中的最大值）
        result.Should().Be(3);
        // 去重后只写入 1 条
        _store.WrittenLogs.Should().ContainSingle();
    }

    /// <summary>
    /// 不同 RequestId 的事件应分别写入，不做去重。
    /// </summary>
    [Fact]
    public async Task IngestConversationTurnEventsAsync_keeps_distinct_request_ids()
    {
        var payload1 = CreateSampleConversationTurnEvent(requestId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var payload2 = CreateSampleConversationTurnEvent(requestId: Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var json1 = JsonSerializer.Serialize(payload1, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var json2 = JsonSerializer.Serialize(payload2, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var envelopes = new List<CoreAdminEventEnvelope>
        {
            new() { SequenceId = 10, EventType = "conversation-turn", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = json1 },
            new() { SequenceId = 20, EventType = "conversation-turn", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = json2 }
        };

        var result = await _ingestor.IngestConversationTurnEventsAsync(envelopes);

        result.Should().Be(20);
        _store.WrittenLogs.Should().HaveCount(2);
    }

    /// <summary>
    /// PayloadJson 无法反序列化的事件应被跳过，不影响其他有效事件。
    /// </summary>
    [Fact]
    public async Task IngestConversationTurnEventsAsync_skips_malformed_payloads()
    {
        var validPayload = CreateSampleConversationTurnEvent();
        var validJson = JsonSerializer.Serialize(validPayload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var envelopes = new List<CoreAdminEventEnvelope>
        {
            // 有效事件
            new() { SequenceId = 1, EventType = "conversation-turn", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = validJson },
            // 无效 JSON — 反序列化失败，会被跳过
            new() { SequenceId = 2, EventType = "conversation-turn", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "not-valid-json{{{" }
        };

        var result = await _ingestor.IngestConversationTurnEventsAsync(envelopes);

        // 最大序号应包含有效事件（第一条有效，序号为 1；第二条解析失败不计入 parsedEvents）
        result.Should().Be(1);
        // 只有第一条被成功写入
        _store.WrittenLogs.Should().ContainSingle();
        _store.WrittenLogs[0].RequestId.Should().Be(validPayload.RequestId);
    }

    /// <summary>
    /// 混合 usage-log 和 conversation-turn 事件时，只消费 conversation-turn 类型。
    /// </summary>
    [Fact]
    public async Task IngestConversationTurnEventsAsync_filters_by_event_type()
    {
        var payload = CreateSampleConversationTurnEvent();
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var envelopes = new List<CoreAdminEventEnvelope>
        {
            new() { SequenceId = 100, EventType = "usage-log", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
            new() { SequenceId = 101, EventType = "conversation-turn", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = json },
            new() { SequenceId = 102, EventType = "detection", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" }
        };

        var result = await _ingestor.IngestConversationTurnEventsAsync(envelopes);

        result.Should().Be(101);
        _store.WrittenLogs.Should().ContainSingle();
    }

    /// <summary>
    /// 创建一个包含合理默认值的对话记录事件负载。
    /// </summary>
    private static CoreConversationTurnEvent CreateSampleConversationTurnEvent(
        Guid? requestId = null)
    {
        return new CoreConversationTurnEvent
        {
            RequestId = requestId ?? Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            AccessKeyId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            SourceTool = "claude-code",
            SessionId = "session-001",
            ConversationGroupKey = "group-key-001",
            RequestModel = "claude-sonnet-4-6",
            ProtocolType = "OpenAI",
            RequestPath = "/v1/chat/completions",
            Source = "proxy",
            UserInputText = "你好，世界",
            AssistantOutputMarkdown = "你好！我是 AI 助手。",
            InputTokens = 15,
            CachedTokens = 3,
            OutputTokens = 8,
            IsStreaming = false,
            Status = "success",
            MetadataJson = "{}",
            ConversationTitle = "测试对话",
            CreatedAt = new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero)
        };
    }
}
