using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AITool.Core.IntegrationTests;

/// <summary>
/// 验证 Core 运行时配置增量 Patch 同步。
/// 全量同步测试在 <see cref="CoreApiEndpointTests"/> 中覆盖，这里只关注增量 Patch 的行为。
/// </summary>
public sealed class CorePatchSyncTests
{
    /// <summary>
    /// 在 Core 尚未收到过全量快照时，增量 Patch 应被拒绝并提示先做全量同步。
    /// </summary>
    [Fact]
    public async Task Patch_sync_rejected_when_core_not_initialized()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var patch = new ConfigPatchPayload
        {
            ConfigVersion = 1,
            Categories = ["AccessKeys"],
            PatchHash = "sha256:placeholder",
            AccessKeys =
            [
                new CoreRuntimeAccessKey
                {
                    Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    KeyName = "prod-key",
                    PlainKey = "sk-prod",
                    AccessKeyHash = "ABCDEF",
                    MaskedValue = "sk-***",
                    IsEnabled = true
                }
            ]
        };

        var response = await client.PostAsJsonAsync("/api/core/config/patch-sync", patch);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("尚未初始化");
    }

    /// <summary>
    /// 全量同步成功后，增量 Patch 应能只更新特定类别。
    /// 验证 Patch 只携带 AccessKeys 时，配置版本正确递增。
    /// </summary>
    [Fact]
    public async Task Patch_sync_updates_only_specified_categories()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        // 先做一次全量同步初始化 Core
        var snapshot = CreateValidSnapshot(configVersion: 1);
        var fullSyncResponse = await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);
        fullSyncResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // 然后发送只包含 AccessKeys 的增量 Patch
        var patch = new ConfigPatchPayload
        {
            ConfigVersion = 2,
            Categories = ["AccessKeys"],
            PatchHash = "sha256:placeholder",
            AccessKeys =
            [
                new CoreRuntimeAccessKey
                {
                    Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    KeyName = "prod-key-updated",
                    PlainKey = "sk-prod-v2",
                    AccessKeyHash = "NEWHASH",
                    MaskedValue = "sk-***v2",
                    IsEnabled = true
                }
            ]
        };

        var patchResponse = await client.PostAsJsonAsync("/api/core/config/patch-sync", patch);
        var patchBody = await patchResponse.Content.ReadAsStringAsync();

        patchResponse.StatusCode.Should().Be(HttpStatusCode.OK, patchBody);
        patchBody.Should().Contain("\"applied\":true");
        patchBody.Should().Contain("\"ignored\":false");

        // 验证配置版本已更新
        var statusResponse = await client.GetAsync("/api/core/config/status");
        var statusBody = await statusResponse.Content.ReadAsStringAsync();
        statusBody.Should().Contain("\"configVersion\":2");
    }

    /// <summary>
    /// Patch 版本号不大于当前版本时，应被忽略。
    /// </summary>
    [Fact]
    public async Task Patch_sync_ignores_stale_version()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        // 先做全量同步到版本 5
        var snapshot = CreateValidSnapshot(configVersion: 5);
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        // 发送版本号 3 的 Patch（低于当前版本 5）
        var patch = new ConfigPatchPayload
        {
            ConfigVersion = 3,
            Categories = ["AccessKeys"],
            PatchHash = "sha256:placeholder",
            AccessKeys =
            [
                new CoreRuntimeAccessKey
                {
                    Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                    KeyName = "stale-key",
                    PlainKey = "sk-stale",
                    AccessKeyHash = "STALE",
                    MaskedValue = "sk-***",
                    IsEnabled = true
                }
            ]
        };

        var response = await client.PostAsJsonAsync("/api/core/config/patch-sync", patch);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("\"ignored\":true");
    }

    /// <summary>
    /// Patch 中携带未知类别名称时应返回 400。
    /// </summary>
    [Fact]
    public async Task Patch_sync_rejects_unknown_category()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        // 先初始化 Core
        var snapshot = CreateValidSnapshot(configVersion: 1);
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        var patch = new ConfigPatchPayload
        {
            ConfigVersion = 2,
            Categories = ["UnknownCategory"],
            PatchHash = "sha256:placeholder"
        };

        var response = await client.PostAsJsonAsync("/api/core/config/patch-sync", patch);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("未知的实体类别");
    }

    /// <summary>
    /// Patch 不携带任何类别时应返回 400。
    /// </summary>
    [Fact]
    public async Task Patch_sync_rejects_empty_categories()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var patch = new ConfigPatchPayload
        {
            ConfigVersion = 1,
            Categories = [],
            PatchHash = "sha256:placeholder"
        };

        var response = await client.PostAsJsonAsync("/api/core/config/patch-sync", patch);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("至少一个变更类别");
    }

    /// <summary>
    /// Patch 携带多个类别时，所有指定类别应被更新，版本号正确递增。
    /// </summary>
    [Fact]
    public async Task Patch_sync_updates_multiple_categories()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        // 先做一次全量同步
        var snapshot = CreateValidSnapshot(configVersion: 1);
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        // 发送包含 Sites + RouteRules 的多类别 Patch
        var newSiteId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var patch = new ConfigPatchPayload
        {
            ConfigVersion = 2,
            Categories = ["Sites", "RouteRules"],
            PatchHash = "sha256:placeholder",
            Sites =
            [
                new CoreRuntimeSite
                {
                    Id = newSiteId,
                    Name = "new-site",
                    BaseUrl = "https://new.example.com",
                    EndpointPathMode = "standard-root",
                    ApiKey = "sk-new",
                    ProtocolType = "OpenAI",
                    SupportsOpenAi = true,
                    SupportsAnthropic = false,
                    IsEnabled = true
                }
            ],
            RouteRules =
            [
                new CoreRuntimeRouteRule
                {
                    Id = Guid.NewGuid(),
                    ExternalModelName = "gpt-4",
                    UpstreamModelName = "gpt-4",
                    SiteId = newSiteId,
                    SiteModelName = "gpt-4",
                    Priority = 1,
                    ModelPriority = 1,
                    InstancePriority = 1,
                    IsEnabled = true,
                    AvailabilityMode = "AllDay",
                    TimeRangesJson = string.Empty
                }
            ]
        };

        var response = await client.PostAsJsonAsync("/api/core/config/patch-sync", patch);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("\"applied\":true");

        // 验证配置版本递增
        var statusResponse = await client.GetAsync("/api/core/config/status");
        var statusBody = await statusResponse.Content.ReadAsStringAsync();
        statusBody.Should().Contain("\"configVersion\":2");
    }

    // ─── 辅助方法 ───────────────────────────────────────────────────

    /// <summary>
    /// 构造一份最小合法的配置快照，包含一个站点和一个访问密钥。
    /// ConfigHash 由 ComputeHash 自动计算，确保与控制器校验逻辑一致。
    /// </summary>
    private static CoreRuntimeConfigSnapshot CreateValidSnapshot(long configVersion)
    {
        var snapshot = new CoreRuntimeConfigSnapshot
        {
            ConfigVersion = configVersion,
            GeneratedAt = DateTimeOffset.UtcNow,
            Sites =
            [
                new CoreRuntimeSite
                {
                    Id = Guid.NewGuid(),
                    Name = "test-site",
                    BaseUrl = "https://api.test.example.com",
                    EndpointPathMode = "append",
                    ApiKey = "sk-test-key",
                    ProtocolType = "OpenAI",
                    SupportsOpenAi = true,
                    SupportsAnthropic = false,
                    IsEnabled = true
                }
            ],
            Models =
            [
                new CoreRuntimeModel
                {
                    Id = Guid.NewGuid(),
                    ModelName = "gpt-4",
                    DisplayName = "GPT-4",
                    IsEnabled = true
                }
            ],
            SiteModelMappings =
            [
                new CoreRuntimeSiteModelMapping
                {
                    Id = Guid.NewGuid(),
                    SiteId = Guid.NewGuid(),
                    ModelLibraryItemId = Guid.NewGuid(),
                    RemoteModelName = "gpt-4",
                    LastStatus = "Healthy",
                    IsEnabled = true,
                    MaxConcurrency = 10
                }
            ],
            RouteEntries =
            [
                new CoreRuntimeRouteEntry
                {
                    Id = Guid.NewGuid(),
                    EntryName = "default"
                }
            ],
            RouteRules =
            [
                new CoreRuntimeRouteRule
                {
                    Id = Guid.NewGuid(),
                    ExternalModelName = "gpt-4",
                    UpstreamModelName = "gpt-4",
                    SiteId = Guid.NewGuid(),
                    SiteModelName = "gpt-4",
                    Priority = 1,
                    ModelPriority = 1,
                    InstancePriority = 1,
                    IsEnabled = true,
                    AvailabilityMode = "always",
                    TimeRangesJson = "{}"
                }
            ],
            AccessKeys =
            [
                new CoreRuntimeAccessKey
                {
                    Id = Guid.NewGuid(),
                    KeyName = "test-key",
                    PlainKey = "sk-access-test",
                    AccessKeyHash = "hash-test",
                    MaskedValue = "sk-***test",
                    IsEnabled = true
                }
            ],
            RuntimeSettings = new CoreRuntimeSettings
            {
                ProxyRequestTimeoutSeconds = 60,
                ProxyRetryCount = 1,
                CircuitBreakerFailureThreshold = 5,
                CircuitBreakerRecoveryMinutes = 2,
                ConcurrencyMode = 0,
                ConcurrencyQueueTimeoutSeconds = 120,
                ConversationLogEnabled = true
            }
        };

        // 使用与控制器一致的哈希计算方法，确保 full-sync 校验通过
        snapshot.ConfigHash = CoreRuntimeConfigSnapshotBuilder.ComputeHash(snapshot);
        return snapshot;
    }
}
