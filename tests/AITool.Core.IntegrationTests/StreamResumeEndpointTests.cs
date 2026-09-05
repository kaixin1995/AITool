using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace AITool.Core.IntegrationTests;

/// <summary>
/// 流式响应断线续传端点测试（T3.2 协议接线）：
/// 第一连接读若干帧取消 → 第二连接带 X-Stream-Resume 重入 → 重放缓冲帧先于新实时流发出。
/// 语义（v1）：重放段 = 自 lastSeq+1 起的已缓冲原始帧；随后是新上游请求的实时流。
/// 与守恒测试串行执行（共享 last-good-config 文件路径）。
/// </summary>
[Collection("CoreStandalone")]
public sealed class StreamResumeEndpointTests
{
    private const string AccessKey = "test-access-key-standalone";
    private const string ResumeHeader = "X-Stream-Resume";
    private const string RequestIdHeader = "X-Stream-Request-Id";

    private static string ConfigDirectory => Path.Combine(AppContext.BaseDirectory, "core-runtime");
    private static string ConfigFilePath => Path.Combine(ConfigDirectory, "last-good-config.json");
    // 注：TestHost 对「中途遗弃的流式响应」有缓冲缺陷，断线重连端到端场景由
    // StreamResumeKestrelTests（真实 Kestrel 进程）验证；本文件仅保留普通客户端语义测试。

    /// <summary>
    /// 普通客户端（无 Resume 头）行为零变化：响应头 X-Stream-Request-Id 可供中继识别（纯增量），
    /// 正文与旧版完全一致，无任何重放逻辑介入。
    /// </summary>
    [Fact]
    public async Task Plain_streaming_client_gets_resume_id_but_body_unchanged()
    {
        using var upstream = new MockOpenAiUpstream(streamByDefault: true, frameCount: 3, frameDelayMs: 30);
        WriteLastGoodConfig(upstream.Port);

        try
        {
            await using var factory = new StandaloneCoreFactory();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessKey);

            var response = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
                {
                    Content = JsonContent.Create(new { model = "gpt-conserve", stream = true, messages = new[] { new { role = "user", content = "hi" } } })
                },
                HttpCompletionOption.ResponseHeadersRead);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Headers.TryGetValues(RequestIdHeader, out var ids).Should().BeTrue(
                "恢复标识头对全部客户端可见（供中继识别），属纯增量响应头");
            ids!.First().Should().NotBeNullOrEmpty();
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("mock-chunk-1");
            body.Should().Contain("[DONE]");
        }
        finally
        {
            DeleteLastGoodConfig();
        }
    }

    private static async Task<string> ReadAvailableAsync(StreamReader reader)
    {
        var sb = new StringBuilder();
        while (true)
        {
            try
            {
                var line = await reader.ReadLineAsync();
                if (line is null)
                {
                    break;
                }

                sb.AppendLine(line);
            }
            catch
            {
                break; // 剩余内容已不可读：保留已读部分。
            }
        }

        return sb.ToString();
    }

    private static void WriteLastGoodConfig(int upstreamPort)
    {
        var dir = ConfigDirectory;
        Directory.CreateDirectory(dir);
        if (File.Exists(ConfigFilePath))
        {
            File.Delete(ConfigFilePath);
        }

        var siteId = Guid.Parse("bbbb0000-0000-0000-0000-000000000001");
        var modelId = Guid.Parse("bbbb0000-0000-0000-0000-000000000002");
        var keyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(AccessKey)));

        var snapshot = new
        {
            configVersion = 1726000000000L,
            generatedAt = DateTimeOffset.UtcNow,
            configHash = "",
            sites = new[]
            {
                new { id = siteId, name = "Resume-Site", baseUrl = $"http://127.0.0.1:{upstreamPort}/", endpointPathMode = "standard-root", apiKey = "upstream-key", protocolType = "OpenAI", supportsOpenAi = true, supportsAnthropic = false, supportsResponses = true, isEnabled = true, managedSource = "", clientEmulation = "None", extraHeadersJson = (string?)null, egressProxyUrl = (string?)null }
            },
            models = new[] { new { id = modelId, modelName = "gpt-conserve", displayName = "Conserve", isEnabled = true, overrideReasoningEffort = "", compatibilityProfileId = (Guid?)null, clientEmulation = "None", extraHeadersJson = (string?)null } },
            siteModelMappings = Array.Empty<object>(),
            routeRules = new[]
            {
                new { id = Guid.Parse("bbbb0000-0000-0000-0000-000000000003"), externalModelName = "gpt-conserve", upstreamModelName = "gpt-conserve", siteId = siteId, siteModelName = "gpt-conserve", priority = 0, modelPriority = 0, instancePriority = 0, isEnabled = true, availabilityMode = "Always", timeRangesJson = (string?)null, overrideReasoningEffort = "", compatibilityRules = Array.Empty<object>() }
            },
            accessKeys = new[] { new { id = Guid.Parse("bbbb0000-0000-0000-0000-000000000004"), keyName = "resume", plainKey = AccessKey, accessKeyHash = keyHash, maskedValue = "resume-...", isEnabled = true, allowedRouteNames = "" } },
            headerProfiles = Array.Empty<object>(),
            proxyProfiles = Array.Empty<object>(),
            accountCredentials = Array.Empty<object>(),
            runtimeSettings = new
            {
                proxyRequestTimeoutSeconds = 60,
                proxyStreamIdleTimeoutSeconds = 0,
                proxyRetryCount = 1,
                rateLimitRetryCount = 0,
                detectionRequestTimeoutSeconds = 60,
                detectionRetryCount = 0,
                detectionConcurrency = 1,
                circuitBreakerFailureThreshold = 5,
                circuitBreakerRecoveryMinutes = 2,
                conversationLogEnabled = true,
                developerFeaturesEnabled = true,
                developerTraceEnabled = true,
                developerFailureDumpEnabled = true,
                developerSimulatorEnabled = true,
                developerProtocolDiagnosticsEnabled = true,
                developerSqlMigrationsEnabled = true,
                developerProxyProfilesEnabled = false,
                concurrencyMode = 0,
                concurrencyQueueTimeoutSeconds = 10
            }
        };

        File.WriteAllText(ConfigFilePath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    private static void DeleteLastGoodConfig()
    {
        if (File.Exists(ConfigFilePath))
        {
            File.Delete(ConfigFilePath);
        }
        if (Directory.Exists(ConfigDirectory) && !Directory.EnumerateFileSystemEntries(ConfigDirectory).Any())
        {
            Directory.Delete(ConfigDirectory);
        }
    }
}