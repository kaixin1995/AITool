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

// 使用日志服务测试，验证日志条目持久化和 Token 合计计算
public sealed class UsageLogServiceTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly ServiceProvider _serviceProvider;
    private readonly UsageLogService _service;

    public UsageLogServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddDbContext<AppDbContext>(dbOptions => dbOptions.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        _serviceProvider = services.BuildServiceProvider();
        _dbContext = _serviceProvider.GetRequiredService<AppDbContext>();

        var batchWriter = new ProxyUsageLogBatchWriter(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProxyUsageLogBatchWriter>.Instance,
            new TestHostEnvironment());
        _service = new UsageLogService(batchWriter);
    }

    // 正常日志写入后应能查到记录且 TotalTokens 为 Input + Cached + Output
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
    }

    // 回退流程日志应完整保存尝试链路元数据
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

    // 多次写入应产生多条记录
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

        var logs = await _dbContext.ProxyUsageLogs.ToListAsync();
        logs.Should().HaveCount(3);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _serviceProvider.Dispose();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "AITool.ApplicationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
