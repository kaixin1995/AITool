using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AITool.Core.IntegrationTests;

/// <summary>
/// Core 宿主代理端点集成测试。
/// 验证在配置快照下发后，代理端点能从快照数据中正确读取访问密钥和路由信息，
/// 完成密钥校验和模型列表查询等代理运行时关键路径。
/// </summary>
public sealed class CoreProxyEndpointTests
{
    // 测试用的明文访问密钥
    private const string TestAccessKey = "sk-test-proxy-access-key";

    // ─── 密钥校验与 /v1/models ────────────────────────────────────

    /// <summary>
    /// 未同步配置时，/v1/models 应返回 401（密钥校验失败）。
    /// </summary>
    [Fact]
    public async Task Models_endpoint_without_config_returns_unauthorized()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAccessKey);

        var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// 同步配置后，使用有效密钥访问 /v1/models 应返回 200 和模型列表。
    /// 验证代理缓存能从配置快照正确读取访问密钥和路由目标。
    /// </summary>
    [Fact]
    public async Task Models_endpoint_after_sync_with_valid_key_returns_ok()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        // 构造并下发包含已知密钥和路由的配置快照
        var snapshot = CreateProxyTestSnapshot(configVersion: 100);
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        // 使用与快照中哈希对应的明文密钥请求模型列表
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestAccessKey);
        var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ModelsListResponse>();
        body.Should().NotBeNull();
        body!.Data.Should().NotBeEmpty();
    }

    /// <summary>
    /// 同步配置后，使用无效密钥访问 /v1/models 应返回 401。
    /// </summary>
    [Fact]
    public async Task Models_endpoint_after_sync_with_wrong_key_returns_unauthorized()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var snapshot = CreateProxyTestSnapshot(configVersion: 101);
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-wrong-key");
        var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// 同步配置后，不携带任何认证头访问 /v1/models 应返回 401。
    /// </summary>
    [Fact]
    public async Task Models_endpoint_after_sync_without_auth_returns_unauthorized()
    {
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        var snapshot = CreateProxyTestSnapshot(configVersion: 102);
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot);

        var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// 重新同步新版本配置后，使用新密钥应能访问成功。
    /// 验证缓存失效和重建机制正常工作。
    /// </summary>
    [Fact]
    public async Task Models_endpoint_after_resync_with_new_key_returns_ok()
    {
        const string newAccessKey = "sk-new-proxy-key-after-resync";
        await using var factory = new CoreHostWebApplicationFactory();
        using var client = factory.CreateClient();

        // 第一次同步
        var snapshot1 = CreateProxyTestSnapshot(configVersion: 200);
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot1);

        // 第二次同步，使用不同的密钥
        var snapshot2 = CreateProxyTestSnapshot(configVersion: 201, accessKey: newAccessKey);
        await client.PostAsJsonAsync("/api/core/config/full-sync", snapshot2);

        // 新密钥应生效
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newAccessKey);
        var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ─── 辅助方法 ─────────────────────────────────────────────────

    /// <summary>
    /// 计算明文访问密钥的 SHA256 哈希值（大写十六进制），
    /// 与 ValidateAccessKeyAsync 中的哈希逻辑保持一致。
    /// </summary>
    private static string ComputeAccessKeyHash(string plainKey)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(plainKey));
        return Convert.ToHexString(hashBytes);
    }

    /// <summary>
    /// 构造一份用于代理端点测试的配置快照。
    /// SiteId 和路由规则中的 SiteId 保持一致，确保路由联查正确。
    /// </summary>
    private static CoreRuntimeConfigSnapshot CreateProxyTestSnapshot(
        long configVersion,
        string? accessKey = null)
    {
        accessKey ??= TestAccessKey;
        var siteId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var accessKeyHash = ComputeAccessKeyHash(accessKey);

        var snapshot = new CoreRuntimeConfigSnapshot
        {
            ConfigVersion = configVersion,
            GeneratedAt = DateTimeOffset.UtcNow,
            Sites =
            [
                new CoreRuntimeSite
                {
                    Id = siteId,
                    Name = "proxy-test-site",
                    BaseUrl = "https://api.proxy-test.example.com",
                    EndpointPathMode = "append",
                    ApiKey = "sk-upstream-test-key",
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
                    Id = modelId,
                    ModelName = "gpt-4o",
                    DisplayName = "GPT-4o",
                    IsEnabled = true
                }
            ],
            SiteModelMappings =
            [
                new CoreRuntimeSiteModelMapping
                {
                    Id = Guid.NewGuid(),
                    SiteId = siteId,
                    ModelLibraryItemId = modelId,
                    RemoteModelName = "gpt-4o",
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
                    ExternalModelName = "gpt-4o",
                    UpstreamModelName = "gpt-4o",
                    SiteId = siteId,
                    SiteModelName = "gpt-4o",
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
                    KeyName = "proxy-test-key",
                    PlainKey = accessKey,
                    AccessKeyHash = accessKeyHash,
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

        snapshot.ConfigHash = CoreRuntimeConfigSnapshotBuilder.ComputeHash(snapshot);
        return snapshot;
    }

    /// <summary>
    /// /v1/models 端点返回的模型列表反序列化辅助类型。
    /// </summary>
    private sealed class ModelsListResponse
    {
        public List<ModelItem> Data { get; set; } = [];
        public bool HasMore { get; set; }
    }

    /// <summary>
    /// 单个模型项反序列化辅助类型。
    /// </summary>
    private sealed class ModelItem
    {
        public string Type { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
    }
}
