using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AITool.Core.IntegrationTests;

/// <summary>
/// 铁律 L1 守恒测试：Core 独立运行能力。
/// 预置 last-good-config.json（无任何 Admin 参与），Core 以生产环境启动后必须能
/// 独立完成 /v1 的完整代理矩阵（非流式/流式/鉴权/失败语义）。任何破坏此守恒的改动都会被拦截。
/// </summary>
[Collection("CoreStandalone")]
public sealed class CoreStandaloneConservationTests
{
    private const string AccessKey = "test-access-key-standalone";

    private static string ConfigDirectory => Path.Combine(AppContext.BaseDirectory, "core-runtime");
    private static string ConfigFilePath => Path.Combine(ConfigDirectory, "last-good-config.json");

    /// <summary>
    /// 无 Admin：快照恢复后，非流式聊天完成请求完整走通（200 + 上游内容）。
    /// </summary>
    [Fact]
    public async Task Non_streaming_chat_completion_succeeds_without_admin()
    {
        using var upstream = new MockOpenAiUpstream();
        WriteLastGoodConfig(upstream.Port);

        try
        {
            await using var factory = new StandaloneCoreFactory();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AccessKey);

            // 快照已恢复：状态接口直接反映 last-good-config 的 ConfigVersion。
            var status = await client.GetFromJsonAsync<JsonElement>("/api/core/config/status");
            status.GetProperty("ready").GetBoolean().Should().BeTrue();
            status.GetProperty("configVersion").GetInt64().Should().BeGreaterThan(0);

            var response = await client.PostAsJsonAsync(
                "/v1/chat/completions",
                new { model = "gpt-conserve", messages = new[] { new { role = "user", content = "hello" } } });

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("mock-completion", "上游内容应被 Core 透传");
        }
        finally
        {
            DeleteLastGoodConfig();
        }
    }

    /// <summary>
    /// 无 Admin：流式请求完整走通，SSE 帧逐个到达且顺序正确。
    /// </summary>
    [Fact]
    public async Task Streaming_chat_completion_succeeds_without_admin()
    {
        using var upstream = new MockOpenAiUpstream(streamByDefault: true);
        WriteLastGoodConfig(upstream.Port);

        try
        {
            await using var factory = new StandaloneCoreFactory();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AccessKey);

            var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = JsonContent.Create(
                    new { model = "gpt-conserve", stream = true, messages = new[] { new { role = "user", content = "hi" } } },
                    options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            }, HttpCompletionOption.ResponseHeadersRead);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

            var raw = await response.Content.ReadAsStringAsync();
            raw.Should().Contain("data: {\"choices\":", "上游 SSE 帧应逐块透传");
            raw.Should().Contain("mock-chunk-1");
            raw.Should().Contain("mock-chunk-2");
            // 帧顺序守恒
            raw.IndexOf("mock-chunk-1").Should().BeLessThan(raw.IndexOf("mock-chunk-2"));
        }
        finally
        {
            DeleteLastGoodConfig();
        }
    }

    /// <summary>
    /// 无 Admin：AccessKey 校验来自快照，错误密钥应 401 invalid_access_key。
    /// </summary>
    [Fact]
    public async Task Invalid_access_key_rejected_without_admin()
    {
        using var upstream = new MockOpenAiUpstream();
        WriteLastGoodConfig(upstream.Port);

        try
        {
            await using var factory = new StandaloneCoreFactory();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong-key");

            var response = await client.PostAsJsonAsync(
                "/v1/chat/completions",
                new { model = "gpt-conserve", messages = new[] { new { role = "user", content = "hi" } } });

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("invalid_access_key");
        }
        finally
        {
            DeleteLastGoodConfig();
        }
    }

    /// <summary>
    /// 无 Admin：路由存在但上游故障时，错误处理语义完整（明确错误响应，不崩溃、不挂起）。
    /// </summary>
    [Fact]
    public async Task Upstream_failure_returns_clean_error_without_admin()
    {
        using var upstream = new MockOpenAiUpstream(failWith500: true);
        WriteLastGoodConfig(upstream.Port);

        try
        {
            await using var factory = new StandaloneCoreFactory();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AccessKey);

            var response = await client.PostAsJsonAsync(
                "/v1/chat/completions",
                new { model = "gpt-conserve", messages = new[] { new { role = "user", content = "hi" } } });

            response.StatusCode.Should().NotBe(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            DeleteLastGoodConfig();
        }
    }

    /// <summary>
    /// 无 Admin：请求不存在的模型应 403 no_available_route（路由解析语义完整）。
    /// </summary>
    [Fact]
    public async Task Unknown_model_returns_no_available_route_without_admin()
    {
        using var upstream = new MockOpenAiUpstream();
        WriteLastGoodConfig(upstream.Port);

        try
        {
            await using var factory = new StandaloneCoreFactory();
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AccessKey);

            var response = await client.PostAsJsonAsync(
                "/v1/chat/completions",
                new { model = "does-not-exist", messages = new[] { new { role = "user", content = "hi" } } });

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("no_available_route");
        }
        finally
        {
            DeleteLastGoodConfig();
        }
    }

    private static void WriteLastGoodConfig(int upstreamPort)
    {
        Directory.CreateDirectory(ConfigDirectory);
        if (File.Exists(ConfigFilePath))
        {
            File.Delete(ConfigFilePath);
        }

        var siteId = Guid.Parse("aaaa0000-0000-0000-0000-000000000001");
        var modelId = Guid.Parse("aaaa0000-0000-0000-0000-000000000002");
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AccessKey)));

        var snapshot = new
        {
            configVersion = 1725000000000L,
            generatedAt = DateTimeOffset.UtcNow,
            configHash = "",
            sites = new[]
            {
                new
                {
                    id = siteId,
                    name = "Conserve-Site",
                    baseUrl = $"http://127.0.0.1:{upstreamPort}/",
                    endpointPathMode = "standard-root",
                    apiKey = "upstream-key",
                    protocolType = "OpenAI",
                    supportsOpenAi = true,
                    supportsAnthropic = false,
                    supportsResponses = true,
                    isEnabled = true,
                    managedSource = "",
                    clientEmulation = "None",
                    extraHeadersJson = (string?)null,
                    egressProxyUrl = (string?)null
                }
            },
            models = new[]
            {
                new { id = modelId, modelName = "gpt-conserve", displayName = "Conserve", isEnabled = true, overrideReasoningEffort = "", compatibilityProfileId = (Guid?)null, clientEmulation = "None", extraHeadersJson = (string?)null }
            },
            siteModelMappings = Array.Empty<object>(),
            routeRules = new[]
            {
                new
                {
                    id = Guid.Parse("aaaa0000-0000-0000-0000-000000000003"),
                    externalModelName = "gpt-conserve",
                    upstreamModelName = "gpt-conserve",
                    siteId = siteId,
                    siteModelName = "gpt-conserve",
                    priority = 0,
                    modelPriority = 0,
                    instancePriority = 0,
                    isEnabled = true,
                    availabilityMode = "Always",
                    timeRangesJson = (string?)null,
                    overrideReasoningEffort = "",
                    compatibilityRules = Array.Empty<object>()
                }
            },
            accessKeys = new[]
            {
                new { id = Guid.Parse("aaaa0000-0000-0000-0000-000000000004"), keyName = "standalone", plainKey = AccessKey, accessKeyHash = keyHash, maskedValue = "test-...-standalone", isEnabled = true, allowedRouteNames = "" }
            },
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

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        File.WriteAllText(ConfigFilePath, json);
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

/// <summary>
/// 本地 mock OpenAI 上游（HttpListener 回环端口）：非流式 JSON / 流式 SSE / 500 故障三种模式。
/// 流式模式支持可控帧数/帧间延迟（供断线续传测试制造确定性慢流）。
/// </summary>
public sealed class MockOpenAiUpstream : IDisposable
{
    private readonly HttpListener _listener;
    private readonly bool _streamByDefault;
    private readonly bool _failWith500;
    private readonly int _frameCount;
    private readonly int _frameDelayMs;
    private readonly CancellationTokenSource _cts = new();
    private int _requestCount;
    private int _responseIndex;
    private string _exceptionLog = "";

    /// <summary>仅对首个流式请求生效：写完 N 帧后强制断开（用于中继断线续传测试）。</summary>
    public int AbortAfterFramesOnFirstRequest { get; init; }

    /// <summary>已受理的 HTTP 请求数（供续传测试确认上游被第二次调用）。</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>最近一次写响应时的异常信息（供续传测试排查）。</summary>
    public string ExceptionLog => _exceptionLog;

    public int Port { get; }

    public MockOpenAiUpstream(bool streamByDefault = false, bool failWith500 = false, int frameCount = 2, int frameDelayMs = 0)
    {
        _streamByDefault = streamByDefault;
        _failWith500 = failWith500;
        _frameCount = frameCount;
        _frameDelayMs = frameDelayMs;

        // 取一个空闲端口后交给 HttpListener（回环前缀免管理员权限）。
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => RespondAsync(ctx));
            Interlocked.Increment(ref _requestCount);
        }
    }

    private async Task RespondAsync(HttpListenerContext ctx)
    {
        try
        {
            if (_failWith500)
            {
                ctx.Response.StatusCode = 500;
                await using (var writer = new StreamWriter(ctx.Response.OutputStream, leaveOpen: false) { AutoFlush = true })
                {
                    await writer.WriteAsync("{\"error\":{\"message\":\"mock upstream 500\"}}");
                }
                ctx.Response.Close();
                return;
            }

            if (_streamByDefault)
            {
                // 首请求中断测试：写完 N 帧后强制断开（模拟上游中途断线，触发中继/续传路径）。
                var currentRequest = Interlocked.Increment(ref _responseIndex);
                if (AbortAfterFramesOnFirstRequest > 0 && currentRequest == 1)
                {
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.SendChunked = true;
                    await using (var writer = new StreamWriter(ctx.Response.OutputStream) { AutoFlush = true })
                    {
                        for (var i = 0; i < Math.Min(AbortAfterFramesOnFirstRequest, Math.Max(1, _frameCount)); i++)
                        {
                            await writer.WriteAsync($"data: {{\"choices\":[{{\"delta\":{{\"content\":\"mock-chunk-{i + 1}\"}}}}]}}\n\n");
                            if (_frameDelayMs > 0)
                            {
                                await Task.Delay(_frameDelayMs);
                            }
                        }
                    }

                    ctx.Response.Abort();
                    Interlocked.CompareExchange(ref _exceptionLog, "aborted-after-frames", "");
                    return;
                }

                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.SendChunked = true;
                await using (var writer = new StreamWriter(ctx.Response.OutputStream) { AutoFlush = true })
                {
                    var c = Math.Max(1, _frameCount);
                    for (var i = 0; i < c; i++)
                    {
                        await writer.WriteAsync($"data: {{\"choices\":[{{\"delta\":{{\"content\":\"mock-chunk-{i + 1}\"}}}}]}}\n\n");
                        if (_frameDelayMs > 0 && i < c - 1)
                        {
                            await Task.Delay(_frameDelayMs);
                        }
                    }

                    await writer.WriteAsync("data: [DONE]\n\n");
                }
            }
            else
            {
                ctx.Response.ContentType = "application/json";
                await using (var writer = new StreamWriter(ctx.Response.OutputStream) { AutoFlush = true })
                {
                    await writer.WriteAsync("{\"id\":\"mock-1\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"mock-completion\"}}]}");
                }
            }
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            // 客户端断连等场景忽略；记录首个异常便于续传测试排查。
            Interlocked.CompareExchange(ref _exceptionLog, $"{ex.GetType().Name}: {ex.Message}", "");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // 已停止则忽略。
        }
        _listener.Close();
    }
}

/// <summary>
/// 生产环境（f?. 无 Testing 豁免）+ 预置 last-good-config 的 Core 工厂。
/// </summary>
public sealed class StandaloneCoreFactory : WebApplicationFactory<AITool.Core.CoreProgramMarker>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("ProxyForwarding:StreamResumeEnabled", "true");
    }
}

/// <summary>
/// 本测试类串行执行（共享固定的 last-good-config 文件路径）。
/// </summary>
[CollectionDefinition("CoreStandalone", DisableParallelization = true)]
public sealed class CoreStandaloneCollectionDefinition;