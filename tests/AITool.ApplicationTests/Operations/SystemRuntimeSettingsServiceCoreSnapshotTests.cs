using AITool.Application.CoreRuntime;
using AITool.Application.Operations;
using AITool.Domain.Models;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Operations;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace AITool.ApplicationTests.Operations;

/// <summary>
/// 验证基于数据库构建 Core 配置快照的系统运行时设置服务行为。
/// </summary>
public sealed class SystemRuntimeSettingsServiceCoreSnapshotTests : IDisposable
{
    private readonly AppDbContext _dbContext;
    private readonly SystemRuntimeSettingsService _service;

    /// <summary>
    /// 为每个测试构造独立内存数据库，避免配置快照数据相互污染。
    /// </summary>
    public SystemRuntimeSettingsServiceCoreSnapshotTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AppDbContext(options);
        _service = new SystemRuntimeSettingsService(_dbContext);
    }

    /// <summary>
    /// 构建 Core 配置快照时，应完整投影当前数据库中的主数据和运行时设置。
    /// </summary>
    [Fact]
    public async Task BuildCoreRuntimeConfigSnapshotAsync_projects_current_authoritative_data()
    {
        _dbContext.Sites.Add(new Site
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
        _dbContext.ModelLibraryItems.Add(new ModelLibraryItem
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            ModelName = "gpt-5.4",
            DisplayName = "GPT-5.4",
            IsEnabled = true
        });
        _dbContext.SiteModelMappings.Add(new SiteModelMapping
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            SiteId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ModelLibraryItemId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            RemoteModelName = "gpt-5.4",
            LastStatus = "success",
            IsEnabled = true,
            MaxConcurrency = 8
        });
        _dbContext.ProxyRouteEntries.Add(new ProxyRouteEntry
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            EntryName = "chat-prod"
        });
        _dbContext.ProxyRouteRules.Add(new ProxyRouteRule
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
        _dbContext.ProxyAccessKeys.Add(new ProxyAccessKey
        {
            Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
            KeyName = "prod-key",
            PlainKey = "sk-prod",
            AccessKeyHash = "ABCDEF",
            MaskedValue = "sk-***",
            IsEnabled = true
        });
        await _dbContext.SaveChangesAsync();

        var updated = await _service.UpdateAsync(new UpdateSystemRuntimeSettingsRequest
        {
            ProxyRequestTimeoutSeconds = 75,
            ProxyRetryCount = 2,
            DetectionRequestTimeoutSeconds = 60,
            DetectionRetryCount = 0,
            DetectionConcurrency = 1,
            CircuitBreakerFailureThreshold = 6,
            CircuitBreakerRecoveryMinutes = 4,
            UsageLogRetentionDays = 7,
            UsageLogAutoCleanupEnabled = true,
            DeveloperFeaturesEnabled = false,
            ConversationLogEnabled = true,
            ConcurrencyMode = 1,
            ConcurrencyQueueTimeoutSeconds = 180
        });

        var snapshot = await _service.BuildCoreRuntimeConfigSnapshotAsync(12);

        snapshot.ConfigVersion.Should().Be(12);
        snapshot.ConfigHash.Should().StartWith("sha256:");
        snapshot.Sites.Should().ContainSingle(x => x.Name == "Primary OpenAI");
        snapshot.Models.Should().ContainSingle(x => x.ModelName == "gpt-5.4");
        snapshot.SiteModelMappings.Should().ContainSingle(x => x.RemoteModelName == "gpt-5.4");
        snapshot.RouteEntries.Should().ContainSingle(x => x.EntryName == "chat-prod");
        snapshot.RouteRules.Should().ContainSingle(x => x.ExternalModelName == "chat-prod");
        snapshot.AccessKeys.Should().ContainSingle(x => x.KeyName == "prod-key");
        snapshot.RuntimeSettings.ProxyRequestTimeoutSeconds.Should().Be(updated.ProxyRequestTimeoutSeconds);
        snapshot.RuntimeSettings.ProxyRetryCount.Should().Be(updated.ProxyRetryCount);
        snapshot.RuntimeSettings.CircuitBreakerFailureThreshold.Should().Be(updated.CircuitBreakerFailureThreshold);
        snapshot.RuntimeSettings.CircuitBreakerRecoveryMinutes.Should().Be(updated.CircuitBreakerRecoveryMinutes);
        snapshot.RuntimeSettings.ConcurrencyMode.Should().Be(updated.ConcurrencyMode);
        snapshot.RuntimeSettings.ConcurrencyQueueTimeoutSeconds.Should().Be(updated.ConcurrencyQueueTimeoutSeconds);
        snapshot.RuntimeSettings.ConversationLogEnabled.Should().Be(updated.ConversationLogEnabled);
        CoreRuntimeConfigSnapshotBuilder.ComputeHash(snapshot).Should().Be(snapshot.ConfigHash);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }
}
