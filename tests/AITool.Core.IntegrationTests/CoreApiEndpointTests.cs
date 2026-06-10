using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AITool.Core.IntegrationTests;

/// <summary>
/// Core 宿主 API 端点集成测试。
/// 覆盖健康检查、就绪检查、运行时状态、配置状态、全量同步、握手、事件确认与回放等端点。
/// 每个测试方法使用独立的 WebApplicationFactory 实例，确保测试之间完全隔离。
/// </summary>
public sealed class CoreApiEndpointTests
{
    // ─── 健康检查 ───────────────────────────────────────────────────

    /// <summary>
    /// GET /health 应返回 200 并包含 ok 状态。
    /// </summary>
    [Fact]
    public async Task Health_root_returns_ok()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ok");
    }

    /// <summary>
    /// GET api/core/health 应返回 200 和 { status: "ok" }。
    /// </summary>
    [Fact]
    public async Task Core_health_returns_ok_status()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/core/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("ok");
    }

    // ─── 就绪检查 ───────────────────────────────────────────────────

    /// <summary>
    /// 在尚未同步配置时，GET api/core/ready 应返回 ready=false。
    /// </summary>
    [Fact]
    public async Task Ready_before_sync_returns_not_ready()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/core/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("ready").GetBoolean().Should().BeFalse();
        body.GetProperty("reason").GetString().Should().Be("No runtime config snapshot loaded");
    }

    // ─── 运行时状态 ─────────────────────────────────────────────────

    /// <summary>
    /// 在尚未同步配置时，GET api/core/runtime/status 应返回 not-ready 状态，
    /// 并包含完整的运行时元数据字段。
    /// </summary>
    [Fact]
    public async Task Runtime_status_before_sync_returns_not_ready()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/core/runtime/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("state").GetString().Should().Be("not-ready");
        body.GetProperty("activeRequestCount").GetInt32().Should().Be(0);
        body.GetProperty("latestSequenceId").GetInt64().Should().Be(0);
        body.GetProperty("appliedConfigVersion").GetInt64().Should().Be(0);
        body.GetProperty("appliedConfigHash").GetString().Should().BeEmpty();
        body.TryGetProperty("coreStartedAt", out _).Should().BeTrue();
        body.TryGetProperty("hasSpoolBacklog", out _).Should().BeTrue();
    }

    // ─── 配置状态 ───────────────────────────────────────────────────

    /// <summary>
    /// 在尚未同步配置时，GET api/core/config/status 应返回 ready=false 和空配置信息。
    /// </summary>
    [Fact]
    public async Task Config_status_before_sync_returns_empty()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/core/config/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("ready").GetBoolean().Should().BeFalse();
        body.GetProperty("configVersion").GetInt64().Should().Be(0);
        body.GetProperty("configHash").GetString().Should().BeEmpty();
        body.GetProperty("hasLastGoodConfig").GetBoolean().Should().BeFalse();
    }

    // ─── 全量配置同步 ───────────────────────────────────────────────

    /// <summary>
    /// POST api/core/config/full-sync 传入合法快照应成功应用并返回 applied=true。
    /// </summary>
    [Fact]
    public async Task Full_sync_with_valid_snapshot_applies_config()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var snapshot = CreateValidSnapshot(configVersion: 1);
        var response = await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("applied").GetBoolean().Should().BeTrue();
        body.GetProperty("ignored").GetBoolean().Should().BeFalse();
        body.GetProperty("configVersion").GetInt64().Should().Be(1);
        body.GetProperty("configHash").GetString().Should().NotBeEmpty();
    }

    /// <summary>
    /// 重复提交同一版本和哈希的快照，应返回 ignored=true。
    /// </summary>
    [Fact]
    public async Task Full_sync_duplicate_version_returns_ignored()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        // 先成功同步一次
        var snapshot = CreateValidSnapshot(configVersion: 2);
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        // 再提交相同快照
        var response = await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("applied").GetBoolean().Should().BeFalse();
        body.GetProperty("ignored").GetBoolean().Should().BeTrue();
    }

    /// <summary>
    /// 提交 ConfigVersion <= 0 的快照应返回 400。
    /// </summary>
    [Fact]
    public async Task Full_sync_zero_version_returns_bad_request()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var snapshot = CreateValidSnapshot(configVersion: 0);
        var response = await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// 提交哈希不匹配的快照应返回 400。
    /// </summary>
    [Fact]
    public async Task Full_sync_wrong_hash_returns_bad_request()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var snapshot = CreateValidSnapshot(configVersion: 3);
        snapshot.ConfigHash = "sha256:INVALIDHASH";

        var response = await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// 提交缺少站点数据的快照应返回 400。
    /// </summary>
    [Fact]
    public async Task Full_sync_no_sites_returns_bad_request()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var snapshot = CreateValidSnapshot(configVersion: 4);
        snapshot.Sites.Clear();

        var response = await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// 提交缺少访问密钥的快照应返回 400。
    /// </summary>
    [Fact]
    public async Task Full_sync_no_access_keys_returns_bad_request()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var snapshot = CreateValidSnapshot(configVersion: 5);
        snapshot.AccessKeys.Clear();

        var response = await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// 成功同步后，config/status 应反映已就绪状态。
    /// </summary>
    [Fact]
    public async Task After_full_sync_config_status_shows_ready()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var snapshot = CreateValidSnapshot(configVersion: 10);
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        var response = await client.GetAsync("/api/core/config/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("ready").GetBoolean().Should().BeTrue();
        body.GetProperty("configVersion").GetInt64().Should().Be(10);
        body.GetProperty("configHash").GetString().Should().NotBeEmpty();
        body.GetProperty("hasLastGoodConfig").GetBoolean().Should().BeTrue();
    }

    /// <summary>
    /// 成功同步后，/api/core/ready 应返回 ready=true。
    /// </summary>
    [Fact]
    public async Task After_full_sync_ready_returns_true()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var snapshot = CreateValidSnapshot(configVersion: 11);
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        var response = await client.GetAsync("/api/core/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("ready").GetBoolean().Should().BeTrue();
    }

    /// <summary>
    /// 成功同步后，/api/core/runtime/status 应返回 ready 状态和正确的配置版本。
    /// </summary>
    [Fact]
    public async Task After_full_sync_runtime_status_shows_ready()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var snapshot = CreateValidSnapshot(configVersion: 12);
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        var response = await client.GetAsync("/api/core/runtime/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("state").GetString().Should().Be("ready");
        body.GetProperty("appliedConfigVersion").GetInt64().Should().Be(12);
        body.GetProperty("appliedConfigHash").GetString().Should().NotBeEmpty();
    }

    // ─── 握手 ───────────────────────────────────────────────────────

    /// <summary>
    /// 当 Core 尚未加载配置且 Admin 版本为 0 时，
    /// 决策为 admin-version-behind（Admin 版本 0 低于 Core 未配置时的默认版本 0，
    /// 但 CoreConfigSyncDecisionResolver 对 current==null 直接返回 full-sync-required）。
    /// 实际上 Core 当前无配置（current is null），所以 Resolver 返回 full-sync-required。
    /// </summary>
    [Fact]
    public async Task Handshake_without_config_returns_full_sync_required()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var request = new CoreAdminHandshakeRequest
        {
            AdminInstanceId = "test-admin-001",
            AdminStartedAt = DateTimeOffset.UtcNow,
            CurrentConfigVersion = 0,
            CurrentConfigHash = string.Empty
        };

        var response = await client.PostAsJsonAsync("/api/core/config/handshake", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Core 当前无配置（current is null），Resolver 第一条规则即返回 full-sync-required
        body.GetProperty("configSyncDecision").GetString().Should().Be("full-sync-required");
        body.GetProperty("ready").GetBoolean().Should().BeFalse();
        body.GetProperty("appliedConfigVersion").GetInt64().Should().Be(0);
        var instanceId = body.GetProperty("coreInstanceId").GetString();
        instanceId.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// 当 Core 已有匹配的配置时，握手应返回 noop 决策。
    /// </summary>
    [Fact]
    public async Task Handshake_with_matching_config_returns_noop()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        // 先同步一份配置
        var snapshot = CreateValidSnapshot(configVersion: 20);
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        var request = new CoreAdminHandshakeRequest
        {
            AdminInstanceId = "test-admin-002",
            AdminStartedAt = DateTimeOffset.UtcNow,
            CurrentConfigVersion = 20,
            CurrentConfigHash = snapshot.ConfigHash
        };

        var response = await client.PostAsJsonAsync("/api/core/config/handshake", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("configSyncDecision").GetString().Should().Be("noop");
        body.GetProperty("ready").GetBoolean().Should().BeTrue();
    }

    // ─── 事件确认 (Ack) ────────────────────────────────────────────

    /// <summary>
    /// 提交合法的 ack 请求应返回确认的序号和时间。
    /// </summary>
    [Fact]
    public async Task Ack_with_valid_request_returns_acked_sequence_id()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var request = new CoreAdminAckRequest
        {
            AdminInstanceId = "test-admin-010",
            AckedSequenceId = 42,
            AckedAt = DateTimeOffset.UtcNow
        };

        var response = await client.PostAsJsonAsync("/api/core/events/ack", request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("ackedSequenceId").GetInt64().Should().Be(42);
    }

    /// <summary>
    /// 提交负数的 AckedSequenceId 应返回 400。
    /// </summary>
    [Fact]
    public async Task Ack_negative_sequence_id_returns_bad_request()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var request = new CoreAdminAckRequest
        {
            AdminInstanceId = "test-admin-011",
            AckedSequenceId = -1,
            AckedAt = DateTimeOffset.UtcNow
        };

        var response = await client.PostAsJsonAsync("/api/core/events/ack", request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ─── 事件回放 (Replay) ─────────────────────────────────────────

    /// <summary>
    /// 初始状态下 replay 应返回空列表。
    /// </summary>
    [Fact]
    public async Task Replay_initial_returns_empty_list()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/core/events/replay?afterSequenceId=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.ValueKind.Should().Be(JsonValueKind.Array);
        body.GetArrayLength().Should().Be(0);
    }

    /// <summary>
    /// 提交负数的 afterSequenceId 应返回 400。
    /// </summary>
    [Fact]
    public async Task Replay_negative_after_sequence_id_returns_bad_request()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/core/events/replay?afterSequenceId=-1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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
