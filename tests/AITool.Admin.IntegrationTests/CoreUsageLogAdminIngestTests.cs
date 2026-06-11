using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using AITool.Admin.IntegrationTests.Infrastructure;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Admin.IntegrationTests.Core;

/// <summary>
/// 验证 UsageLog 真实链路已经具备 Admin 消费入库的最小闭环。
/// </summary>
public sealed class CoreUsageLogAdminIngestTests
{
    /// <summary>
    /// Core 发布 UsageLog 事件后，Admin 最小消费器应能通过 replay 拉取事件、写回数据库并返回 ack 序号。
    /// </summary>
    [Fact]
    public async Task Replay_then_ingest_usage_log_event_should_persist_into_admin_database()
    {
        await using var factory = new CoreConfigSyncWebApplicationFactory();
        using var httpClient = factory.CreateClient();
        var client = new CoreAdminClient(httpClient);

        await factory.PublishUsageLogEventAsync();
        await Task.Delay(150);

        var replay = await client.ReplayAsync(0);
        replay.Should().HaveCount(1);
        replay[0].EventType.Should().Be("usage-log");

        await using var scope = factory.Services.CreateAsyncScope();
        var ingestor = new AdminUsageLogEventIngestor(scope.ServiceProvider.GetRequiredService<AppDbContext>());
        var ackSequence = await ingestor.IngestUsageLogEventsAsync(replay);
        ackSequence.Should().Be(replay[0].SequenceId);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = await db.ProxyUsageLogs.SingleAsync();
        log.RequestModel.Should().Be("chat-prod");
        log.AttemptedModel.Should().Be("gpt-5.4");
        log.TotalTokens.Should().Be(18);
    }
}
