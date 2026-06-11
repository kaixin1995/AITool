using System.Text.Json;
using AITool.Application.CoreRuntime;
using AITool.Core.Services;
using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.Core.IntegrationTests;

/// <summary>
/// 验证 DeveloperTrace 事件发布器将追踪记录正确投影到事件信封，
/// 并验证预览截断和 pending 状态跳过等边界行为。
/// </summary>
public sealed class CoreDeveloperTraceEventPublisherTests : IDisposable
{
    /// <summary>
    /// 测试序号提供器使用的临时目录，测试结束后由 Dispose 清理。
    /// </summary>
    private readonly string _tempRoot;

    public CoreDeveloperTraceEventPublisherTests()
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
    /// 发布完成的追踪记录后，应生成 developer-trace 类型的信封，所有字段正确序列化。
    /// </summary>
    [Fact]
    public async Task PublishAsync_projects_completed_trace_into_event_envelope()
    {
        var sequenceProvider = CreateSequenceProvider();
        var eventBus = new CoreAdminEventBus();
        var publisher = new CoreDeveloperTraceEventPublisher(sequenceProvider, eventBus);

        var traceId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var requestId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var siteId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var startedAt = new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero);
        var finishedAt = new DateTimeOffset(2026, 6, 10, 10, 0, 5, TimeSpan.Zero);

        var entry = new DeveloperInvocationTraceEntry
        {
            TraceId = traceId,
            RequestId = requestId,
            CreatedAt = startedAt,
            UpdatedAt = finishedAt,
            Source = "claude-code",
            ProtocolType = "OpenAI",
            RequestModel = "chat-prod",
            AttemptedModel = "gpt-5.4",
            TargetSiteId = siteId,
            TargetSiteName = "Site-A",
            Status = "success",
            IsStreaming = true,
            InputTokens = 100,
            CachedTokens = 20,
            OutputTokens = 50,
            TotalDurationMs = 5000,
            RequestBody = "{\"model\":\"chat-prod\"}",
            ResponseBody = "{\"choices\":[]}",
            Attempts =
            [
                new DeveloperInvocationTraceAttempt
                {
                    ForwardingMode = "direct",
                    AttemptedModel = "gpt-5.4"
                }
            ]
        };

        await publisher.PublishAsync(entry);

        var envelope = await eventBus.Reader.ReadAsync();
        envelope.SequenceId.Should().Be(1);
        envelope.EventType.Should().Be("developer-trace");
        // DeveloperTrace 信封的 OccurredAt 取自 payload.FinishedAt（即 entry.UpdatedAt）
        envelope.OccurredAt.Should().Be(finishedAt);

        var payload = JsonSerializer.Deserialize<CoreDeveloperTraceEvent>(
            envelope.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        payload.Should().NotBeNull();
        payload!.TraceId.Should().Be(traceId);
        payload.RequestId.Should().Be(requestId);
        payload.ProtocolType.Should().Be("OpenAI");
        payload.RequestModel.Should().Be("chat-prod");
        payload.AttemptedModel.Should().Be("gpt-5.4");
        payload.TargetSiteId.Should().Be(siteId);
        payload.TargetSiteName.Should().Be("Site-A");
        payload.ForwardingMode.Should().Be("direct");
        payload.Status.Should().Be("success");
        payload.StartedAt.Should().Be(startedAt);
        payload.FinishedAt.Should().Be(finishedAt);
        payload.ErrorMessage.Should().BeEmpty();
        payload.Source.Should().Be("claude-code");
        payload.IsStreaming.Should().BeTrue();
        payload.InputTokens.Should().Be(100);
        payload.CachedTokens.Should().Be(20);
        payload.OutputTokens.Should().Be(50);
        payload.TotalDurationMs.Should().Be(5000);
    }

    /// <summary>
    /// status 为 pending 的追踪记录不应产生事件，直接跳过。
    /// </summary>
    [Fact]
    public async Task PublishAsync_skips_pending_status_entry()
    {
        var sequenceProvider = CreateSequenceProvider();
        var eventBus = new CoreAdminEventBus();
        var publisher = new CoreDeveloperTraceEventPublisher(sequenceProvider, eventBus);

        var entry = new DeveloperInvocationTraceEntry
        {
            TraceId = Guid.NewGuid(),
            Status = "pending"
        };

        await publisher.PublishAsync(entry);

        // 事件总线不应有任何信封
        eventBus.Reader.TryRead(out _).Should().BeFalse();
    }

