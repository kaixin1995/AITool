using System.Net;
using System.Net.Http.Json;
using AITool.Application.CoreRuntime;
using AITool.Application.Operations;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Operations;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.Core;

/// <summary>
/// 验证 Core 运行时配置全量同步最小闭环。
/// </summary>
public sealed class CoreConfigSyncTests
{
    /// <summary>
    /// 当 Core 还没有任何配置时，应先处于未就绪状态；收到一份合法快照后进入 ready。
    /// 再次提交同版本同哈希配置时应被忽略，避免 Admin 重启触发无意义切换。
    /// </summary>
    [Fact]
    public async Task Full_sync_switches_core_from_not_ready_to_ready_and_ignores_same_snapshot()
    {
        await using var factory = new CoreConfigSyncWebApplicationFactory();
        using var client = factory.CreateClient();

        var readyBefore = await client.GetAsync("/api/core/ready");
        var readyBeforeBody = await readyBefore.Content.ReadAsStringAsync();
        readyBefore.StatusCode.Should().Be(HttpStatusCode.OK, readyBeforeBody);
        readyBeforeBody.Should().Contain("\"ready\":false");

        var snapshot = await factory.BuildSnapshotAsync(1);
        var syncResponse = await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);
        var syncBody = await syncResponse.Content.ReadAsStringAsync();
        syncResponse.StatusCode.Should().Be(HttpStatusCode.OK, syncBody);
        syncBody.Should().Contain("\"applied\":true");
        syncBody.Should().Contain("\"ignored\":false");

        var readyAfter = await client.GetAsync("/api/core/ready");
        var readyAfterBody = await readyAfter.Content.ReadAsStringAsync();
        readyAfter.StatusCode.Should().Be(HttpStatusCode.OK, readyAfterBody);
        readyAfterBody.Should().Contain("\"ready\":true");

        var statusResponse = await client.GetAsync("/api/core/config/status");
        var statusBody = await statusResponse.Content.ReadAsStringAsync();
        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK, statusBody);
        statusBody.Should().Contain("\"configVersion\":1");
        statusBody.Should().Contain(snapshot.ConfigHash);

        var ignoredResponse = await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);
        var ignoredBody = await ignoredResponse.Content.ReadAsStringAsync();
        ignoredResponse.StatusCode.Should().Be(HttpStatusCode.OK, ignoredBody);
        ignoredBody.Should().Contain("\"applied\":false");
        ignoredBody.Should().Contain("\"ignored\":true");
    }

    /// <summary>
    /// 提交被篡改哈希的快照时，Core 应拒绝这份配置，避免错误配置覆盖当前运行态。
    /// </summary>
    [Fact]
    public async Task Full_sync_rejects_snapshot_with_invalid_hash()
    {
        await using var factory = new CoreConfigSyncWebApplicationFactory();
        using var client = factory.CreateClient();

        var snapshot = await factory.BuildSnapshotAsync(2);
        snapshot.ConfigHash = "sha256:tampered";

        var response = await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("配置哈希校验失败");
    }
}

internal sealed class CoreConfigSyncWebApplicationFactory : WebApplicationFactory<Program>
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
