using AITool.Application.UsageLogs;
using AITool.Domain.Proxy;
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
    /// 初始化独立的测试容器和数据库，避免不同用例之间共享状态。
    /// </summary>
    public UsageLogServiceTests()
    {
        // 单独保留一份选项实例，便于注册到容器中。
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        // 这里构建最小服务集合，只保留批量写入器运行所必需的依赖。
        var services = new ServiceCollection();
        services.AddSingleton(options);
        // 为每个测试提供独立内存库，确保断言只覆盖当前场景的数据。
        services.AddDbContext<AppDbContext>(dbOptions => dbOptions.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        _serviceProvider = services.BuildServiceProvider();
        // 直接拿到上下文，后续用来读取落库结果。
        _dbContext = _serviceProvider.GetRequiredService<AppDbContext>();

        // 使用真实批量写入器，尽量覆盖日志服务与持久化层的协作路径。
        var batchWriter = new ProxyUsageLogBatchWriter(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProxyUsageLogBatchWriter>.Instance,
            new TestHostEnvironment());
        _service = new UsageLogService(batchWriter);
    }

    /// <summary>
    /// 正常日志写入后，应能查到完整记录，并且总 Token 数是各部分之和。
    /// </summary>
    [Fact]
    public async Task LogAsync_persists_entry_with_correct_total_tokens()
    {
        // 这条日志同时覆盖流式字段和 Token 统计字段。
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

        // 读取唯一一条记录，确认服务已真正完成持久化。
        var log = await _dbContext.ProxyUsageLogs.SingleAsync();
        log.ProtocolType.Should().Be("OpenAI");
        log.RequestModel.Should().Be("gpt-5");
        log.Status.Should().Be("success");
        log.CachedTokens.Should().Be(25);
        log.TotalTokens.Should().Be(175);
        log.IsStreaming.Should().BeTrue();
        log.FirstTokenLatencyMs.Should().Be(5400);
        log.TotalDurationMs.Should().Be(8000);
    }

    /// <summary>
    /// 回退流程中的尝试信息应完整保留下来，便于后续排查多次转发过程。
    /// </summary>
    [Fact]
    public async Task LogAsync_persists_attempt_metadata_for_fallback_flow()
    {
        // 用同一个请求标识串联整个回退过程，便于验证是否原样落库。
        var requestId = Guid.NewGuid();
        // 这条记录模拟一次失败的中间尝试，重点覆盖回退链路字段。
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

        // 只写入一条记录时，直接读取单条结果验证关键元数据。
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
            // 使用循环变量区分模型名和 Token 值，便于确认不是同一条数据被覆盖。
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

        // 取回全部记录，确认累计生成了三条独立日志。
        var logs = await _dbContext.ProxyUsageLogs.ToListAsync();
        logs.Should().HaveCount(3);
    }

    /// <summary>
    /// 释放数据库上下文和服务容器，避免内存数据库与作用域对象残留。
    /// </summary>
    public void Dispose()
    {
        _dbContext.Dispose();
        _serviceProvider.Dispose();
    }

    /// <summary>
    /// 为批量写入器提供最小化宿主环境，避免测试依赖真实运行目录配置。
    /// </summary>
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        /// <summary>
        /// 标记当前环境名称，便于底层组件按测试环境运行。
        /// </summary>
        public string EnvironmentName { get; set; } = "Testing";

        /// <summary>
        /// 声明应用名称，满足宿主环境接口要求。
        /// </summary>
        public string ApplicationName { get; set; } = "AITool.ApplicationTests";

        /// <summary>
        /// 以当前测试运行目录作为内容根目录即可满足场景需要。
        /// </summary>
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        /// <summary>
        /// 使用空文件提供器，避免测试触发实际文件访问。
        /// </summary>
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