    /// <summary>
    /// 超过 512 字符的请求体和响应体应被截断，尾部添加省略号。
    /// </summary>
    [Fact]
    public async Task PublishAsync_truncates_long_previews()
    {
        var sequenceProvider = CreateSequenceProvider();
        var eventBus = new CoreAdminEventBus();
        var publisher = new CoreDeveloperTraceEventPublisher(sequenceProvider, eventBus);

        // 构造一个 600 字符的请求体和 700 字符的响应体
        var longRequestBody = new string('A', 600);
        var longResponseBody = new string('B', 700);

        var entry = new DeveloperInvocationTraceEntry
        {
            TraceId = Guid.NewGuid(),
            Status = "success",
            RequestBody = longRequestBody,
            ResponseBody = longResponseBody
        };

        await publisher.PublishAsync(entry);

        var envelope = await eventBus.Reader.ReadAsync();
        var payload = JsonSerializer.Deserialize<CoreDeveloperTraceEvent>(
            envelope.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        payload.Should().NotBeNull();

        // 截断后应为 512 字符 + "..." = 515 字符
        payload!.RequestPreview.Should().Be(string.Concat(longRequestBody.AsSpan(0, 512), "..."));
        payload.ResponsePreview.Should().Be(string.Concat(longResponseBody.AsSpan(0, 512), "..."));
    }

    /// <summary>
    /// 空请求体和空响应体应被映射为空字符串而非 null。
    /// </summary>
    [Fact]
    public async Task PublishAsync_handles_empty_body_fields()
    {
        var sequenceProvider = CreateSequenceProvider();
        var eventBus = new CoreAdminEventBus();
        var publisher = new CoreDeveloperTraceEventPublisher(sequenceProvider, eventBus);

        var entry = new DeveloperInvocationTraceEntry
        {
            TraceId = Guid.NewGuid(),
            Status = "error",
            RequestBody = "",
            ResponseBody = null!,
            ErrorMessage = "upstream timeout"
        };

        await publisher.PublishAsync(entry);

        var envelope = await eventBus.Reader.ReadAsync();
        var payload = JsonSerializer.Deserialize<CoreDeveloperTraceEvent>(
            envelope.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        payload.Should().NotBeNull();
        payload!.RequestPreview.Should().BeEmpty();
        payload.ResponsePreview.Should().BeEmpty();
        payload.ErrorMessage.Should().Be("upstream timeout");
    }

    /// <summary>
    /// 传入 null 参数应抛出 ArgumentNullException。
    /// </summary>
    [Fact]
    public async Task PublishAsync_throws_on_null_entry()
    {
        var sequenceProvider = CreateSequenceProvider();
        var eventBus = new CoreAdminEventBus();
        var publisher = new CoreDeveloperTraceEventPublisher(sequenceProvider, eventBus);

        var act = () => publisher.PublishAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    /// <summary>
    /// 没有任何 Attempt 记录时，ForwardingMode 应为空字符串。
    /// </summary>
    [Fact]
    public async Task PublishAsync_empty_attempts_yields_empty_forwarding_mode()
    {
        var sequenceProvider = CreateSequenceProvider();
        var eventBus = new CoreAdminEventBus();
        var publisher = new CoreDeveloperTraceEventPublisher(sequenceProvider, eventBus);

        var entry = new DeveloperInvocationTraceEntry
        {
            TraceId = Guid.NewGuid(),
            Status = "success",
            Attempts = []
        };

        await publisher.PublishAsync(entry);

        var envelope = await eventBus.Reader.ReadAsync();
        var payload = JsonSerializer.Deserialize<CoreDeveloperTraceEvent>(
            envelope.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        payload.Should().NotBeNull();
        payload!.ForwardingMode.Should().BeEmpty();
    }

    /// <summary>
    /// 连续发布多条事件时，序号应递增。
    /// </summary>
    [Fact]
    public async Task PublishAsync_sequential_calls_increment_sequence()
    {
        var sequenceProvider = CreateSequenceProvider();
        var eventBus = new CoreAdminEventBus();
        var publisher = new CoreDeveloperTraceEventPublisher(sequenceProvider, eventBus);

        for (int i = 0; i < 3; i++)
        {
            var entry = new DeveloperInvocationTraceEntry
            {
                TraceId = Guid.NewGuid(),
                Status = "success"
            };
            await publisher.PublishAsync(entry);
        }

        var env1 = await eventBus.Reader.ReadAsync();
        var env2 = await eventBus.Reader.ReadAsync();
        var env3 = await eventBus.Reader.ReadAsync();

        env1.SequenceId.Should().Be(1);
        env2.SequenceId.Should().Be(2);
        env3.SequenceId.Should().Be(3);
    }
}
