using System.Text.Json;
using AITool.Application.CoreRuntime;
using AITool.Domain.Proxy;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AITool.ApplicationTests.CoreRuntime;

/// <summary>
/// 验证 Admin 侧 UsageLog 事件消费入库行为。
/// </summary>
public sealed class AdminUsageLogEventIngestorTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly AdminUsageLogEventIngestor _ingestor;

    /// <summary>
    /// 构造独立内存数据库，验证 UsageLog 事件消费结果。
    /// </summary>
    public AdminUsageLogEventIngestorTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);
        _ingestor = new AdminUsageLogEventIngestor(_dbContext);
    }

    /// <summary>
    /// UsageLog 事件应能被正确写入 Admin 数据库，并返回最大连续 sequence 供 ack 使用。
    /// </summary>
    [Fact]
    public async Task IngestUsageLogEventsAsync_persists_logs_and_returns_latest_sequence()
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
        var envelope = new CoreAdminEventEnvelope
        {
            SequenceId = 7,
            EventType = "usage-log",
            OccurredAt = payload.RequestedAt,
            PayloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };

        var ackSequence = await _ingestor.IngestUsageLogEventsAsync([envelope]);

        ackSequence.Should().Be(7);
        var log = await _dbContext.ProxyUsageLogs.SingleAsync();
        log.RequestId.Should().Be(payload.RequestId);
        log.ProtocolType.Should().Be(payload.ProtocolType);
        log.RequestModel.Should().Be(payload.RequestModel);
        log.AttemptedModel.Should().Be(payload.AttemptedModel);
        log.TotalTokens.Should().Be(18);
    }

    /// <summary>
    /// replay 重复投递相同 UsageLog 事件时，不应重复插入完全相同的记录。
    /// 但为了让上层可以安全推进 ack，本次返回的最大连续序号仍应覆盖整批事件。
    /// </summary>
    [Fact]
    public async Task IngestUsageLogEventsAsync_skips_duplicate_replayed_entries_and_returns_batch_max_sequence()
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
        var envelopes = new[]
        {
            new CoreAdminEventEnvelope { SequenceId = 1, EventType = "usage-log", OccurredAt = payload.RequestedAt, PayloadJson = json },
            new CoreAdminEventEnvelope { SequenceId = 2, EventType = "usage-log", OccurredAt = payload.RequestedAt, PayloadJson = json }
        };

        var ackSequence = await _ingestor.IngestUsageLogEventsAsync(envelopes);

        ackSequence.Should().Be(2);
        _dbContext.ProxyUsageLogs.Should().ContainSingle();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }
}
