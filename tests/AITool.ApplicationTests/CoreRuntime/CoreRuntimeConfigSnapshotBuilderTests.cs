using AITool.Application.CoreRuntime;
using AITool.Domain.Models;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;

namespace AITool.ApplicationTests.CoreRuntime;

/// <summary>
/// 验证 Core 运行时配置快照构建行为。
/// </summary>
public sealed class CoreRuntimeConfigSnapshotBuilderTests
{
    /// <summary>
    /// 构建快照后应包含完整配置并生成稳定哈希。
    /// </summary>
    [Fact]
    public void Build_creates_snapshot_with_expected_payload_and_hash()
    {
        var generatedAt = new DateTimeOffset(2026, 6, 10, 8, 30, 0, TimeSpan.Zero);
        var snapshot = CoreRuntimeConfigSnapshotBuilder.Build(
            [
                new Site
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
                }
            ],
            [
                new ModelLibraryItem
                {
                    Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    ModelName = "gpt-5.4",
                    DisplayName = "GPT-5.4",
                    IsEnabled = true
                }
            ],
            [
                new SiteModelMapping
                {
                    Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    SiteId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    ModelLibraryItemId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    RemoteModelName = "gpt-5.4",
                    LastStatus = "success",
                    IsEnabled = true,
                    MaxConcurrency = 6
                }
            ],
            [
                new ProxyRouteEntry
                {
                    Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
                    EntryName = "chat-prod"
                }
            ],
            [
                new ProxyRouteRule
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
                }
            ],
            [
                new ProxyAccessKey
                {
                    Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    KeyName = "prod-key",
                    PlainKey = "sk-prod",
                    AccessKeyHash = "ABCDEF",
                    MaskedValue = "sk-***",
                    IsEnabled = true
                }
            ],
            new SystemRuntimeSettings
            {
                ProxyRequestTimeoutSeconds = 45,
                ProxyRetryCount = 2,
                CircuitBreakerFailureThreshold = 7,
                CircuitBreakerRecoveryMinutes = 9,
                ConcurrencyMode = 1,
                ConcurrencyQueueTimeoutSeconds = 180,
                ConversationLogEnabled = true
            },
            12,
            generatedAt);

        snapshot.ConfigVersion.Should().Be(12);
        snapshot.GeneratedAt.Should().Be(generatedAt);
        snapshot.ConfigHash.Should().StartWith("sha256:");
        snapshot.Sites.Should().ContainSingle();
        snapshot.Models.Should().ContainSingle();
        snapshot.SiteModelMappings.Should().ContainSingle();
        snapshot.RouteEntries.Should().ContainSingle();
        snapshot.RouteRules.Should().ContainSingle();
        snapshot.AccessKeys.Should().ContainSingle();
        snapshot.RuntimeSettings.ProxyRequestTimeoutSeconds.Should().Be(45);
        snapshot.RuntimeSettings.ProxyRetryCount.Should().Be(2);
        snapshot.RuntimeSettings.CircuitBreakerFailureThreshold.Should().Be(7);
        snapshot.RuntimeSettings.CircuitBreakerRecoveryMinutes.Should().Be(9);
        snapshot.RuntimeSettings.ConcurrencyMode.Should().Be(1);
        snapshot.RuntimeSettings.ConcurrencyQueueTimeoutSeconds.Should().Be(180);
        snapshot.RuntimeSettings.ConversationLogEnabled.Should().BeTrue();

        var recomputedHash = CoreRuntimeConfigSnapshotBuilder.ComputeHash(snapshot);
        recomputedHash.Should().Be(snapshot.ConfigHash);
    }

    /// <summary>
    /// 相同配置多次构建时，哈希应保持一致。
    /// </summary>
    [Fact]
    public void ComputeHash_returns_same_value_for_same_payload()
    {
        var snapshotA = new CoreRuntimeConfigSnapshot
        {
            Sites = [new CoreRuntimeSite { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "Site A" }],
            Models = [new CoreRuntimeModel { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), ModelName = "gpt-5.4" }],
            SiteModelMappings = [new CoreRuntimeSiteModelMapping { Id = Guid.Parse("33333333-3333-3333-3333-333333333333") }],
            RouteEntries = [new CoreRuntimeRouteEntry { Id = Guid.Parse("44444444-4444-4444-4444-444444444444"), EntryName = "chat-prod" }],
            RouteRules = [new CoreRuntimeRouteRule { Id = Guid.Parse("55555555-5555-5555-5555-555555555555"), ExternalModelName = "chat-prod" }],
            AccessKeys = [new CoreRuntimeAccessKey { Id = Guid.Parse("66666666-6666-6666-6666-666666666666"), KeyName = "key" }],
            RuntimeSettings = new CoreRuntimeSettings { ProxyRequestTimeoutSeconds = 60 }
        };
        var snapshotB = new CoreRuntimeConfigSnapshot
        {
            Sites = [new CoreRuntimeSite { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "Site A" }],
            Models = [new CoreRuntimeModel { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), ModelName = "gpt-5.4" }],
            SiteModelMappings = [new CoreRuntimeSiteModelMapping { Id = Guid.Parse("33333333-3333-3333-3333-333333333333") }],
            RouteEntries = [new CoreRuntimeRouteEntry { Id = Guid.Parse("44444444-4444-4444-4444-444444444444"), EntryName = "chat-prod" }],
            RouteRules = [new CoreRuntimeRouteRule { Id = Guid.Parse("55555555-5555-5555-5555-555555555555"), ExternalModelName = "chat-prod" }],
            AccessKeys = [new CoreRuntimeAccessKey { Id = Guid.Parse("66666666-6666-6666-6666-666666666666"), KeyName = "key" }],
            RuntimeSettings = new CoreRuntimeSettings { ProxyRequestTimeoutSeconds = 60 }
        };

        CoreRuntimeConfigSnapshotBuilder.ComputeHash(snapshotA)
            .Should().Be(CoreRuntimeConfigSnapshotBuilder.ComputeHash(snapshotB));
    }
}
