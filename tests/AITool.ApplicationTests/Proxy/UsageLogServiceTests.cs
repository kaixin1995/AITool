using AITool.Application.UsageLogs;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.ApplicationTests.Proxy;

/// <summary>
/// 验证使用日志服务是否能正确写入数据库，并补齐统计字段和回退链路信息。
/// 同时覆盖 UsageLog 旁路事件发布，确保后续切换事件流时不需要再回头改这条主链路出口。
/// </summary>
public sealed class UsageLogServiceTests : IDisposable
{
    /// <summary>
    /// 内存数据库上下文，用于断言日志最终写入结果。
    /// </summary>
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 测试专用依赖注入容器，用来创建批量写入器所需作用域。
    /// </summary>
    private readonly ServiceProvider _serviceProvider;

    /// <summary>
    /// 被测服务，负责把使用日志转交到底层批量写入器。
    /// </summary>
    private readonly UsageLogService _service;

    /// <summary>
    /// Core 事件总线，用于读取 UsageLog 旁路发布出来的事件。
    /// </summary>
    private readonly CoreAdminEventBus _eventBus;

    /// <summary>
    /// 初始化独立的测试容器和数据库，避免不同用例之间共享状态。
    /// </summary>
    public UsageLogServiceTests()
    {
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContext<AppDbContext>(dbOptions => dbOptions.UseInMemoryDatabase(databaseName));
        _serviceProvider = services.BuildServiceProvider();
        _dbContext = _serviceProvider.GetRequiredService<AppDbContext>();

        _eventBus = new CoreAdminEventBus();
        var sequenceProvider = new CoreEventSequenceProvider();
        var eventPublisher = new CoreUsageLogEventPublisher(sequenceProvider, _eventBus);
        var batchWriter = new ProxyUsageLogBatchWriter(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProxyUsageLogBatchWriter>.Instance,
            new TestHostEnvironment());
        _service = new UsageLogService(batchWriter, eventPublisher);
    }

    /// <summary>
    /// 正常日志写入后，应能查到完整记录，并且总 Token 数是各部分之和。
    /// 同时应额外生成一条 usage-log 事件，为后续 Admin 事件消费打基础。
    /// </summary>
    [Fact]
    public async Task LogAsync_persists_entry_with_correct_total_tokens()
    {
        var entry = new UsageLogEntry
        {
            AccessKeyId = Guid.NewGuid(),
            ProtocolType = "OpenAI",
            RequestModel = "gpt-5",
            TargetSiteId = Guid.NewGuid(),
            Status = "success",
            InputTokens = 100,
            CachedTokens = 25,
            OutputTokens = 50,
            IsStreaming = true,
            FirstTokenLatencyMs = 5400,
            StreamDurationMs = 2600,
            TotalDurationMs = 8000
        };

        await _service.LogAsync(entry);

        var log = await _dbContext.ProxyUsageLogs.SingleAsync();
        log.ProtocolType.Should().Be("OpenAI");
        log.RequestModel.Should().Be("gpt-5");
        log.Status.Should().Be("success");
        log.CachedTokens.Should().Be(25);
        log.TotalTokens.Should().Be(175);
        log.IsStreaming.Should().BeTrue();
        log.FirstTokenLatencyMs.Should().Be(5400);
        log.TotalDurationMs.Should().Be(8000);

        var envelope = await _eventBus.Reader.ReadAsync();
        envelope.EventType.Should().Be("usage-log");
    }

    /// <summary>
    /// 回退流程中的尝试信息应完整保留下来，便于后续排查多次转发过程。
    /// </summary>
    [Fact]
    public async Task LogAsync_persists_attempt_metadata_for_fallback_flow()
    {
        var requestId = Guid.NewGuid();
        var entry = new UsageLogEntry
        {
            AccessKeyId = Guid.NewGuid(),
            ProtocolType = "OpenAI",
            RequestModel = "gpt-5.5",
            AttemptedModel = "glm-5.1",
            TargetSiteId = Guid.NewGuid(),
            Status = "fail",
            Source = "proxy",
            RetryCount = 2,
            AttemptIndex = 3,
            IsFinalResult = false,
            FallbackTriggered = true,
            RequestId = requestId,
            ErrorMessage = "upstream timeout",
            InputTokens = 0,
            CachedTokens = 8704,
            OutputTokens = 0,
            IsStreaming = false,
            FirstTokenLatencyMs = 0,
            StreamDurationMs = 0,
            TotalDurationMs = 8000
        };

        await _service.LogAsync(entry);

        var log = await _dbContext.ProxyUsageLogs.SingleAsync();
        log.RequestId.Should().Be(requestId);
        log.AttemptedModel.Should().Be("glm-5.1");
        log.AttemptIndex.Should().Be(3);
        log.IsFinalResult.Should().BeFalse();
        log.FallbackTriggered.Should().BeTrue();
        log.ErrorMessage.Should().Be("upstream timeout");
    }

    /// <summary>
    /// 连续多次记录日志时，每次调用都应生成一条独立数据。
    /// </summary>
    [Fact]
    public async Task LogAsync_creates_multiple_entries()
    {
        for (var i = 0; i < 3; i++)
        {
            await _service.LogAsync(new UsageLogEntry
            {
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "Anthropic",
                RequestModel = $"model-{i}",
                TargetSiteId = Guid.NewGuid(),
                Status = "success",
                InputTokens = i * 10,
                OutputTokens = i * 5
            });
        }

        var logs = await _dbContext.ProxyUsageLogs.OrderBy(x => x.RequestModel).ToListAsync();
        logs.Should().HaveCount(3);
        logs.Select(x => x.RequestModel).Should().Equal("model-0", "model-1", "model-2");
    }

    /// <summary>
    /// 释放测试使用的内存数据库与依赖容器。
    /// </summary>
    public void Dispose()
    {
        _dbContext.Dispose();
        _serviceProvider.Dispose();
    }

    /// <summary>
    /// 测试专用宿主环境，强制后台写入器走直写模式，避免测试等待后台任务调度。
    /// </summary>
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "AITool.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
