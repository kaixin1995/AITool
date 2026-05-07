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
            UsageLogAutoCleanupEnabled = true
        });

        _dbContext.ProxyUsageLogs.AddRange(
            new ProxyUsageLog
            {
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "OpenAI",
                RequestModel = "gpt-5",
                AttemptedModel = "gpt-5",
                TargetSiteId = Guid.NewGuid(),
                Status = "fail",
                ErrorMessage = string.Empty,
                ReasoningEffort = string.Empty,
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
                AttemptedModel = "gpt-5",
                TargetSiteId = Guid.NewGuid(),
                Status = "success",
                ErrorMessage = string.Empty,
                ReasoningEffort = string.Empty,
                InputTokens = 10,
                OutputTokens = 5,
                TotalTokens = 15,
                RequestedAt = DateTimeOffset.UtcNow.AddDays(-2)
            });

        await _dbContext.SaveChangesAsync();

        var beforePruneAt = DateTimeOffset.UtcNow;
        var result = await _service.PruneAsync();
        var settings = await _dbContext.SystemRuntimeSettings.SingleAsync(x => x.Id == 1);

        result.UsageLogPrunedCount.Should().Be(1);
        _dbContext.ProxyUsageLogs.Should().ContainSingle();
        _dbContext.ProxyUsageLogs.Single().RequestedAt.Should().BeAfter(beforePruneAt.AddDays(-3).AddMinutes(-1));
        settings.LastUsageLogPrunedCount.Should().Be(1);
        settings.LastUsageLogPrunedAt.Should().NotBeNull();
        settings.LastUsageLogPrunedAt.Should().BeOnOrAfter(beforePruneAt);
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
            UsageLogAutoCleanupEnabled = true
        });
        _dbContext.ProxyUsageLogs.Add(new ProxyUsageLog
        {
            AccessKeyId = Guid.NewGuid(),
            ProtocolType = "OpenAI",
            RequestModel = "gpt-5",
            AttemptedModel = "gpt-5",
            TargetSiteId = Guid.NewGuid(),
            Status = "success",
            ErrorMessage = string.Empty,
            ReasoningEffort = string.Empty,
            InputTokens = 10,
            OutputTokens = 5,
            TotalTokens = 15,
            RequestedAt = baseTime.AddDays(-3)
        });
        await _dbContext.SaveChangesAsync();

        var result = await service.PruneAsync();
        var settings = await _dbContext.SystemRuntimeSettings.SingleAsync(x => x.Id == 1);

        result.UsageLogPrunedCount.Should().Be(0);
        _dbContext.ProxyUsageLogs.Should().ContainSingle();
        settings.LastUsageLogPrunedCount.Should().Be(0);
        settings.LastUsageLogPrunedAt.Should().Be(baseTime);
    }

    [Fact]
    public async Task PruneAsync_skips_cleanup_when_auto_cleanup_disabled()
    {
        var baseTime = new DateTimeOffset(2026, 04, 28, 12, 00, 00, TimeSpan.Zero);
        var service = new LogRetentionService(_dbContext, () => baseTime);

        _dbContext.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            UsageLogRetentionDays = 3,
            UsageLogAutoCleanupEnabled = false
        });
        _dbContext.ProxyUsageLogs.Add(new ProxyUsageLog
        {
            AccessKeyId = Guid.NewGuid(),
            ProtocolType = "OpenAI",
            RequestModel = "gpt-5",
            AttemptedModel = "gpt-5",
            TargetSiteId = Guid.NewGuid(),
            Status = "success",
            ErrorMessage = string.Empty,
            ReasoningEffort = string.Empty,
            RequestedAt = baseTime.AddDays(-10)
        });
        await _dbContext.SaveChangesAsync();

        var result = await service.PruneAsync();
        var settings = await _dbContext.SystemRuntimeSettings.SingleAsync(x => x.Id == 1);

        result.UsageLogPrunedCount.Should().Be(0);
        _dbContext.ProxyUsageLogs.Should().ContainSingle();
        settings.LastUsageLogPrunedCount.Should().Be(0);
        settings.LastUsageLogPrunedAt.Should().Be(baseTime);
    }

    public void Dispose() => _dbContext.Dispose();
}
