using AITool.Infrastructure.Operations;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AITool.ApplicationTests.Operations;

// 运行时设置服务 SQLite 测试，验证历史库缺表时也能自动恢复默认配置
public sealed class SystemRuntimeSettingsServiceSqliteTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _dbContext;
    private readonly SystemRuntimeSettingsService _service;

    public SystemRuntimeSettingsServiceSqliteTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        _dbContext = new AppDbContext(options);
        _dbContext.Database.EnsureCreated();
        _dbContext.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS SystemRuntimeSettings");
        _service = new SystemRuntimeSettingsService(_dbContext);
    }

    [Fact]
    public async Task GetOrCreateAsync_creates_default_record_when_system_runtime_settings_table_is_missing()
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

        var exists = await _dbContext.SystemRuntimeSettings.SingleAsync(x => x.Id == 1);
        exists.ProxyRequestTimeoutSeconds.Should().Be(60);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
    }
}
