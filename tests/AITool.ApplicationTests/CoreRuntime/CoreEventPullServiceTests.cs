using System.Net;
using System.Text.Json;
using AITool.Application.Conversations;
using AITool.Application.CoreRuntime;
using AITool.Admin.Services;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Conversations;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AITool.ApplicationTests.CoreRuntime;

/// <summary>
/// 验证 Admin 侧事件拉取服务的 Replay → Ingest → Ack 完整流程。
/// 使用自定义 HttpMessageHandler 模拟 Core 宿主的 HTTP 接口，不需要启动真实服务器。
/// </summary>
public sealed class CoreEventPullServiceTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly AdminUsageLogEventIngestor _usageLogIngestor;
    private readonly StubConversationLogStore _conversationStore;
    private readonly AdminConversationTurnEventIngestor _conversationTurnIngestor;
    private readonly AdminDeveloperTraceEventIngestor _developerTraceIngestor;
    private readonly AdminRouteFallbackEventIngestor _routeFallbackIngestor;
    private readonly AdminConfigAppliedEventIngestor _configAppliedIngestor;
    private readonly AdminCircuitBreakerEventIngestor _circuitBreakerIngestor;
    private readonly CoreEventAckStateStore _ackStateStore;
    private readonly string _ackMetaPath;

    /// <summary>
    /// 构造独立内存数据库和消费器实例。
    /// 为 ack 状态持久化创建独立的临时目录，测试结束后随 Dispose 清理。
    /// </summary>
    public CoreEventPullServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);
        _usageLogIngestor = new AdminUsageLogEventIngestor(_dbContext);
        _conversationStore = new StubConversationLogStore();
        _conversationTurnIngestor = new AdminConversationTurnEventIngestor(
            _conversationStore, LoggerStub.Create<AdminConversationTurnEventIngestor>());

        // 开发者追踪 Ingestor 使用空的内存存储，验证事件能被正确分发
        var traceStore = new AdminDeveloperTraceStore();
        _developerTraceIngestor = new AdminDeveloperTraceEventIngestor(
            traceStore, LoggerStub.Create<AdminDeveloperTraceEventIngestor>());

        // 路由回退 Ingestor 使用空的内存存储，验证 route-fallback 事件能被正确分发
        var routeFallbackStore = new AdminRouteFallbackStore();
        _routeFallbackIngestor = new AdminRouteFallbackEventIngestor(
            routeFallbackStore, LoggerStub.Create<AdminRouteFallbackEventIngestor>());

        // 配置变更应用 Ingestor 使用空的内存存储，验证 config-applied 事件能被正确分发
        var configAppliedStore = new AdminConfigAppliedStore();
        _configAppliedIngestor = new AdminConfigAppliedEventIngestor(
            configAppliedStore, LoggerStub.Create<AdminConfigAppliedEventIngestor>());

        // 熔断状态变更 Ingestor 使用空的内存存储，验证 circuit-breaker 事件能被正确分发
        var circuitBreakerStore = new AdminCircuitBreakerStore();
        _circuitBreakerIngestor = new AdminCircuitBreakerEventIngestor(
            circuitBreakerStore, LoggerStub.Create<AdminCircuitBreakerEventIngestor>());

        // 使用独立的临时目录存放 ack.meta，确保测试之间互不干扰
        _ackMetaPath = Path.Combine(Path.GetTempPath(), $"aitool-test-ack-{Guid.NewGuid():N}", "ack.meta");
        _ackStateStore = new CoreEventAckStateStore(_ackMetaPath, LoggerStub.Create<CoreEventAckStateStore>());
    }

    /// <summary>
    /// 没有积压事件时，PullAndProcessAsync 应返回 0，不执行 ack。
    /// </summary>
    [Fact]
    public async Task PullAndProcessAsync_returns_zero_when_no_events()
    {
        var handler = new StubHttpMessageHandler();
        // 模拟 replay 返回空列表
        handler.SetupReplayResponse([]);
        handler.SetupAckResponse(new CoreAckResult { AckedSequenceId = 0, AckedAt = DateTimeOffset.UtcNow });

        var coreClient = CreateCoreClient(handler);
        var service = new CoreEventPullService(coreClient, _usageLogIngestor, _conversationTurnIngestor, _developerTraceIngestor, _routeFallbackIngestor, _configAppliedIngestor, _circuitBreakerIngestor, _ackStateStore, LoggerStub.Create<CoreEventPullService>());

        var count = await service.PullAndProcessAsync(CancellationToken.None);

        count.Should().Be(0);
        service.AckedSequenceId.Should().Be(0);
        // 没有 ack 请求被发出
        handler.AckCallCount.Should().Be(0);
    }

    /// <summary>
    /// 有积压 UsageLog 事件时，应拉取 → 入库 → ack，并返回正确的事件数量和序号。
    /// </summary>
    [Fact]
    public async Task PullAndProcessAsync_processes_usage_log_events_and_acks()
    {
        var payload = new CoreUsageLogEvent
        {
            RequestId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            AccessKeyId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            ProtocolType = "OpenAI",
            ForwardingMode = "direct",
            RequestModel = "chat-prod",
            AttemptedModel = "gpt-5.4",
            TargetSiteId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            Status = "success",
            Source = "proxy",
            RetryCount = 0,
            AttemptIndex = 1,
            IsFinalResult = true,
            FallbackTriggered = false,
            ErrorMessage = string.Empty,
            InputTokens = 10,
            CachedTokens = 2,
            OutputTokens = 6,
            IsStreaming = false,
            IsStreamInterrupted = false,
            FirstTokenLatencyMs = 30,
            StreamDurationMs = 0,
            TotalDurationMs = 80,
            ReasoningEffort = string.Empty,
            RequestedAt = new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero)
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var envelopes = new List<CoreAdminEventEnvelope>
        {
            new() { SequenceId = 1, EventType = "usage-log", OccurredAt = payload.RequestedAt, PayloadJson = json },
            new() { SequenceId = 2, EventType = "usage-log", OccurredAt = payload.RequestedAt, PayloadJson = json },
            new() { SequenceId = 3, EventType = "usage-log", OccurredAt = payload.RequestedAt, PayloadJson = json }
        };

        var handler = new StubHttpMessageHandler();
        handler.SetupReplayResponse(envelopes);
        handler.SetupAckResponse(new CoreAckResult { AckedSequenceId = 3, AckedAt = DateTimeOffset.UtcNow });

        var coreClient = CreateCoreClient(handler);
        var service = new CoreEventPullService(coreClient, _usageLogIngestor, _conversationTurnIngestor, _developerTraceIngestor, _routeFallbackIngestor, _configAppliedIngestor, _circuitBreakerIngestor, _ackStateStore, LoggerStub.Create<CoreEventPullService>());

        var count = await service.PullAndProcessAsync(CancellationToken.None);

        count.Should().Be(3);
        service.AckedSequenceId.Should().Be(3);
        handler.AckCallCount.Should().Be(1);
        // 两条 UsageLog 应被写入数据库（第三条是重复的 RequestId+AttemptIndex+...，但每条 envelope 的 SequenceId 不同，
        // Ingestor 的去重基于 RequestId+AttemptIndex+RequestedAt+AttemptedModel+Status，所以三条中只有第一条入库）
        // 实际上三条 payload 完全相同，Ingestor 会去重为 1 条
        _dbContext.ProxyUsageLogs.Should().ContainSingle();
    }

    /// <summary>
    /// 事件中包含无法消费的未知类型时，所有 Ingestor 都返回 0，
    /// 但服务仍应推进 ack 序号到本批最大值，避免 spool 持续膨胀。
    /// </summary>
    [Fact]
    public async Task PullAndProcessAsync_acks_unknown_events_to_prevent_spool_growth()
    {
        var envelopes = new List<CoreAdminEventEnvelope>
        {
            new() { SequenceId = 10, EventType = "detection", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
            new() { SequenceId = 11, EventType = "route-fallback", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" }
        };

        var handler = new StubHttpMessageHandler();
        handler.SetupReplayResponse(envelopes);
        handler.SetupAckResponse(new CoreAckResult { AckedSequenceId = 11, AckedAt = DateTimeOffset.UtcNow });

        var coreClient = CreateCoreClient(handler);
        var service = new CoreEventPullService(coreClient, _usageLogIngestor, _conversationTurnIngestor, _developerTraceIngestor, _routeFallbackIngestor, _configAppliedIngestor, _circuitBreakerIngestor, _ackStateStore, LoggerStub.Create<CoreEventPullService>());

        var count = await service.PullAndProcessAsync(CancellationToken.None);

        count.Should().Be(2);
        // 即使没有任何 Ingestor 能消费，ack 序号仍应推进到 11
        service.AckedSequenceId.Should().Be(11);
        handler.AckCallCount.Should().Be(1);
        _dbContext.ProxyUsageLogs.Should().BeEmpty();
        _conversationStore.WrittenLogs.Should().BeEmpty();
    }

    /// <summary>
    /// 混合事件类型（usage-log + conversation-turn）时，两种 Ingestor 分别消费各自事件，
    /// ack 序号推进到本批次最大值。
    /// </summary>
    [Fact]
    public async Task PullAndProcessAsync_processes_mixed_event_types()
    {
        var usagePayload = new CoreUsageLogEvent
        {
            RequestId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            AccessKeyId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            ProtocolType = "OpenAI",
            ForwardingMode = "direct",
            RequestModel = "chat-prod",
            AttemptedModel = "gpt-5.4",
            TargetSiteId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            Status = "success",
            Source = "proxy",
            RetryCount = 0,
            AttemptIndex = 1,
            IsFinalResult = true,
            FallbackTriggered = false,
            ErrorMessage = string.Empty,
            InputTokens = 10,
            CachedTokens = 2,
            OutputTokens = 6,
            IsStreaming = false,
            IsStreamInterrupted = false,
            FirstTokenLatencyMs = 30,
            StreamDurationMs = 0,
            TotalDurationMs = 80,
            ReasoningEffort = string.Empty,
            RequestedAt = new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero)
        };
        var usageJson = JsonSerializer.Serialize(usagePayload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var conversationPayload = new CoreConversationTurnEvent
        {
            RequestId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            AccessKeyId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            SourceTool = "claude-code",
            SessionId = "session-001",
            ConversationGroupKey = "group-key-001",
            RequestModel = "claude-sonnet-4-6",
            ProtocolType = "OpenAI",
            RequestPath = "/v1/chat/completions",
            Source = "proxy",
            UserInputText = "你好",
            AssistantOutputMarkdown = "你好！",
            InputTokens = 5,
            CachedTokens = 0,
            OutputTokens = 3,
            IsStreaming = false,
            Status = "success",
            CreatedAt = new DateTimeOffset(2026, 6, 10, 10, 1, 0, TimeSpan.Zero)
        };
        var conversationJson = JsonSerializer.Serialize(conversationPayload, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var envelopes = new List<CoreAdminEventEnvelope>
        {
            new() { SequenceId = 1, EventType = "usage-log", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = usageJson },
            new() { SequenceId = 2, EventType = "conversation-turn", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = conversationJson },
            new() { SequenceId = 3, EventType = "detection", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" }
        };

        var handler = new StubHttpMessageHandler();
        handler.SetupReplayResponse(envelopes);
        handler.SetupAckResponse(new CoreAckResult { AckedSequenceId = 3, AckedAt = DateTimeOffset.UtcNow });

        var coreClient = CreateCoreClient(handler);
        var service = new CoreEventPullService(coreClient, _usageLogIngestor, _conversationTurnIngestor, _developerTraceIngestor, _routeFallbackIngestor, _configAppliedIngestor, _circuitBreakerIngestor, _ackStateStore, LoggerStub.Create<CoreEventPullService>());

        var count = await service.PullAndProcessAsync(CancellationToken.None);

        count.Should().Be(3);
        service.AckedSequenceId.Should().Be(3);
        handler.AckCallCount.Should().Be(1);
        // UsageLog 应被写入数据库
        _dbContext.ProxyUsageLogs.Should().ContainSingle();
        // ConversationTurn 应被写入对话记录存储
        _conversationStore.WrittenLogs.Should().ContainSingle();
        _conversationStore.WrittenLogs[0].RequestId.Should().Be(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"));
        _conversationStore.WrittenLogs[0].SessionId.Should().Be("session-001");
    }

    /// <summary>
    /// 连续两轮拉取时，第二轮应使用上一轮推进的 ack 序号作为 afterSequenceId。
    /// </summary>
    [Fact]
    public async Task PullAndProcessAsync_carries_acked_sequence_across_rounds()
    {
        var handler = new StubHttpMessageHandler();

        // 第一轮：返回 3 条事件
        var round1Envelopes = new List<CoreAdminEventEnvelope>
        {
            new() { SequenceId = 1, EventType = "other", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
            new() { SequenceId = 2, EventType = "other", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
            new() { SequenceId = 3, EventType = "other", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" }
        };
        handler.SetupReplayResponse(round1Envelopes);
        handler.SetupAckResponse(new CoreAckResult { AckedSequenceId = 3, AckedAt = DateTimeOffset.UtcNow });

        var coreClient = CreateCoreClient(handler);
        var service = new CoreEventPullService(coreClient, _usageLogIngestor, _conversationTurnIngestor, _developerTraceIngestor, _routeFallbackIngestor, _configAppliedIngestor, _circuitBreakerIngestor, _ackStateStore, LoggerStub.Create<CoreEventPullService>());

        var count1 = await service.PullAndProcessAsync(CancellationToken.None);
        count1.Should().Be(3);
        service.AckedSequenceId.Should().Be(3);

        // 第二轮：空积压
        handler.SetupReplayResponse([]);
        var count2 = await service.PullAndProcessAsync(CancellationToken.None);
        count2.Should().Be(0);
        // 第二轮 replay 请求中 afterSequenceId 应为 3
        handler.LastReplayAfterSequenceId.Should().Be(3);
    }

    /// <summary>
    /// ack 状态应持久化到磁盘，使得新的 CoreEventPullService 实例能从上次的确认序号继续。
    /// 模拟 Admin 进程重启场景：第一个实例消费并 ack 到序号 5，
    /// 第二个实例构造时自动从文件恢复 ack 序号，不再重复消费。
    /// </summary>
    [Fact]
    public async Task PullAndProcessAsync_persists_ack_state_across_service_instances()
    {
        var handler = new StubHttpMessageHandler();

        // 第一轮：返回 5 条事件
        var envelopes = new List<CoreAdminEventEnvelope>
        {
            new() { SequenceId = 1, EventType = "other", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
            new() { SequenceId = 2, EventType = "other", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
            new() { SequenceId = 3, EventType = "other", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
            new() { SequenceId = 4, EventType = "other", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
            new() { SequenceId = 5, EventType = "other", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" }
        };
        handler.SetupReplayResponse(envelopes);
        handler.SetupAckResponse(new CoreAckResult { AckedSequenceId = 5, AckedAt = DateTimeOffset.UtcNow });

        var coreClient = CreateCoreClient(handler);

        // 第一个服务实例消费并 ack
        var service1 = new CoreEventPullService(
            coreClient, _usageLogIngestor, _conversationTurnIngestor, _developerTraceIngestor, _routeFallbackIngestor, _configAppliedIngestor, _circuitBreakerIngestor, _ackStateStore,
            LoggerStub.Create<CoreEventPullService>());
        var count1 = await service1.PullAndProcessAsync(CancellationToken.None);
        count1.Should().Be(5);
        service1.AckedSequenceId.Should().Be(5);

        // 模拟 Admin 重启：创建新的 ackStateStore 和 service 实例
        var ackStateStore2 = new CoreEventAckStateStore(_ackMetaPath, LoggerStub.Create<CoreEventAckStateStore>());
        var service2 = new CoreEventPullService(
            coreClient, _usageLogIngestor, _conversationTurnIngestor, _developerTraceIngestor, _routeFallbackIngestor, _configAppliedIngestor, _circuitBreakerIngestor, ackStateStore2,
            LoggerStub.Create<CoreEventPullService>());

        // 新实例应从持久化文件恢复 ack 序号为 5
        service2.AckedSequenceId.Should().Be(5);
    }

    /// <summary>
    /// 利用模拟 HttpMessageHandler 创建 CoreAdminClient 实例。
    /// </summary>
    private static CoreAdminClient CreateCoreClient(StubHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5029/")
        };
        return new CoreAdminClient(httpClient);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        // 清理 ack.meta 临时目录
        try
        {
            var dir = Path.GetDirectoryName(_ackMetaPath);
            if (dir != null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // 测试清理失败不影响结果
        }
    }
}

/// <summary>
/// 模拟 Core 宿主 HTTP 接口的 HttpMessageHandler。
/// 根据 URL 路径返回预设的 JSON 响应，记录请求参数供测试断言。
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private IReadOnlyList<CoreAdminEventEnvelope>? _replayResponse;
    private CoreAckResult? _ackResponse;

    /// <summary>
    /// ack 接口被调用的次数。
    /// </summary>
    public int AckCallCount { get; private set; }

    /// <summary>
    /// 最近一次 replay 请求中的 afterSequenceId 参数值。
    /// </summary>
    public long LastReplayAfterSequenceId { get; private set; }

    /// <summary>
    /// 设置 replay 接口返回的事件列表。
    /// </summary>
    public void SetupReplayResponse(IReadOnlyList<CoreAdminEventEnvelope> envelopes)
    {
        _replayResponse = envelopes;
    }

    /// <summary>
    /// 设置 ack 接口返回的结果。
    /// </summary>
    public void SetupAckResponse(CoreAckResult result)
    {
        _ackResponse = result;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;

        if (path.Contains("/api/core/events/replay", StringComparison.OrdinalIgnoreCase))
        {
            // 提取 afterSequenceId 参数
            var query = request.RequestUri!.Query;
            if (!string.IsNullOrEmpty(query))
            {
                // 简单解析 afterSequenceId=N 格式
                var match = System.Text.RegularExpressions.Regex.Match(query, @"afterSequenceId=(\d+)");
                if (match.Success)
                {
                    LastReplayAfterSequenceId = long.Parse(match.Groups[1].Value);
                }
            }

            var json = JsonSerializer.Serialize(
                _replayResponse ?? [],
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }

        if (path.Contains("/api/core/events/ack", StringComparison.OrdinalIgnoreCase))
        {
            AckCallCount++;
            var json = JsonSerializer.Serialize(
                _ackResponse ?? new CoreAckResult { AckedSequenceId = 0, AckedAt = DateTimeOffset.UtcNow },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>
/// 轻量 ILogger 替身，不输出任何日志，用于不依赖真实日志基础设施的单元测试。
/// </summary>
internal static class LoggerStub
{
    public static ILogger<T> Create<T>()
    {
        return new StubLogger<T>();
    }

    private sealed class StubLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => false;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}

/// <summary>
/// 内存实现的对话记录存储替身，用于不依赖真实文件系统的单元测试。
/// </summary>
internal sealed class StubConversationLogStore : IConversationLogStore
{
    /// <summary>
    /// 已写入的全部对话记录。
    /// </summary>
    public List<ConversationTurnLog> WrittenLogs { get; } = [];

    public Task AppendBatchAsync(IReadOnlyList<ConversationTurnLog> logs, CancellationToken cancellationToken = default)
    {
        WrittenLogs.AddRange(logs);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConversationTurnLog>> QueryAsync(ConversationLogQuery query, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ConversationTurnLog> result = Array.Empty<ConversationTurnLog>();
        return Task.FromResult(result);
    }

    public Task<int> DeleteSessionAsync(string groupKey, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(0);
    }

    public Task<int> UpdateSessionTitleAsync(string groupKey, string title, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(0);
    }

    public Task PruneExpiredAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
