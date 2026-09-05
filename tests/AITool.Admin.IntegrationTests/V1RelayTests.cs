using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace AITool.Admin.IntegrationTests;

internal static class RelayTestConstants
{
    public const string AccessKey = "relay-test-access-key";
    public const string AccessKeyName = AccessKey;
}

internal static class RelayTestHelpers
{
    public static string Shorten(string s)
        => s.Length <= 300 ? s : s[..300] + "...";

    public static async Task<string> Probe5029Async()
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            var res = await probe.GetAsync("http://127.0.0.1:5029/health");
            return $"{(int)res.StatusCode}";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }
}

/// <summary>
    /// Admin /v1 中继端点测试（真实 Core 进程 + TestHost Admin）：
    /// 默认关闭 404 / AccessKey 校验 / 非流式透传 / 流式断线自动续传（Core→上游中断后重连补齐）。
    /// </summary>
    [Collection("RelaySerialized")]
    public sealed class V1RelayTests
    {

    [Fact]
    public async Task Relay_disabled_returns_404()
    {
        await using var factory = new RelayWebApplicationFactory(relayEnabled: false);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RelayTestConstants.AccessKey);

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "gpt-conserve", messages = new[] { new { role = "user", content = "hi" } } });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Relay_rejects_invalid_access_key()
    {
        using var upstream = new RelayMockUpstream();
        await using var factory = new RelayWebApplicationFactory(relayEnabled: true, upstreamPort: upstream.Port);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-key");

        var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "gpt-conserve" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("invalid_access_key");
    }

    [Fact]
    public async Task Relay_passes_through_non_streaming_response()
    {
        using var upstream = new RelayMockUpstream();
        var core = await RelayCoreProcess.StartAsync(upstream.Port);
        try
        {
            await using var factory = new RelayWebApplicationFactory(relayEnabled: true, coreBaseUrl: $"http://127.0.0.1:{core.Port}/");
            // 诊断：中继客户端实际指向。
            var relayTarget = "unknown";
            var directViaRelayClient = "n/a";
            using (var scope = factory.Services.CreateScope())
            {
                var relayClient = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>()
                    .CreateClient("RelayCoreClient");
                relayTarget = relayClient.BaseAddress?.ToString() ?? "null";
                relayClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RelayTestConstants.AccessKey);
                var probe = await relayClient.PostAsJsonAsync("/v1/chat/completions", new { model = "gpt-conserve", stream = false, messages = new[] { new { role = "user", content = "hi" } } });
                directViaRelayClient = $"{(int)probe.StatusCode}:" + (await probe.Content.ReadAsStringAsync());
            }

            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RelayTestConstants.AccessKey);

            var response = await client.PostAsJsonAsync("/v1/chat/completions", new { model = "gpt-conserve", messages = new[] { new { role = "user", content = "hi" } } });

            response.StatusCode.Should().Be(HttpStatusCode.OK, "中继目标:" + relayTarget + "；RelayCoreClient 直发:" + directViaRelayClient + "；响应体:" + await response.Content.ReadAsStringAsync());
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("mock-completion");
        }
        finally
        {
            core.Dispose();
        }
    }

    /// <summary>
    /// 流式断线续传：Core→上游在首请求第 3 帧后强制断开 → 中继自动带 X-Stream-Resume 重连 →
    /// 客户端收到「重放段 + 新流」且仅一个 [DONE]。
    /// </summary>
    [Fact]
    public async Task Relay_recovers_stream_when_upstream_connection_breaks()
    {
        using var upstream = new RelayMockUpstream(abortAfterFramesOnFirstRequest: 3, frameCount: 8, frameDelayMs: 60);
        var core = await RelayCoreProcess.StartAsync(upstream.Port);
        try
        {
            // 健康检查：Core 直连应正常（排除中继层干扰）。
            using (var direct = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{core.Port}/"), Timeout = TimeSpan.FromSeconds(10) })
            {
                direct.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RelayTestConstants.AccessKey);
                var directResponse = await direct.PostAsJsonAsync("/v1/chat/completions", new { model = "gpt-conserve", messages = new[] { new { role = "user", content = "hi" } } });
                directResponse.StatusCode.Should().Be(HttpStatusCode.OK, "Core 直连应 200：" + await directResponse.Content.ReadAsStringAsync());
            }

            await using var factory = new RelayWebApplicationFactory(relayEnabled: true, coreBaseUrl: $"http://127.0.0.1:{core.Port}/");
            using var client = factory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", RelayTestConstants.AccessKey);

            var response = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
                {
                    Content = JsonContent.Create(new { model = "gpt-conserve", stream = true, messages = new[] { new { role = "user", content = "hi" } } })
                },
                HttpCompletionOption.ResponseHeadersRead);

            response.StatusCode.Should().Be(HttpStatusCode.OK, "转发错误响应体:" + await response.Content.ReadAsStringAsync() + "；Core stderr 尾:" + RelayTestHelpers.Shorten(core.StderrTail) + "；mock 收到路径:" + string.Join(",", upstream.Paths) + "；本机5029存活:" + await RelayTestHelpers.Probe5029Async());
            var body = await response.Content.ReadAsStringAsync();

            // 第一流的前 3 帧已到达。
            body.Should().Contain("mock-chunk-1");
            body.Should().Contain("mock-chunk-3");
            // 重放段补齐断点后的帧（chunk-4 在这次行程中一定出现——首流或重放）。
            body.Should().Contain("mock-chunk-4");
            // 新流完整 + 仅一个 [DONE]（截断语义）。
            body.IndexOf("data: [DONE]", StringComparison.Ordinal).Should().BeGreaterThan(0);
            body.LastIndexOf("data: [DONE]", StringComparison.Ordinal).Should().Be(body.IndexOf("data: [DONE]", StringComparison.Ordinal));
            body.Should().Contain("mock-chunk-8");
        }
        finally
        {
            core.Dispose();
        }
    }
}

