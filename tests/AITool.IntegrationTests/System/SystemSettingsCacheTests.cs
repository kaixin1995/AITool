using AITool.Application.Operations;
using AITool.Domain.Operations;
using AITool.Infrastructure.Operations;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Web.Pages.Admin.System;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;

namespace AITool.IntegrationTests.System;

// 系统设置缓存测试，验证保存后代理运行时缓存会立刻刷新。
public sealed class SystemSettingsCacheTests : IAsyncDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-system-settings-cache-{Guid.NewGuid():N}.db");

    public SystemSettingsCacheTests()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
        services.AddScoped<ISystemRuntimeSettingsService, SystemRuntimeSettingsService>();
        services.AddSingleton<ProxyRequestMetadataCache>();
        services.AddSingleton<RouteCircuitStateStore>();
        services.AddSingleton<AnalyticsBackgroundQueryExecutor>();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task OnPostAsync_invalidates_runtime_settings_cache_immediately()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISystemRuntimeSettingsService>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        var circuitStore = scope.ServiceProvider.GetRequiredService<RouteCircuitStateStore>();
        var analyticsQueryExecutor = scope.ServiceProvider.GetRequiredService<AnalyticsBackgroundQueryExecutor>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

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

        var before = await cache.GetRuntimeSettingsAsync(CancellationToken.None);
        before.ProxyRequestTimeoutSeconds.Should().Be(8);
        before.ProxyRetryCount.Should().Be(1);
        before.DeveloperFeaturesEnabled.Should().BeFalse();

        var page = new SettingsModel(settingsService, cache, circuitStore, analyticsQueryExecutor)
        {
            Input = new UpdateSystemRuntimeSettingsRequest
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
            }
        };

        await page.OnPostAsync(CancellationToken.None);

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

    public async ValueTask DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
