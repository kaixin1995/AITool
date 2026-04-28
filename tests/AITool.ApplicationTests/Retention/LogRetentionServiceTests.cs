using AITool.Domain.Detection;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Retention;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AITool.ApplicationTests.Retention;

// 日志保留策略测试，验证按运行时配置清理日志并回写结果
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

    [Fact]
    public async Task PruneAsync_uses_runtime_retention_settings_and_writes_back_prune_result()
    {
        _dbContext.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            UsageLogRetentionDays = 3,
            DetectionLogRetentionDays = 5
        });

        _dbContext.ProxyUsageLogs.AddRange(
            new ProxyUsageLog
            {
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "OpenAI",
                RequestModel = "gpt-5",
                TargetSiteId = Guid.NewGuid(),
                Status = "fail",
                InputTokens = 10,
                OutputTokens = 5,
                TotalTokens = 15,
                RequestedAt = DateTimeOffset.UtcNow.AddDays(-4)
            },
            new ProxyUsageLog
            {
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "OpenAI",
                RequestModel = "gpt-5",
                TargetSiteId = Guid.NewGuid(),
                Status = "success",
                InputTokens = 10,
                OutputTokens = 5,
                TotalTokens = 15,
                RequestedAt = DateTimeOffset.UtcNow.AddDays(-2)
            });

        _dbContext.DetectionLogs.AddRange(
            new DetectionLog
            {
                SiteId = Guid.NewGuid(),
                ModelLibraryItemId = Guid.NewGuid(),
                Status = "fail",
                DurationMs = 100,
                CheckedAt = DateTimeOffset.UtcNow.AddDays(-6)
            },
            new DetectionLog
            {
                SiteId = Guid.NewGuid(),
                ModelLibraryItemId = Guid.NewGuid(),
                Status = "success",
                DurationMs = 50,
                CheckedAt = DateTimeOffset.UtcNow.AddDays(-4)
            });

        await _dbContext.SaveChangesAsync();

        var beforePruneAt = DateTimeOffset.UtcNow;
        var result = await _service.PruneAsync();
        var settings = await _dbContext.SystemRuntimeSettings.SingleAsync(x => x.Id == 1);

        result.UsageLogPrunedCount.Should().Be(1);
        result.DetectionLogPrunedCount.Should().Be(1);

        _dbContext.ProxyUsageLogs.Should().ContainSingle();
        _dbContext.ProxyUsageLogs.Single().RequestedAt.Should().BeAfter(beforePruneAt.AddDays(-3).AddMinutes(-1));

        _dbContext.DetectionLogs.Should().ContainSingle();
        _dbContext.DetectionLogs.Single().CheckedAt.Should().BeAfter(beforePruneAt.AddDays(-5).AddMinutes(-1));

        settings.LastUsageLogPrunedCount.Should().Be(1);
        settings.LastDetectionLogPrunedCount.Should().Be(1);
        settings.LastUsageLogPrunedAt.Should().NotBeNull();
        settings.LastDetectionLogPrunedAt.Should().NotBeNull();
        settings.LastUsageLogPrunedAt.Should().BeOnOrAfter(beforePruneAt);
        settings.LastDetectionLogPrunedAt.Should().BeOnOrAfter(beforePruneAt);
    }

    [Fact]
    public async Task PruneAsync_keeps_logs_at_exact_cutoff_boundary()
    {
        var baseTime = new DateTimeOffset(2026, 04, 28, 12, 00, 00, TimeSpan.Zero);
        var service = new LogRetentionService(_dbContext, () => baseTime);

        _dbContext.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            UsageLogRetentionDays = 3,
            DetectionLogRetentionDays = 5
        });
        _dbContext.ProxyUsageLogs.Add(new ProxyUsageLog
        {
            AccessKeyId = Guid.NewGuid(),
            ProtocolType = "OpenAI",
            RequestModel = "gpt-5",
            TargetSiteId = Guid.NewGuid(),
            Status = "success",
            InputTokens = 10,
            OutputTokens = 5,
            TotalTokens = 15,
            RequestedAt = baseTime.AddDays(-3)
        });
        _dbContext.DetectionLogs.Add(new DetectionLog
        {
            SiteId = Guid.NewGuid(),
            ModelLibraryItemId = Guid.NewGuid(),
            Status = "success",
            DurationMs = 50,
            CheckedAt = baseTime.AddDays(-5)
        });
        await _dbContext.SaveChangesAsync();

        var result = await service.PruneAsync();
        var settings = await _dbContext.SystemRuntimeSettings.SingleAsync(x => x.Id == 1);

        result.UsageLogPrunedCount.Should().Be(0);
        result.DetectionLogPrunedCount.Should().Be(0);
        _dbContext.ProxyUsageLogs.Should().ContainSingle();
        _dbContext.DetectionLogs.Should().ContainSingle();
        settings.LastUsageLogPrunedCount.Should().Be(0);
        settings.LastDetectionLogPrunedCount.Should().Be(0);
        settings.LastUsageLogPrunedAt.Should().Be(baseTime);
        settings.LastDetectionLogPrunedAt.Should().Be(baseTime);
    }


    public void Dispose() => _dbContext.Dispose();
}