/// <summary>
/// Admin 测试宿主：可开关中继、指定 Core 地址、隔离 SQLite 并预置访问密钥。
/// </summary>
internal sealed class RelayWebApplicationFactory : WebApplicationFactory<AITool.Admin.AdminProgramMarker>
{
    private readonly bool _relayEnabled;
    private readonly int _upstreamPort;
    private readonly bool _useUpstreamPort;
    private readonly string _coreBaseUrl;
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-relay-{Guid.NewGuid():N}.db");

    public RelayWebApplicationFactory(bool relayEnabled, int upstreamPort = 0, string coreBaseUrl = "")
    {
        _relayEnabled = relayEnabled;
        _upstreamPort = upstreamPort;
        _useUpstreamPort = upstreamPort > 0;
        _coreBaseUrl = coreBaseUrl;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Relay:Enabled", _relayEnabled ? "true" : "false");
        if (!string.IsNullOrEmpty(_coreBaseUrl))
        {
            builder.UseSetting("CoreServer:BaseUrl", _coreBaseUrl);
        }

        builder.ConfigureServices(services =>
        {
            IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        SeedAsync().GetAwaiter().GetResult();
    }

    private async Task SeedAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SqlSugarSetup.InitializeDatabase(db.Client);

        if (_useUpstreamPort)
        {
            db.Sites.AddRange(
                new Domain.Sites.Site
                {
                    Id = Guid.Parse("dddd0000-0000-0000-0000-000000000001"),
                    Name = "Relay-Site",
                    BaseUrl = $"http://127.0.0.1:{_upstreamPort}/",
                    EndpointPathMode = "standard-root",
                    ApiKey = "upstream-key",
                    ProtocolType = "OpenAI",
                    SupportsOpenAi = true,
                    SupportsAnthropic = false,
                    IsEnabled = true
                },
                new Domain.Sites.Site
                {
                    Id = Guid.Parse("dddd0000-0000-0000-0000-000000000002"),
                    Name = "Relay-Site-2",
                    BaseUrl = $"http://127.0.0.1:{_upstreamPort}/",
                    EndpointPathMode = "standard-root",
                    ApiKey = "upstream-key-2",
                    ProtocolType = "OpenAI",
                    SupportsOpenAi = true,
                    SupportsAnthropic = false,
                    IsEnabled = true
                });
            db.ModelLibraryItems.AddRange(new Domain.Models.ModelLibraryItem
            {
                Id = Guid.Parse("dddd0000-0000-0000-0000-000000000003"),
                ModelName = "gpt-conserve",
                DisplayName = "Relay",
                IsEnabled = true
            });
            db.ProxyRouteRules.AddRange(new Domain.Proxy.ProxyRouteRule
            {
                Id = Guid.Parse("dddd0000-0000-0000-0000-000000000004"),
                ExternalModelName = "gpt-conserve",
                UpstreamModelName = "gpt-conserve",
                SiteId = Guid.Parse("dddd0000-0000-0000-0000-000000000001"),
                SiteModelName = "gpt-conserve",
                IsEnabled = true,
                ModelPriority = 0,
                InstancePriority = 0,
                Priority = 0
            });
            db.SiteModelMappings.AddRange(new Domain.SiteCatalog.SiteModelMapping
            {
                Id = Guid.Parse("dddd0000-0000-0000-0000-000000000005"),
                SiteId = Guid.Parse("dddd0000-0000-0000-0000-000000000001"),
                ModelLibraryItemId = Guid.Parse("dddd0000-0000-0000-0000-000000000003"),
                RemoteModelName = "gpt-conserve",
                IsEnabled = true,
                MaxConcurrency = 0
            });
        }

        db.ProxyAccessKeys.AddRange(new ProxyAccessKey
        {
            Id = Guid.Parse("dddd0000-0000-0000-0000-000000000006"),
            KeyName = "relay-key",
            PlainKey = RelayTestConstants.AccessKey,
            AccessKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RelayTestConstants.AccessKey))),
            MaskedValue = "relay-...-key",
            IsEnabled = true
        });
    }
}

