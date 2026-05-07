using AITool.Application.Operations;
using AITool.Infrastructure.Operations;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AITool.ApplicationTests.Operations;

// 系统运行时设置服务测试，验证默认值创建和更新持久化行为
public sealed class SystemRuntimeSettingsServiceTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly SystemRuntimeSettingsService _service;

    public SystemRuntimeSettingsServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);
        _service = new SystemRuntimeSettingsService(_dbContext);
    }

    [Fact]
    public async Task GetOrCreateAsync_returns_defaults_when_database_is_empty()
    {
        var settings = await _service.GetOrCreateAsync();

        settings.Id.Should().Be(1);
        settings.ProxyRequestTimeoutSeconds.Should().Be(60);
        settings.ProxyRetryCount.Should().Be(1);
        settings.DetectionRequestTimeoutSeconds.Should().Be(60);
        settings.DetectionRetryCount.Should().Be(0);
        settings.DetectionConcurrency.Should().Be(1);
        settings.CircuitBreakerFailureThreshold.Should().Be(5);
        settings.CircuitBreakerRecoveryMinutes.Should().Be(2);
        settings.UsageLogRetentionDays.Should().Be(7);
        settings.UsageLogAutoCleanupEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task GetOrCreateAsync_uses_fixed_id_1_record_when_other_rows_exist()
    {
        _dbContext.SystemRuntimeSettings.Add(new AITool.Domain.Operations.SystemRuntimeSettings
        {
            Id = 2,
            ProxyRequestTimeoutSeconds = 120,
            ProxyRetryCount = 5,
            DetectionRequestTimeoutSeconds = 30,
            DetectionRetryCount = 2,
            DetectionConcurrency = 4,
            CircuitBreakerFailureThreshold = 8,
            CircuitBreakerRecoveryMinutes = 6,
            UsageLogRetentionDays = 30,
            UsageLogAutoCleanupEnabled = false
        });
        await _dbContext.SaveChangesAsync();

        var settings = await _service.GetOrCreateAsync();

        settings.Id.Should().Be(1);
        settings.ProxyRequestTimeoutSeconds.Should().Be(60);
        settings.ProxyRetryCount.Should().Be(1);
        settings.DetectionRequestTimeoutSeconds.Should().Be(60);
        settings.DetectionRetryCount.Should().Be(0);
        settings.DetectionConcurrency.Should().Be(1);
        settings.CircuitBreakerFailureThreshold.Should().Be(5);
        settings.CircuitBreakerRecoveryMinutes.Should().Be(2);
        settings.UsageLogRetentionDays.Should().Be(7);
        settings.UsageLogAutoCleanupEnabled.Should().BeTrue();
        _dbContext.SystemRuntimeSettings.Should().ContainSingle(x => x.Id == 1);
    }

    [Fact]
    public async Task UpdateAsync_persists_timeout_retry_and_retention_changes()
    {
        await _service.GetOrCreateAsync();

        var updated = await _service.UpdateAsync(new UpdateSystemRuntimeSettingsRequest
        {
            ProxyRequestTimeoutSeconds = 90,
            ProxyRetryCount = 3,
            DetectionRequestTimeoutSeconds = 45,
            DetectionRetryCount = 2,
            DetectionConcurrency = 6,
            CircuitBreakerFailureThreshold = 7,
            CircuitBreakerRecoveryMinutes = 9,
            UsageLogRetentionDays = 14,
            UsageLogAutoCleanupEnabled = false
        });

        updated.Id.Should().Be(1);
        updated.ProxyRequestTimeoutSeconds.Should().Be(90);
        updated.ProxyRetryCount.Should().Be(3);
        updated.DetectionRequestTimeoutSeconds.Should().Be(45);
        updated.DetectionRetryCount.Should().Be(2);
        updated.DetectionConcurrency.Should().Be(6);
        updated.CircuitBreakerFailureThreshold.Should().Be(7);
        updated.CircuitBreakerRecoveryMinutes.Should().Be(9);
        updated.UsageLogRetentionDays.Should().Be(14);
        updated.UsageLogAutoCleanupEnabled.Should().BeFalse();

        var reloaded = await _service.GetOrCreateAsync();
        reloaded.Id.Should().Be(1);
        reloaded.ProxyRequestTimeoutSeconds.Should().Be(90);
        reloaded.ProxyRetryCount.Should().Be(3);
        reloaded.DetectionRequestTimeoutSeconds.Should().Be(45);
        reloaded.DetectionRetryCount.Should().Be(2);
        reloaded.DetectionConcurrency.Should().Be(6);
        reloaded.CircuitBreakerFailureThreshold.Should().Be(7);
        reloaded.CircuitBreakerRecoveryMinutes.Should().Be(9);
        reloaded.UsageLogRetentionDays.Should().Be(14);
        reloaded.UsageLogAutoCleanupEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateAsync_clamps_invalid_values_to_minimums()
    {
        var updated = await _service.UpdateAsync(new UpdateSystemRuntimeSettingsRequest
        {
            ProxyRequestTimeoutSeconds = 0,
            ProxyRetryCount = -1,
            DetectionRequestTimeoutSeconds = 0,
            DetectionRetryCount = -3,
            DetectionConcurrency = 0,
            CircuitBreakerFailureThreshold = 0,
            CircuitBreakerRecoveryMinutes = 0,
            UsageLogRetentionDays = 0,
            UsageLogAutoCleanupEnabled = true
        });

        updated.Id.Should().Be(1);
        updated.ProxyRequestTimeoutSeconds.Should().Be(1);
        updated.ProxyRetryCount.Should().Be(0);
        updated.DetectionRequestTimeoutSeconds.Should().Be(1);
        updated.DetectionRetryCount.Should().Be(0);
        updated.DetectionConcurrency.Should().Be(1);
        updated.CircuitBreakerFailureThreshold.Should().Be(1);
        updated.CircuitBreakerRecoveryMinutes.Should().Be(1);
        updated.UsageLogRetentionDays.Should().Be(1);
        updated.UsageLogAutoCleanupEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task ClearUsageLogsAsync_filters_by_source_and_time_range()
    {
        await _service.GetOrCreateAsync();
        var baseTime = new DateTimeOffset(2026, 05, 07, 12, 00, 00, TimeSpan.Zero);

        _dbContext.ProxyUsageLogs.AddRange(
            new AITool.Domain.Proxy.ProxyUsageLog
            {
                Id = Guid.NewGuid(),
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "OpenAI",
                RequestModel = "gpt-5",
                AttemptedModel = "gpt-5",
                TargetSiteId = Guid.NewGuid(),
                Status = "success",
                Source = "codex",
                ErrorMessage = string.Empty,
                ReasoningEffort = string.Empty,
                RequestedAt = baseTime.AddHours(-3)
            },
            new AITool.Domain.Proxy.ProxyUsageLog
            {
                Id = Guid.NewGuid(),
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "OpenAI",
                RequestModel = "gpt-5",
                AttemptedModel = "gpt-5",
                TargetSiteId = Guid.NewGuid(),
                Status = "success",
                Source = "codex",
                ErrorMessage = string.Empty,
                ReasoningEffort = string.Empty,
                RequestedAt = baseTime.AddHours(-1)
            },
            new AITool.Domain.Proxy.ProxyUsageLog
            {
                Id = Guid.NewGuid(),
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "OpenAI",
                RequestModel = "gpt-5",
                AttemptedModel = "gpt-5",
                TargetSiteId = Guid.NewGuid(),
                Status = "success",
                Source = "chat",
                ErrorMessage = string.Empty,
                ReasoningEffort = string.Empty,
                RequestedAt = baseTime.AddHours(-2)
            });
        await _dbContext.SaveChangesAsync();

        var deletedCount = await _service.ClearUsageLogsAsync(new ClearUsageLogsRequest
        {
            Source = "codex",
            StartTime = baseTime.AddHours(-4),
            EndTime = baseTime.AddHours(-2)
        });

        deletedCount.Should().Be(1);
        _dbContext.ProxyUsageLogs.Should().HaveCount(2);
        _dbContext.ProxyUsageLogs.Should().Contain(x => x.Source == "codex" && x.RequestedAt == baseTime.AddHours(-1));
        _dbContext.ProxyUsageLogs.Should().Contain(x => x.Source == "chat");
    }

    public void Dispose() => _dbContext.Dispose();
}
