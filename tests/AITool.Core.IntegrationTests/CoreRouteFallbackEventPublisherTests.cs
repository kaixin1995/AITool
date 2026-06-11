using System.Text.Json;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using AITool.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.Core.IntegrationTests;

/// <summary>
/// 验证 RouteFallback 事件的发布、信封构造和负载序列化。
/// </summary>
public sealed class CoreRouteFallbackEventPublisherTests : IDisposable
{
    /// <summary>
    /// 测试序号提供器使用的临时目录，测试结束后由 Dispose 清理。
    /// </summary>
    private readonly string _tempRoot;

    public CoreRouteFallbackEventPublisherTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"aitool-test-seq-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, true);
        }
    }

    /// <summary>
    /// 创建测试用的序号提供器实例。
    /// </summary>
    private CoreEventSequenceProvider CreateSequenceProvider()
    {
        var options = new CoreEventSpoolOptions { RootPath = _tempRoot };
        var spoolStore = new CoreEventSpoolStore(options, NullLogger<CoreEventSpoolStore>.Instance);
        return new CoreEventSequenceProvider(
            options,
            spoolStore,
            NullLogger<CoreEventSequenceProvider>.Instance);
    }

    /// <summary>
    /// 发布路由回退事件后，应生成 route-fallback 类型的信封，并把所有字段正确序列化到负载中。
    /// </summary>
    [Fact]
    public async Task PublishAsync_projects_route_fallback_into_event_envelope()
    {
        var sequenceProvider = CreateSequenceProvider();
        var eventBus = new CoreAdminEventBus();
        var publisher = new CoreRouteFallbackEventPublisher(sequenceProvider, eventBus);

        var requestId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var fromRouteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var fromSiteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var toRouteId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var toSiteId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        await publisher.PublishAsync(
            requestId,
            "chat-prod",
            fromRouteId, fromSiteId, "gpt-5.4-site-a",
            toRouteId, toSiteId, "gpt-5.4-site-b",
            "upstream timeout");

        var envelope = await eventBus.Reader.ReadAsync();
        envelope.SequenceId.Should().Be(1);
        envelope.EventType.Should().Be("route-fallback");
        envelope.OccurredAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));

        var payload = JsonSerializer.Deserialize<CoreRouteFallbackEvent>(
            envelope.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        payload.Should().NotBeNull();
        payload!.RequestId.Should().Be(requestId);
        payload.RequestModel.Should().Be("chat-prod");
        payload.FromRouteId.Should().Be(fromRouteId);
        payload.FromSiteId.Should().Be(fromSiteId);
        payload.FromSiteModelName.Should().Be("gpt-5.4-site-a");
        payload.ToRouteId.Should().Be(toRouteId);
        payload.ToSiteId.Should().Be(toSiteId);
        payload.ToSiteModelName.Should().Be("gpt-5.4-site-b");
        payload.Reason.Should().Be("upstream timeout");
        payload.OccurredAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// 连续发布多条回退事件时，序号应递增。
    /// </summary>
    [Fact]
    public async Task PublishAsync_increments_sequence_id_across_multiple_events()
    {
        var sequenceProvider = CreateSequenceProvider();
        var eventBus = new CoreAdminEventBus();
        var publisher = new CoreRouteFallbackEventPublisher(sequenceProvider, eventBus);

        await publisher.PublishAsync(Guid.NewGuid(), "model-a",
            Guid.NewGuid(), Guid.NewGuid(), "site-a",
            Guid.NewGuid(), Guid.NewGuid(), "site-b",
            "error-1");
        await publisher.PublishAsync(Guid.NewGuid(), "model-b",
            Guid.NewGuid(), Guid.NewGuid(), "site-c",
            Guid.NewGuid(), Guid.NewGuid(), "site-d",
            "error-2");

        var envelope1 = await eventBus.Reader.ReadAsync();
        var envelope2 = await eventBus.Reader.ReadAsync();

        envelope1.SequenceId.Should().Be(1);
        envelope2.SequenceId.Should().Be(2);
    }
}
