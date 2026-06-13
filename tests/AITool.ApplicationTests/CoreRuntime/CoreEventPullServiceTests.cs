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

public sealed class CoreEventPullServiceTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly AdminUnifiedProxyEventIngestor _unifiedIngestor;
    private readonly StubConversationLogStore _conversationStore;
    private readonly AdminConversationTurnEventIngestor _conversationTurnIngestor;
    private readonly AdminRouteFallbackEventIngestor _routeFallbackIngestor;
    private readonly AdminConfigAppliedEventIngestor _configAppliedIngestor;
    private readonly AdminCircuitBreakerEventIngestor _circuitBreakerIngestor;
    private readonly CoreEventAckStateStore _ackStateStore;
    private readonly string _ackMetaPath;

    public CoreEventPullServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);

        var traceStore = new AdminDeveloperTraceStore();
        var extractionService = new ConversationExtractionService();
        _conversationStore = new StubConversationLogStore();
        _unifiedIngestor = new AdminUnifiedProxyEventIngestor(
            _dbContext, traceStore, extractionService, _conversationStore,
            LoggerStub.Create<AdminUnifiedProxyEventIngestor>());

        _conversationTurnIngestor = new AdminConversationTurnEventIngestor(
            _conversationStore, LoggerStub.Create<AdminConversationTurnEventIngestor>());

        var routeFallbackStore = new AdminRouteFallbackStore();
        _routeFallbackIngestor = new AdminRouteFallbackEventIngestor(
            routeFallbackStore, LoggerStub.Create<AdminRouteFallbackEventIngestor>());

        var configAppliedStore = new AdminConfigAppliedStore();
        _configAppliedIngestor = new AdminConfigAppliedEventIngestor(
            configAppliedStore, LoggerStub.Create<AdminConfigAppliedEventIngestor>());

        var circuitBreakerStore = new AdminCircuitBreakerStore();
        _circuitBreakerIngestor = new AdminCircuitBreakerEventIngestor(
            circuitBreakerStore, LoggerStub.Create<AdminCircuitBreakerEventIngestor>());

        _ackMetaPath = Path.Combine(Path.GetTempPath(), $"aitool-test-ack-{Guid.NewGuid():N}", "ack.meta");
        _ackStateStore = new CoreEventAckStateStore(_ackMetaPath, LoggerStub.Create<CoreEventAckStateStore>());
    }

    [Fact]
    public async Task PullAndProcessAsync_returns_zero_when_no_events()
    {
        var handler = new StubHttpMessageHandler();
        handler.SetupReplayResponse([]);
        handler.SetupAckResponse(new CoreAckResult { AckedSequenceId = 0, AckedAt = DateTimeOffset.UtcNow });

        var coreClient = CreateCoreClient(handler);
        var service = new CoreEventPullService(coreClient, _unifiedIngestor, _conversationTurnIngestor, _routeFallbackIngestor, _configAppliedIngestor, _circuitBreakerIngestor, _ackStateStore, LoggerStub.Create<CoreEventPullService>());

        var count = await service.PullAndProcessAsync(CancellationToken.None);
        count.Should().Be(0);
        service.AckedSequenceId.Should().Be(0);
        handler.AckCallCount.Should().Be(0);
    }

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
        var service = new CoreEventPullService(coreClient, _unifiedIngestor, _conversationTurnIngestor, _routeFallbackIngestor, _configAppliedIngestor, _circuitBreakerIngestor, _ackStateStore, LoggerStub.Create<CoreEventPullService>());

        var count = await service.PullAndProcessAsync(CancellationToken.None);
        count.Should().Be(2);
        service.AckedSequenceId.Should().Be(11);
        handler.AckCallCount.Should().Be(1);
        _dbContext.ProxyUsageLogs.Should().BeEmpty();
        _conversationStore.WrittenLogs.Should().BeEmpty();
    }

    [Fact]
    public async Task PullAndProcessAsync_carries_acked_sequence_across_rounds()
    {
        var handler = new StubHttpMessageHandler();
        var round1Envelopes = new List<CoreAdminEventEnvelope>
        {
            new() { SequenceId = 1, EventType = "other", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
            new() { SequenceId = 2, EventType = "other", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" },
            new() { SequenceId = 3, EventType = "other", OccurredAt = DateTimeOffset.UtcNow, PayloadJson = "{}" }
        };
        handler.SetupReplayResponse(round1Envelopes);
        handler.SetupAckResponse(new CoreAckResult { AckedSequenceId = 3, AckedAt = DateTimeOffset.UtcNow });

        var coreClient = CreateCoreClient(handler);
        var service = new CoreEventPullService(coreClient, _unifiedIngestor, _conversationTurnIngestor, _routeFallbackIngestor, _configAppliedIngestor, _circuitBreakerIngestor, _ackStateStore, LoggerStub.Create<CoreEventPullService>());

        var count1 = await service.PullAndProcessAsync(CancellationToken.None);
        count1.Should().Be(3);
        service.AckedSequenceId.Should().Be(3);

        handler.SetupReplayResponse([]);
        var count2 = await service.PullAndProcessAsync(CancellationToken.None);
        count2.Should().Be(0);
        handler.LastReplayAfterSequenceId.Should().Be(3);
    }

    [Fact]
    public async Task PullAndProcessAsync_persists_ack_state_across_service_instances()
    {
        var handler = new StubHttpMessageHandler();
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

        var service1 = new CoreEventPullService(
            coreClient, _unifiedIngestor, _conversationTurnIngestor, _routeFallbackIngestor, _configAppliedIngestor, _circuitBreakerIngestor, _ackStateStore,
            LoggerStub.Create<CoreEventPullService>());
        var count1 = await service1.PullAndProcessAsync(CancellationToken.None);
        count1.Should().Be(5);
        service1.AckedSequenceId.Should().Be(5);

        var ackStateStore2 = new CoreEventAckStateStore(_ackMetaPath, LoggerStub.Create<CoreEventAckStateStore>());
        var service2 = new CoreEventPullService(
            coreClient, _unifiedIngestor, _conversationTurnIngestor, _routeFallbackIngestor, _configAppliedIngestor, _circuitBreakerIngestor, ackStateStore2,
            LoggerStub.Create<CoreEventPullService>());

        service2.AckedSequenceId.Should().Be(5);
    }

    private static CoreAdminClient CreateCoreClient(StubHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5029/") };
        return new CoreAdminClient(httpClient);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        try
        {
            var dir = Path.GetDirectoryName(_ackMetaPath);
            if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch { }
    }
}

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private IReadOnlyList<CoreAdminEventEnvelope>? _replayResponse;
    private CoreAckResult? _ackResponse;
    public int AckCallCount { get; private set; }
    public long LastReplayAfterSequenceId { get; private set; }

    public void SetupReplayResponse(IReadOnlyList<CoreAdminEventEnvelope> envelopes) => _replayResponse = envelopes;
    public void SetupAckResponse(CoreAckResult result) => _ackResponse = result;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;

        if (path.Contains("/api/core/events/replay", StringComparison.OrdinalIgnoreCase))
        {
            var query = request.RequestUri!.Query;
            if (!string.IsNullOrEmpty(query))
            {
                var match = System.Text.RegularExpressions.Regex.Match(query, @"afterSequenceId=(\d+)");
                if (match.Success) LastReplayAfterSequenceId = long.Parse(match.Groups[1].Value);
            }
            var json = JsonSerializer.Serialize(_replayResponse ?? [], new JsonSerializerOptions(JsonSerializerDefaults.Web));
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

internal static class LoggerStub
{
    public static ILogger<T> Create<T>() => new LoggerFactory().CreateLogger<T>();
}

internal sealed class StubConversationLogStore : IConversationLogService, IConversationLogStore
{
    public List<ConversationTurnEntry> WrittenLogs { get; } = [];
    public List<ConversationTurnLog> WrittenTurnLogs { get; } = [];

    public Task LogAsync(ConversationTurnEntry entry, CancellationToken cancellationToken = default)
    {
        WrittenLogs.Add(entry);
        return Task.CompletedTask;
    }

    public Task AppendBatchAsync(IReadOnlyList<ConversationTurnLog> logs, CancellationToken ct = default)
    {
        WrittenTurnLogs.AddRange(logs);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ConversationTurnLog>> QueryAsync(ConversationLogQuery query, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ConversationTurnLog>>([]);

    public Task<int> DeleteSessionAsync(string groupKey, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<int> UpdateSessionTitleAsync(string groupKey, string title, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task PruneExpiredAsync(CancellationToken ct = default)
        => Task.CompletedTask;
}
