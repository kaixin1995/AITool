using AITool.Domain.Detection;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Retention;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AITool.ApplicationTests.Retention;

// 日志保留策略测试，验证过期日志清理和近期日志保留
public sealed class LogRetentionServiceTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly LogRetentionService _service;

    public LogRetentionServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);
        _service = new LogRetentionService(_dbContext);
    }

    // 超过 7 天的日志应被清理
    [Fact]
    public async Task PruneAsync_removes_logs_older_than_7_days()
    {
        _dbContext.DetectionLogs.Add(new DetectionLog
        {
            SiteId = Guid.NewGuid(),
            ModelLibraryItemId = Guid.NewGuid(),
            Status = "fail",
            DurationMs = 100,
            CheckedAt = DateTimeOffset.UtcNow.AddDays(-10)
        });
        _dbContext.ProxyUsageLogs.Add(new ProxyUsageLog
        {
            AccessKeyId = Guid.NewGuid(),
            ProtocolType = "OpenAI",
            RequestModel = "gpt-5",
            TargetSiteId = Guid.NewGuid(),
            Status = "fail",
            InputTokens = 10,
            OutputTokens = 5,
            TotalTokens = 15,
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-8)
        });
        await _dbContext.SaveChangesAsync();

        await _service.PruneAsync();

        _dbContext.DetectionLogs.Should().BeEmpty();
        _dbContext.ProxyUsageLogs.Should().BeEmpty();
    }

    // 7 天内的日志应保留
    [Fact]
    public async Task PruneAsync_keeps_recent_logs()
    {
        _dbContext.DetectionLogs.Add(new DetectionLog
        {
            SiteId = Guid.NewGuid(),
            ModelLibraryItemId = Guid.NewGuid(),
            Status = "success",
            DurationMs = 50,
            CheckedAt = DateTimeOffset.UtcNow.AddDays(-3)
        });
        _dbContext.ProxyUsageLogs.Add(new ProxyUsageLog
        {
            AccessKeyId = Guid.NewGuid(),
            ProtocolType = "Anthropic",
            RequestModel = "claude-4",
            TargetSiteId = Guid.NewGuid(),
            Status = "success",
            InputTokens = 20,
            OutputTokens = 10,
            TotalTokens = 30,
            RequestedAt = DateTimeOffset.UtcNow.AddDays(-1)
        });
        await _dbContext.SaveChangesAsync();

        await _service.PruneAsync();

        _dbContext.DetectionLogs.Should().HaveCount(1);
        _dbContext.ProxyUsageLogs.Should().HaveCount(1);
    }

    public void Dispose() => _dbContext.Dispose();
}