/// <summary>
/// 中继测试专用 mock 上游：非流式 JSON / 流式 SSE（首请求可强制断流，触发中继续传）。
/// </summary>
internal sealed class RelayMockUpstream : IDisposable
{
    private readonly HttpListener _listener;
    private readonly int _abortAfterFrames;
    private readonly int _frameCount;
    private readonly int _frameDelayMs;
    private readonly CancellationTokenSource _cts = new();
    private int _responseIndex;

    /// <summary>已受理请求的路径（诊断用）。</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> Paths { get; } = new();

    public int Port { get; }

    public RelayMockUpstream(bool streamByDefault = false, int abortAfterFramesOnFirstRequest = 0, int frameCount = 2, int frameDelayMs = 0)
    {
        _abortAfterFrames = abortAfterFramesOnFirstRequest;
        _frameCount = frameCount;
        _frameDelayMs = frameDelayMs;

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
            _ = Interlocked.Increment(ref _responseIndex);
            Paths.Enqueue(ctx.Request.Url.AbsolutePath + ctx.Request.Url.Query);
        }
    }

    private async Task RespondAsync(HttpListenerContext ctx)
    {
        try
        {
            var isStreaming = Interlocked.Increment(ref _responseIndex) >= 1; // 除 500 外全部流式/JSON 由帧逻辑决定
            _ = isStreaming;
            try
            {
                var requestBody = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                if (requestBody.Contains("\"stream\":true", StringComparison.Ordinal) || requestBody.Contains("\"stream\": true", StringComparison.Ordinal))
                {
                    // 流式：首请求可中途断开。
                    if (_abortAfterFrames > 0 && _responseIndex == 1)
                    {
                        ctx.Response.ContentType = "text/event-stream";
                        ctx.Response.SendChunked = true;
                        await using (var writer = new StreamWriter(ctx.Response.OutputStream) { AutoFlush = true })
                        {
                            for (var i = 0; i < Math.Min(_abortAfterFrames, Math.Max(1, _frameCount)); i++)
                            {
                                await writer.WriteAsync($"data: {{\"choices\":[{{\"delta\":{{\"content\":\"mock-chunk-{i + 1}\"}}}}]}}\n\n");
                                if (_frameDelayMs > 0)
                                {
                                    await Task.Delay(_frameDelayMs);
                                }
                            }
                        }

                        ctx.Response.Abort();
                        return;
                    }

                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.SendChunked = true;
                    await using (var writer = new StreamWriter(ctx.Response.OutputStream) { AutoFlush = true })
                    {
                        for (var i = 0; i < _frameCount; i++)
                        {
                            await writer.WriteAsync($"data: {{\"choices\":[{{\"delta\":{{\"content\":\"mock-chunk-{i + 1}\"}}}}]}}\n\n");
                            if (_frameDelayMs > 0 && i < _frameCount - 1)
                            {
                                await Task.Delay(_frameDelayMs);
                            }
                        }

                        await writer.WriteAsync("data: [DONE]\n\n");
                    }

                    ctx.Response.Close();
                    return;
                }
            }
            catch
            {
                // 读体失败：按非流式处理。
            }

            ctx.Response.ContentType = "application/json";
            await using (var writer = new StreamWriter(ctx.Response.OutputStream) { AutoFlush = true })
            {
                await writer.WriteAsync("{\"id\":\"mock-1\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"mock-completion\"}}]}");
            }

            ctx.Response.Close();
        }
        catch
        {
            // 客户端断连等场景忽略。
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
/// 启动真实 Core 进程（dotnet exec 测试输出 dll），预置与守恒测试同构的 last-good-config。
/// </summary>
internal sealed class RelayCoreProcess : IDisposable
{
    private const string ConfigAccessKey = RelayTestConstants.AccessKeyName;
    private readonly Process _process;
    private readonly StringBuilder _stderrTail = new();

    /// <summary>Core 进程 stderr 尾部（诊断用）。</summary>
    public string StderrTail
    {
        get
        {
            lock (_stderrTail)
            {
                return _stderrTail.ToString();
            }
        }
    }

    public int Port { get; }

    private RelayCoreProcess(Process process, int port)
    {
        _process = process;
        Port = port;
    }

    public static async Task<RelayCoreProcess> StartAsync(int upstreamPort)
    {
        var port = FreePort();
        var dll = Path.Combine(AppContext.BaseDirectory, "AITool.Core.dll");
        File.Exists(dll).Should().BeTrue();

        var configDir = Path.Combine(AppContext.BaseDirectory, "core-runtime");
        Directory.CreateDirectory(configDir);
        var configFile = Path.Combine(configDir, "last-good-config.json");
        if (File.Exists(configFile))
        {
            File.Delete(configFile);
        }

        var siteId = Guid.Parse("eeee0000-0000-0000-0000-000000000001");
        var modelId = Guid.Parse("eeee0000-0000-0000-0000-000000000002");
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ConfigAccessKey)));
        var snapshot = new
        {
            configVersion = 1728000000000L,
            generatedAt = DateTimeOffset.UtcNow,
            configHash = "",
            sites = new[]
            {
                new { id = siteId, name = "Relay-Core-Site", baseUrl = $"http://127.0.0.1:{upstreamPort}/", endpointPathMode = "standard-root", apiKey = "upstream-key", protocolType = "OpenAI", supportsOpenAi = true, supportsAnthropic = false, supportsResponses = true, isEnabled = true, managedSource = "", clientEmulation = "None", extraHeadersJson = (string?)null, egressProxyUrl = (string?)null }
            },
            models = new[] { new { id = modelId, modelName = "gpt-conserve", displayName = "Conserve", isEnabled = true, overrideReasoningEffort = "", compatibilityProfileId = (Guid?)null, clientEmulation = "None", extraHeadersJson = (string?)null } },
            siteModelMappings = Array.Empty<object>(),
            routeRules = new[]
            {
                new { id = Guid.Parse("eeee0000-0000-0000-0000-000000000003"), externalModelName = "gpt-conserve", upstreamModelName = "gpt-conserve", siteId = siteId, siteModelName = "gpt-conserve", priority = 0, modelPriority = 0, instancePriority = 0, isEnabled = true, availabilityMode = "Always", timeRangesJson = (string?)null, overrideReasoningEffort = "", compatibilityRules = Array.Empty<object>() }
            },
            accessKeys = new[] { new { id = Guid.Parse("eeee0000-0000-0000-0000-000000000004"), keyName = "relay", plainKey = ConfigAccessKey, accessKeyHash = keyHash, maskedValue = "relay-...", isEnabled = true, allowedRouteNames = "" } },
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
        File.WriteAllText(configFile, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"exec \"{dll}\" --CoreServer:Port={port}",
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        var process = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 Core 进程");
        var launcher = new RelayCoreProcess(process, port);
        _ = Task.Run(() => process.StandardOutput.ReadToEndAsync());
        _ = Task.Run(async () =>
        {
            var line = await process.StandardError.ReadLineAsync();
            while (line is not null)
            {
                lock (launcher._stderrTail)
                {
                    if (launcher._stderrTail.Length > 8000)
                    {
                        launcher._stderrTail.Remove(0, 4000);
                    }

                    launcher._stderrTail.AppendLine(line);
                }

                line = await process.StandardError.ReadLineAsync();
            }
        });

        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    var res = await probe.GetAsync($"http://127.0.0.1:{port}/health");
                    if (res.IsSuccessStatusCode)
                    {
                        return launcher;
                    }
                }
                catch
                {
                    // 未就绪，重试。
                }

                await Task.Delay(200);
            }

            throw new TimeoutException("Core 进程未就绪");
        }
        catch
        {
            launcher.Dispose();
            throw;
        }
    }

    private static async Task<string> Probe5029Async()
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            var res = await probe.GetAsync("http://127.0.0.1:5029/health");
            return $"{(int)res.StatusCode}";
        }
        catch (Exception ex)
        {
            return ex.GetType().Name;
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch
        {
            // 已退出则忽略。
        }

        _process.Dispose();
    }
}
/// <summary>
/// 进程型测试（真实 Core 子进程 + 本地端口）串行执行：消除端口探测释放后的并行抢占竞态。
/// </summary>
[CollectionDefinition("RelaySerialized", DisableParallelization = true)]
public sealed class RelaySerializedCollectionDefinition;
