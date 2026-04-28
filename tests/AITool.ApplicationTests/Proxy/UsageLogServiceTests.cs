using AITool.Application.UsageLogs;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AITool.ApplicationTests.Proxy;

// 使用日志服务测试，验证日志条目持久化和 Token 合计计算
public sealed class UsageLogServiceTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly UsageLogService _service;

    public UsageLogServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);
        _service = new UsageLogService(_dbContext);
    }

    // 正常日志写入后应能查到记录且 TotalTokens 为 Input + Output
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
            OutputTokens = 50
        };

        await _service.LogAsync(entry);

        var log = await _dbContext.ProxyUsageLogs.SingleAsync();
        log.ProtocolType.Should().Be("OpenAI");
        log.RequestModel.Should().Be("gpt-5");
        log.Status.Should().Be("success");
        log.TotalTokens.Should().Be(150);
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
            OutputTokens = 0
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

    public void Dispose() => _dbContext.Dispose();
}
