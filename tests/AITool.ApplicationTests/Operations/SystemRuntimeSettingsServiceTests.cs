using AITool.Application.Operations;
using AITool.Infrastructure.Operations;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AITool.ApplicationTests.Operations;

/// <summary>
/// 验证系统运行时设置服务在默认值创建、更新保存和日志清理筛选上的行为。
/// </summary>
public sealed class SystemRuntimeSettingsServiceTests : IDisposable
{
    /// <summary>
    /// 内存数据库上下文，用于准备和校验运行时设置数据。
    /// </summary>
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 被测服务，负责读取、创建和更新运行时设置。
    /// </summary>
    private readonly SystemRuntimeSettingsService _service;

    /// <summary>
    /// 为每个测试构建独立的内存数据库，避免设置数据互相污染。
    /// </summary>
    public SystemRuntimeSettingsServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);
        _service = new SystemRuntimeSettingsService(_dbContext);
    }

    /// <summary>
    /// 当数据库中还没有设置记录时，服务应自动创建一条默认配置。
    /// </summary>
    [Fact]
    public async Task GetOrCreateAsync_returns_defaults_when_database_is_empty()
    {
        // 直接调用读取接口，验证其同时具备自动初始化能力。
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
        settings.DeveloperFeaturesEnabled.Should().BeFalse();
        settings.ConcurrencyMode.Should().Be(0);
        settings.ConcurrencyQueueTimeoutSeconds.Should().Be(120);
    }

    /// <summary>
    /// 即使表里已有其他主键的旧记录，服务也应强制使用固定的 1 号配置记录。
    /// </summary>
    [Fact]
    public async Task GetOrCreateAsync_uses_fixed_id_1_record_when_other_rows_exist()
    {
        // 先插入一条非 1 号记录，模拟历史脏数据或异常数据。
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
            UsageLogAutoCleanupEnabled = false,
            DeveloperFeaturesEnabled = true
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
        settings.DeveloperFeaturesEnabled.Should().BeFalse();
        _dbContext.SystemRuntimeSettings.Should().ContainSingle(x => x.Id == 1);
    }

    /// <summary>
    /// 更新接口应把超时、重试和保留策略等配置完整写回数据库。
    /// </summary>
    [Fact]
    public async Task UpdateAsync_persists_timeout_retry_and_retention_changes()
    {
        // 先确保默认记录存在，再执行更新流程。
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
            UsageLogAutoCleanupEnabled = false,
            DeveloperFeaturesEnabled = true,
            ConcurrencyMode = 1,
            ConcurrencyQueueTimeoutSeconds = 300
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
        updated.DeveloperFeaturesEnabled.Should().BeTrue();
        updated.ConcurrencyMode.Should().Be(1);
        updated.ConcurrencyQueueTimeoutSeconds.Should().Be(300);

        // 再次读取设置，确认变更不仅返回正确，也已经持久化保存。
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
        reloaded.DeveloperFeaturesEnabled.Should().BeTrue();
        reloaded.ConcurrencyMode.Should().Be(1);
        reloaded.ConcurrencyQueueTimeoutSeconds.Should().Be(300);
    }

    /// <summary>
    /// 输入非法值时，服务应自动收敛到允许的最小值，避免把无效配置写入系统。
    /// </summary>
    [Fact]
    public async Task UpdateAsync_clamps_invalid_values_to_minimums()
    {
        // 故意提交 0 和负数，验证服务层的最小值保护是否生效。
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
            UsageLogAutoCleanupEnabled = true,
            DeveloperFeaturesEnabled = true,
            ConcurrencyMode = -1,
            ConcurrencyQueueTimeoutSeconds = 0
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
        updated.DeveloperFeaturesEnabled.Should().BeTrue();
        updated.ConcurrencyMode.Should().Be(0);
        updated.ConcurrencyQueueTimeoutSeconds.Should().Be(1);
    }

    /// <summary>
    /// 清理使用日志时，应同时按照来源和时间范围两个条件精确筛选。
    /// </summary>
    [Fact]
    public async Task ClearUsageLogsAsync_filters_by_source_and_time_range()
    {
        await _service.GetOrCreateAsync();
        // 固定基准时间，便于清晰构造筛选窗口。
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

    /// <summary>
    /// 没有传任何筛选条件时，应清空当前所有使用日志。
    /// </summary>
    [Fact]
    public async Task ClearUsageLogsAsync_clears_all_when_filters_are_empty()
    {
        await _service.GetOrCreateAsync();

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
                RequestedAt = DateTimeOffset.UtcNow.AddHours(-2)
            },
            new AITool.Domain.Proxy.ProxyUsageLog
            {
                Id = Guid.NewGuid(),
                AccessKeyId = Guid.NewGuid(),
                ProtocolType = "Anthropic",
                RequestModel = "claude-4",
                AttemptedModel = "claude-4-sonnet",
                TargetSiteId = Guid.NewGuid(),
                Status = "fail",
                Source = "chat",
                ErrorMessage = "timeout",
                ReasoningEffort = string.Empty,
                RequestedAt = DateTimeOffset.UtcNow.AddHours(-1)
            });
        await _dbContext.SaveChangesAsync();

        // 使用空请求对象，验证服务会走全量删除路径。
        var deletedCount = await _service.ClearUsageLogsAsync(new ClearUsageLogsRequest());

        deletedCount.Should().Be(2);
        _dbContext.ProxyUsageLogs.Should().BeEmpty();
    }

    /// <summary>
    /// 释放测试使用的数据库资源。
    /// </summary>
    public void Dispose()
    {
        _dbContext.Dispose();
    }
}
