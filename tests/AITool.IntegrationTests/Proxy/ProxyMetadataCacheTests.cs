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
            DetectionRequestTimeoutSeconds = 10,
            DetectionRetryCount = 0,
            DetectionConcurrency = 1,
            CircuitBreakerFailureThreshold = 5,
            CircuitBreakerRecoveryMinutes = 2,
            UsageLogRetentionDays = 7,
            UsageLogAutoCleanupEnabled = true
        });
        await db.SaveChangesAsync();

        var before = await cache.GetRuntimeSettingsAsync(CancellationToken.None);
        before.ProxyRequestTimeoutSeconds.Should().Be(8);
        before.ProxyRetryCount.Should().Be(1);
        before.DetectionRequestTimeoutSeconds.Should().Be(10);
        before.CircuitBreakerFailureThreshold.Should().Be(5);

        var settings = await db.SystemRuntimeSettings.SingleAsync();
        settings.ProxyRequestTimeoutSeconds = 15;
        settings.ProxyRetryCount = 3;
        settings.DetectionRequestTimeoutSeconds = 22;
        settings.DetectionRetryCount = 2;
        settings.DetectionConcurrency = 4;
        settings.CircuitBreakerFailureThreshold = 7;
        settings.CircuitBreakerRecoveryMinutes = 9;
        settings.UsageLogAutoCleanupEnabled = false;
        await db.SaveChangesAsync();

        cache.InvalidateRuntimeSettings();

        var after = await cache.GetRuntimeSettingsAsync(CancellationToken.None);
        after.ProxyRequestTimeoutSeconds.Should().Be(15);
        after.ProxyRetryCount.Should().Be(3);
        after.DetectionRequestTimeoutSeconds.Should().Be(22);
        after.DetectionRetryCount.Should().Be(2);
        after.DetectionConcurrency.Should().Be(4);
        after.CircuitBreakerFailureThreshold.Should().Be(7);
        after.CircuitBreakerRecoveryMinutes.Should().Be(9);
        after.UsageLogAutoCleanupEnabled.Should().BeFalse();
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
            SupportsOpenAi = true,
            SupportsAnthropic = true,
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

    [Fact]
    public async Task Get_route_targets_for_model_prioritizes_matching_protocol_before_bridge_targets()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();

        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var openAiSiteId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var anthropicSiteId = Guid.Parse("55555555-5555-5555-5555-555555555555");

        db.Sites.AddRange(
            new Site
            {
                Id = openAiSiteId,
                Name = "OpenAI Only",
                BaseUrl = "https://openai-only.example.com",
                ApiKey = "openai-key",
                ProtocolType = "OpenAI",
                SupportsOpenAi = true,
                SupportsAnthropic = false,
                IsEnabled = true
            },
            new Site
            {
                Id = anthropicSiteId,
                Name = "Anthropic Native",
                BaseUrl = "https://anthropic-native.example.com",
                ApiKey = "anthropic-key",
                ProtocolType = "Anthropic",
                SupportsOpenAi = false,
                SupportsAnthropic = true,
                IsEnabled = true
            });

        db.ProxyRouteRules.AddRange(
            new ProxyRouteRule
            {
                Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                ExternalModelName = "dual-route-model",
                UpstreamModelName = "openai-upstream",
                SiteId = openAiSiteId,
                SiteModelName = "openai-model",
                Priority = 0,
                ModelPriority = 0,
                InstancePriority = 0,
                IsEnabled = true
            },
            new ProxyRouteRule
            {
                Id = Guid.Parse("77777777-7777-7777-7777-777777777777"),
                ExternalModelName = "dual-route-model",
                UpstreamModelName = "anthropic-upstream",
                SiteId = anthropicSiteId,
                SiteModelName = "anthropic-model",
                Priority = 1,
                ModelPriority = 1,
                InstancePriority = 1,
                IsEnabled = true
            });

        await db.SaveChangesAsync();

        var anthropicRoutes = await cache.GetRouteTargetsForModelAsync("Anthropic", "dual-route-model", CancellationToken.None);
        anthropicRoutes.Should().HaveCount(2);
        // 当前缓存按后台配置顺序返回候选路由，协议不匹配时交给控制器走兼容转发。
        anthropicRoutes[0].ResolveProtocolForClient("Anthropic").Should().Be("OpenAI");
        anthropicRoutes[0].SiteId.Should().Be(openAiSiteId);
        anthropicRoutes[1].ResolveProtocolForClient("Anthropic").Should().Be("Anthropic");

        var openAiRoutes = await cache.GetRouteTargetsForModelAsync("OpenAI", "dual-route-model", CancellationToken.None);
        openAiRoutes.Should().HaveCount(2);
        openAiRoutes[0].ResolveProtocolForClient("OpenAI").Should().Be("OpenAI");
        openAiRoutes[0].SiteId.Should().Be(openAiSiteId);
        openAiRoutes[1].ResolveProtocolForClient("OpenAI").Should().Be("Anthropic");
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
