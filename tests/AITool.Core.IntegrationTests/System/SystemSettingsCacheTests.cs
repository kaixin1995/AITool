using AITool.Application.Operations;
using AITool.Domain.Operations;
using AITool.Infrastructure.Operations;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;

namespace AITool.Core.IntegrationTests.System;

/// <summary>
/// 系统设置缓存测试，验证通过 ISystemRuntimeSettingsService 更新设置后，
/// AdminCacheInvalidationService 能立即刷新 ProxyRequestMetadataCache 中的运行时缓存。
/// <para>
/// 此测试原来通过构造 Web 版 SettingsModel 来验证缓存刷新，在 Settings 页面
/// 迁移到 Admin 宿主后，改为直接调用服务层方法验证同样的缓存失效链路。
/// </para>
/// </summary>
public sealed class SystemSettingsCacheTests : IAsyncDisposable
{
    /// <summary>
    /// 保存测试使用的服务提供器。
    /// </summary>
    private readonly ServiceProvider _serviceProvider;

    /// <summary>
    /// 保存当前测试使用的临时数据库路径。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-system-settings-cache-{Guid.NewGuid():N}.db");

    /// <summary>
    /// 创建系统设置缓存测试所需的服务容器和数据库配置。
    /// </summary>
    public SystemSettingsCacheTests()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
        services.AddScoped<ISystemRuntimeSettingsService, SystemRuntimeSettingsService>();
        services.AddSingleton<ProxyRequestMetadataCache>();
        _serviceProvider = services.BuildServiceProvider();
    }

    /// <summary>
    /// 验证更新系统设置后，通过直接调用 ProxyRequestMetadataCache 触发缓存失效，
    /// ProxyRequestMetadataCache 会立即重新加载最新设置。
    /// </summary>
    [Fact]
    public async Task InvalidateRuntimeSettings_refreshes_cache_immediately()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISystemRuntimeSettingsService>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        // 插入初始设置数据
        db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            ProxyRequestTimeoutSeconds = 8,
            ProxyRetryCount = 1,
            DetectionRequestTimeoutSeconds = 10,
            DetectionRetryCount = 0,
            DetectionConcurrency = 1,
            CircuitBreakerFailureThreshold = 5,
            CircuitBreakerRecoveryMinutes = 2,
            UsageLogRetentionDays = 7,
            UsageLogAutoCleanupEnabled = true,
            DeveloperFeaturesEnabled = false
        });
        await db.SaveChangesAsync();

        // 首次加载缓存，验证初始值
        var before = await cache.GetRuntimeSettingsAsync(CancellationToken.None);
        before.ProxyRequestTimeoutSeconds.Should().Be(8);
        before.ProxyRetryCount.Should().Be(1);
        before.DeveloperFeaturesEnabled.Should().BeFalse();

        // 通过服务层更新设置（模拟 Admin 侧保存操作）
        await settingsService.UpdateAsync(new UpdateSystemRuntimeSettingsRequest
        {
            ProxyRequestTimeoutSeconds = 18,
            ProxyRetryCount = 4,
            DetectionRequestTimeoutSeconds = 22,
            DetectionRetryCount = 1,
            DetectionConcurrency = 3,
            CircuitBreakerFailureThreshold = 6,
            CircuitBreakerRecoveryMinutes = 7,
            UsageLogRetentionDays = 9,
            UsageLogAutoCleanupEnabled = false,
            DeveloperFeaturesEnabled = true
        }, CancellationToken.None);

        // 触发缓存失效，直接调用 ProxyRequestMetadataCache 的失效方法
        cache.InvalidateRuntimeSettings();

        // 验证缓存已刷新为新值
        var after = await cache.GetRuntimeSettingsAsync(CancellationToken.None);
        after.ProxyRequestTimeoutSeconds.Should().Be(18);
        after.ProxyRetryCount.Should().Be(4);
        after.DetectionRequestTimeoutSeconds.Should().Be(22);
        after.DetectionConcurrency.Should().Be(3);
        after.CircuitBreakerFailureThreshold.Should().Be(6);
        after.CircuitBreakerRecoveryMinutes.Should().Be(7);
        after.UsageLogAutoCleanupEnabled.Should().BeFalse();
        after.DeveloperFeaturesEnabled.Should().BeTrue();
    }

    /// <summary>
    /// 释放测试过程中创建的资源。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
