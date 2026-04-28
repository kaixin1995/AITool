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
        settings.UsageLogRetentionDays.Should().Be(7);
        settings.DetectionLogRetentionDays.Should().Be(7);
    }

    [Fact]
    public async Task GetOrCreateAsync_uses_fixed_id_1_record_when_other_rows_exist()
    {
        _dbContext.SystemRuntimeSettings.Add(new AITool.Domain.Operations.SystemRuntimeSettings
        {
            Id = 2,
            ProxyRequestTimeoutSeconds = 120,
            ProxyRetryCount = 5,
            UsageLogRetentionDays = 30,
            DetectionLogRetentionDays = 30
        });
        await _dbContext.SaveChangesAsync();

        var settings = await _service.GetOrCreateAsync();

        settings.Id.Should().Be(1);
        settings.ProxyRequestTimeoutSeconds.Should().Be(60);
        settings.ProxyRetryCount.Should().Be(1);
        settings.UsageLogRetentionDays.Should().Be(7);
        settings.DetectionLogRetentionDays.Should().Be(7);
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
            UsageLogRetentionDays = 14,
            DetectionLogRetentionDays = 21
        });

        updated.Id.Should().Be(1);
        updated.ProxyRequestTimeoutSeconds.Should().Be(90);
        updated.ProxyRetryCount.Should().Be(3);
        updated.UsageLogRetentionDays.Should().Be(14);
        updated.DetectionLogRetentionDays.Should().Be(21);

        var reloaded = await _service.GetOrCreateAsync();
        reloaded.Id.Should().Be(1);
        reloaded.ProxyRequestTimeoutSeconds.Should().Be(90);
        reloaded.ProxyRetryCount.Should().Be(3);
        reloaded.UsageLogRetentionDays.Should().Be(14);
        reloaded.DetectionLogRetentionDays.Should().Be(21);
    }

    [Fact]
    public async Task UpdateAsync_clamps_invalid_values_to_minimums()
    {
        var updated = await _service.UpdateAsync(new UpdateSystemRuntimeSettingsRequest
        {
            ProxyRequestTimeoutSeconds = 0,
            ProxyRetryCount = -1,
            UsageLogRetentionDays = 0,
            DetectionLogRetentionDays = -5
        });

        updated.Id.Should().Be(1);
        updated.ProxyRequestTimeoutSeconds.Should().Be(1);
        updated.ProxyRetryCount.Should().Be(0);
        updated.UsageLogRetentionDays.Should().Be(1);
        updated.DetectionLogRetentionDays.Should().Be(1);
    }

    public void Dispose() => _dbContext.Dispose();
}
