using AITool.Application.CoreRuntime;
using AITool.Application.Operations;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.DependencyInjection;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.Admin.IntegrationTests.Infrastructure;

/// <summary>
/// 用于构建 Admin 测试宿主，准备 Core 配置同步测试所需的基础数据。
/// 此工厂启动 Admin 宿主（含数据库），模拟 Admin 与 Core 之间的配置同步场景。
/// </summary>
internal sealed class CoreConfigSyncWebApplicationFactory : WebApplicationFactory<AITool.Admin.AdminProgramMarker>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-core-config-sync-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));

            // 注册代理运行时基础设施，提供 IUsageLogService、事件 spool、
            // 配置快照等 Core 控制器所需的服务。
            var spoolPath = Path.Combine(Path.GetTempPath(), $"aitool-core-event-spool-{Guid.NewGuid():N}");
            services.AddProxyRuntimeInfrastructure(
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ProxyForwarding:RequestTimeoutSeconds"] = "30",
                    ["ProxyForwarding:RetryCount"] = "1"
                }).Build().GetSection("ProxyForwarding"),
                spoolPath,
                useCoreRuntimeConfigProviderForCache: false);

            // 注册 CoreRuntimeConfigProvider，Core 配置同步控制器需要此服务。
            services.AddSingleton(new CoreRuntimeConfigFileOptions
            {
                FilePath = Path.Combine(Path.GetTempPath(), $"aitool-core-runtime-config-{Guid.NewGuid():N}.json")
            });
            services.AddSingleton<CoreRuntimeConfigProvider>();
            services.AddSingleton<ICoreRuntimeConfigProvider>(sp => sp.GetRequiredService<CoreRuntimeConfigProvider>());

            // 注册 ProxyRequestMetadataCache（不使用配置快照路径），
            // 使缓存从数据库查询路由和密钥数据。
            services.RemoveAll<ProxyRequestMetadataCache>();
            services.AddSingleton<ProxyRequestMetadataCache>(sp =>
            {
                var memoryCache = sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
                var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                return new ProxyRequestMetadataCache(memoryCache, scopeFactory);
            });

            // 添加 Core 程序集到控制器发现，使 api/core/* 和代理端点可用。
            services.AddControllers()
                .AddApplicationPart(typeof(AITool.Core.Controllers.Core.CoreConfigSyncController).Assembly);
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        SeedAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 基于当前测试数据库中的主数据构建一份 Core 配置快照。
    /// </summary>
    internal async Task<CoreRuntimeConfigSnapshot> BuildSnapshotAsync(long configVersion)
    {
        await using var scope = Services.CreateAsyncScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<ISystemRuntimeSettingsService>();
        return await settingsService.BuildCoreRuntimeConfigSnapshotAsync(configVersion);
    }

    internal async Task PublishUsageLogEventAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        // UsageLog 事件已改为统一 "proxy-request" 事件发布。
        // 直接通过事件总线发布一条用于 replay 验证。
        var sequenceProvider = scope.ServiceProvider.GetRequiredService<CoreEventSequenceProvider>();
        var eventBus = scope.ServiceProvider.GetRequiredService<CoreAdminEventBus>();
        var envelope = CoreAdminEventEnvelopeBuilder.CreateUnifiedProxyEnvelope(
            sequenceProvider.Next(),
            new CoreUnifiedProxyEvent
            {
                TraceId = Guid.NewGuid(),
                RequestId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                AccessKeyId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                ProtocolType = "OpenAI",
                ForwardingMode = "direct",
                RequestModel = "chat-prod",
                AttemptedModel = "gpt-5.4",
                TargetSiteId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                TargetSiteName = "test-site",
                Status = "success",
                Source = "proxy",
                InputTokens = 10,
                CachedTokens = 2,
                OutputTokens = 6,
                IsStreaming = false,
                TotalDurationMs = 80,
                RequestedAt = new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero),
                FinishedAt = new DateTimeOffset(2026, 6, 10, 10, 0, 1, TimeSpan.Zero),
                Attempts =
                [
                    new CoreUnifiedAttemptDetail
                    {
                        AttemptId = Guid.NewGuid(),
                        AttemptIndex = 0,
                        AttemptedModel = "gpt-5.4",
                        TargetSiteId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                        TargetSiteName = "test-site",
                        Status = "success",
                        InputTokens = 10,
                        CachedTokens = 2,
                        OutputTokens = 6,
                        IsStreaming = false,
                        TotalDurationMs = 80,
                        StartedAt = new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero),
                        FinishedAt = new DateTimeOffset(2026, 6, 10, 10, 0, 1, TimeSpan.Zero)
                    }
                ]
            });
        await eventBus.PublishAsync(envelope);
        await Task.Delay(150); // 等待 Spool 后台服务落盘
    }

    private async Task SeedAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        db.Sites.Add(new AITool.Domain.Sites.Site
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "Primary OpenAI",
            BaseUrl = "https://api.example.com",
            EndpointPathMode = "standard-root",
            ApiKey = "site-key",
            ProtocolType = "OpenAI",
            SupportsOpenAi = true,
            SupportsAnthropic = false,
            IsEnabled = true
        });
        db.ModelLibraryItems.Add(new AITool.Domain.Models.ModelLibraryItem
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            ModelName = "gpt-5.4",
            DisplayName = "GPT-5.4",
            IsEnabled = true
        });
        db.SiteModelMappings.Add(new AITool.Domain.SiteCatalog.SiteModelMapping
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            SiteId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ModelLibraryItemId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            RemoteModelName = "gpt-5.4",
            LastStatus = "success",
            IsEnabled = true,
            MaxConcurrency = 8
        });
        db.ProxyRouteEntries.Add(new AITool.Domain.Proxy.ProxyRouteEntry
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            EntryName = "chat-prod"
        });
        db.ProxyRouteRules.Add(new AITool.Domain.Proxy.ProxyRouteRule
        {
            Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            ExternalModelName = "chat-prod",
            UpstreamModelName = "gpt-5.4",
            SiteId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            SiteModelName = "gpt-5.4",
            Priority = 0,
            ModelPriority = 0,
            InstancePriority = 0,
            IsEnabled = true,
            AvailabilityMode = "AllDay",
            TimeRangesJson = string.Empty
        });
        db.ProxyAccessKeys.Add(new AITool.Domain.Proxy.ProxyAccessKey
        {
            Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
            KeyName = "prod-key",
            PlainKey = "sk-prod",
            AccessKeyHash = "ABCDEF",
            MaskedValue = "sk-***",
            IsEnabled = true
        });
        db.SystemRuntimeSettings.Add(new AITool.Domain.Operations.SystemRuntimeSettings
        {
            Id = 1,
            ProxyRequestTimeoutSeconds = 60,
            ProxyRetryCount = 1,
            DetectionRequestTimeoutSeconds = 60,
            DetectionRetryCount = 0,
            DetectionConcurrency = 1,
            CircuitBreakerFailureThreshold = 5,
            CircuitBreakerRecoveryMinutes = 2,
            UsageLogRetentionDays = 7,
            UsageLogAutoCleanupEnabled = true,
            DeveloperFeaturesEnabled = false,
            ConversationLogEnabled = true,
            ConcurrencyMode = 0,
            ConcurrencyQueueTimeoutSeconds = 120
        });

        await db.SaveChangesAsync();
    }
}
