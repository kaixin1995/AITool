using System.Security.Cryptography;
using System.Text;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.IntegrationTests.Proxy;

// 代理元数据缓存测试，验证失效后能及时读取到最新配置。
public sealed class ProxyMetadataCacheTests : IAsyncDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-proxy-metadata-cache-{Guid.NewGuid():N}.db");

    public ProxyMetadataCacheTests()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
        services.AddSingleton<ProxyRequestMetadataCache>();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task InvalidateAccessKeys_refreshes_validation_result()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var rawKey = "cache-key";
        db.ProxyAccessKeys.Add(new ProxyAccessKey
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            KeyName = "cache",
            PlainKey = rawKey,
            AccessKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))),
            MaskedValue = "sk-***",
            IsEnabled = true
        });
        await db.SaveChangesAsync();

        (await cache.ValidateAccessKeyAsync(rawKey, CancellationToken.None)).Should().NotBeNull();

        var accessKey = await db.ProxyAccessKeys.SingleAsync();
        accessKey.IsEnabled = false;
        await db.SaveChangesAsync();

        // 缓存未失效前仍会命中旧快照，这里先确认失效动作确实有意义。
        (await cache.ValidateAccessKeyAsync(rawKey, CancellationToken.None)).Should().NotBeNull();

        cache.InvalidateAccessKeys();

        (await cache.ValidateAccessKeyAsync(rawKey, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task InvalidateRuntimeSettings_refreshes_timeout_and_retry_values()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            ProxyRequestTimeoutSeconds = 8,
            ProxyRetryCount = 1,
            UsageLogRetentionDays = 7,
            DetectionLogRetentionDays = 7
        });
        await db.SaveChangesAsync();

        var before = await cache.GetRuntimeSettingsAsync(CancellationToken.None);
        before.ProxyRequestTimeoutSeconds.Should().Be(8);
        before.ProxyRetryCount.Should().Be(1);

        var settings = await db.SystemRuntimeSettings.SingleAsync();
        settings.ProxyRequestTimeoutSeconds = 15;
        settings.ProxyRetryCount = 3;
        await db.SaveChangesAsync();

        cache.InvalidateRuntimeSettings();

        var after = await cache.GetRuntimeSettingsAsync(CancellationToken.None);
        after.ProxyRequestTimeoutSeconds.Should().Be(15);
        after.ProxyRetryCount.Should().Be(3);
    }

    [Fact]
    public async Task InvalidateRouteTargets_refreshes_enabled_route_snapshot()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var siteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        db.Sites.Add(new Site
        {
            Id = siteId,
            Name = "Route Cache Site",
            BaseUrl = "https://route-cache.example.com",
            ApiKey = "route-key",
            ProtocolType = "OpenAI",
            IsEnabled = true
        });
        db.ProxyRouteRules.Add(new ProxyRouteRule
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            ExternalModelName = "cache-route-model",
            UpstreamModelName = "cache-upstream",
            SiteId = siteId,
            SiteModelName = "cache-site-model",
            Priority = 0,
            ModelPriority = 0,
            InstancePriority = 0,
            IsEnabled = true
        });
        await db.SaveChangesAsync();

        (await cache.GetRouteTargetsForModelAsync("OpenAI", "cache-route-model", CancellationToken.None)).Should().HaveCount(1);

        var route = await db.ProxyRouteRules.SingleAsync();
        route.IsEnabled = false;
        await db.SaveChangesAsync();

        cache.InvalidateRouteTargets();

        (await cache.GetRouteTargetsForModelAsync("OpenAI", "cache-route-model", CancellationToken.None)).Should().BeEmpty();
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
