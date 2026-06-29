using AITool.ApplicationTests;
using AITool.Infrastructure.Operations;
using AITool.Infrastructure.Persistence;
using FluentAssertions;

namespace AITool.ApplicationTests.Operations;

/// <summary>
/// 使用 SQLite 内存库验证运行时设置服务在真实关系型表结构上的基础创建行为。
/// </summary>
public sealed class SystemRuntimeSettingsServiceSqliteTests : IDisposable
{
    /// <summary>
    /// 基于 SQLite 的数据库上下文，用于验证关系型存储场景。
    /// </summary>
    private readonly AppDbContext _dbContext;

    /// <summary>
    /// 被测服务，负责读取或创建默认运行时设置。
    /// </summary>
    private readonly SystemRuntimeSettingsService _service;

    /// <summary>
    /// 数据库清理回调。
    /// </summary>
    private readonly Action _disposeDatabase;

    /// <summary>
    /// 初始化临时 SQLite 数据库，并确保表结构已经创建完成。
    /// </summary>
    public SystemRuntimeSettingsServiceSqliteTests()
    {
        var (dbContext, dispose) = TestDatabaseFactory.Create();
        _dbContext = dbContext;
        _disposeDatabase = dispose;
        _service = new SystemRuntimeSettingsService(_dbContext);
    }

    /// <summary>
    /// 表存在但没有任何设置记录时，服务应创建默认的 1 号配置并落库。
    /// </summary>
    [Fact]
    public async Task GetOrCreateAsync_creates_default_record_when_table_exists_but_row_is_missing()
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
        settings.DeveloperFeaturesEnabled.Should().BeFalse();

        // 再次从数据库读取，确认默认记录确实写入了底层表。
        var exists = await _dbContext.SystemRuntimeSettings.SingleAsync(x => x.Id == 1);
        exists.ProxyRequestTimeoutSeconds.Should().Be(60);
        exists.DeveloperFeaturesEnabled.Should().BeFalse();
    }

    /// <summary>
    /// 释放上下文和连接，确保 SQLite 内存库被正确回收。
    /// </summary>
    public void Dispose()
    {
        _disposeDatabase();
    }
}
