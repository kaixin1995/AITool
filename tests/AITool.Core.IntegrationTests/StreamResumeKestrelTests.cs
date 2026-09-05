using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AITool.Core.IntegrationTests;

/// <summary>
/// 流式断线续传端到端测试（真实 Kestrel 进程，非 TestServer）：
/// TestHost 对「中途遗弃的流式响应」有已知缓冲缺陷，无法承载断线重连场景。
/// 以 dotnet exec 启动真实 Core 宿主 + 真实 HTTP 往返验证整套协议：
/// 首连接读 2 帧后取消 → 第二连接带 X-Stream-Resume 重入 → 重放缓冲帧先于新实时流。
/// </summary>
[Collection("CoreStandalone")]
public sealed class StreamResumeKestrelTests
{
    private const string AccessKey = "test-access-key-standalone";
    private const string ResumeHeader = "X-Stream-Resume";
    private const string RequestIdHeader = "X-Stream-Request-Id";

    [Fact]
    public async Task Resume_replays_buffered_frames_before_fresh_stream_on_real_kestrel()
    {
        // 慢流：8 帧 × 150ms。
        using var upstream = new MockOpenAiUpstream(streamByDefault: true, frameCount: 8, frameDelayMs: 150);
        var coreProcess = await CoreProcessLauncher.StartAsync(upstream.Port);

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{coreProcess.Port}/"), Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessKey);

            // ---- 第一连接：读 2 帧后中断 ----
            string? resumeId;
            string firstBody;
            using (var firstCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                var response = await client.SendAsync(
                    new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
                    {
                        Content = JsonContent.Create(new { model = "gpt-conserve", stream = true, messages = new[] { new { role = "user", content = "hi" } } })
                    },
                    HttpCompletionOption.ResponseHeadersRead,
                    firstCts.Token);

                response.StatusCode.Should().Be(HttpStatusCode.OK);
                resumeId = response.Headers.TryGetValues(RequestIdHeader, out var ids) ? ids.FirstOrDefault() : null;
                resumeId.Should().NotBeNullOrEmpty();

                await using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream);
                var collected = 0;
                firstBody = "";
                while (collected < 2 && firstCts.Token.IsCancellationRequested == false)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null)
                    {
                        break;
                    }

                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        collected++;
                        firstBody += line + "\n";
                    }
                }

                firstCts.Cancel();
            }

            firstBody.Should().Contain("mock-chunk-1");
            firstBody.Should().Contain("mock-chunk-2");

            // 等 Core 完成取消传播与缓冲安定。
            await Task.Delay(TimeSpan.FromMilliseconds(500));

            // ---- 第二连接：断线续传 ----
            var resumedResponse = await client.SendAsync(
                new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
                {
                    Headers = { { ResumeHeader, $"{resumeId}:2" } },
                    Content = JsonContent.Create(new { model = "gpt-conserve", stream = true, messages = new[] { new { role = "user", content = "hi" } } })
                },
                HttpCompletionOption.ResponseHeadersRead);

            resumedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var resumedBody = ReadAllLinesRobust(await resumedResponse.Content.ReadAsStreamAsync());

            // 续传后 Core 应发起第二次上游请求（mock 计数佐证实时流确实存在）。
            if (upstream.RequestCount < 2)
            {
                // 探针：第三次普通流请求，区分「mock 上游已死」与「Core 不再调上游」。
                await Task.Delay(TimeSpan.FromMilliseconds(300));
                try
                {
                    var probeResponse = await client.SendAsync(
                        new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
                        {
                            Content = JsonContent.Create(new { model = "gpt-conserve", stream = true, messages = new[] { new { role = "user", content = "probe" } } })
                        },
                        HttpCompletionOption.ResponseHeadersRead);
                    _ = probeResponse.StatusCode;
                    await Task.Delay(TimeSpan.FromMilliseconds(250));
                }
                catch
                {
                    // 探针失败不影响主断言，仅用于计数观察。
                }
            }

            upstream.RequestCount.Should().BeGreaterThanOrEqualTo(2,
                $"续传后的新实时流应触发新的上游请求（mock 异常:{upstream.ExceptionLog}；Core stderr:{Shorten(coreProcess.StderrTail)}）");

            // 重放段（chunk-3 起）必须先于新实时流（chunk-1 重新出现）发出。
            var firstChunk3 = resumedBody.IndexOf("mock-chunk-3", StringComparison.Ordinal);
            var firstChunk1 = resumedBody.IndexOf("mock-chunk-1", StringComparison.Ordinal);
            firstChunk3.Should().BeGreaterThanOrEqualTo(0, $"重放段应包含断点后的帧（上游异常:{upstream.ExceptionLog}）");
            firstChunk3.Should().BeLessThan(firstChunk1,
                $"重放缓冲帧必须先于新实时流发出（收到内容视口:{Shorten(resumedBody)}）");

            resumedBody.Should().Contain("[DONE]");
            resumedBody.Should().Contain("mock-chunk-8");
        }
        finally
        {
            coreProcess.Dispose();
        }
    }

    private static string Shorten(string s)
        => s.Length <= 220 ? s : s[..220] + "...";

    private static string ReadAllLinesRobust(Stream stream)
    {
        var sb = new StringBuilder();
        using var reader = new StreamReader(stream);
        while (true)
        {
            try
            {
                var line = reader.ReadLine();
                if (line is null)
                {
                    break;
                }

                sb.AppendLine(line);
            }
            catch (Exception ex)
            {
                sb.AppendLine($"<<读取中断:{ex.GetType().Name}>>");
                break;
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// 启动真实 Core 宿主进程（dotnet exec bin 输出 dll），返回可用端口与进程句柄。
/// </summary>
public sealed class CoreProcessLauncher : IDisposable
{
    private const string ConfigAccessKey = "test-access-key-standalone";

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

    private CoreProcessLauncher(Process process, int port)
    {
        _process = process;
        Port = port;
    }

    public static async Task<CoreProcessLauncher> StartAsync(int upstreamPort)
    {
        var port = FreePort();
        var dll = Path.Combine(AppContext.BaseDirectory, "AITool.Core.dll");
        File.Exists(dll).Should().BeTrue("Core dll 应在测试输出目录");

        // 预置 last-good-config。
        var configDir = Path.Combine(AppContext.BaseDirectory, "core-runtime");
        Directory.CreateDirectory(configDir);
        var configFile = Path.Combine(configDir, "last-good-config.json");
        if (File.Exists(configFile))
        {
            File.Delete(configFile);
        }

        var siteId = Guid.Parse("cccc0000-0000-0000-0000-000000000001");
        var modelId = Guid.Parse("cccc0000-0000-0000-0000-000000000002");
        var keyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(ConfigAccessKey)));
        var snapshot = new
        {
            configVersion = 1727000000000L,
            generatedAt = DateTimeOffset.UtcNow,
            configHash = "",
            sites = new[]
            {
                new { id = siteId, name = "Kestrel-Site", baseUrl = $"http://127.0.0.1:{upstreamPort}/", endpointPathMode = "standard-root", apiKey = "upstream-key", protocolType = "OpenAI", supportsOpenAi = true, supportsAnthropic = false, supportsResponses = true, isEnabled = true, managedSource = "", clientEmulation = "None", extraHeadersJson = (string?)null, egressProxyUrl = (string?)null }
            },
            models = new[] { new { id = modelId, modelName = "gpt-conserve", displayName = "Conserve", isEnabled = true, overrideReasoningEffort = "", compatibilityProfileId = (Guid?)null, clientEmulation = "None", extraHeadersJson = (string?)null } },
            siteModelMappings = Array.Empty<object>(),
            routeRules = new[]
            {
                new { id = Guid.Parse("cccc0000-0000-0000-0000-000000000003"), externalModelName = "gpt-conserve", upstreamModelName = "gpt-conserve", siteId = siteId, siteModelName = "gpt-conserve", priority = 0, modelPriority = 0, instancePriority = 0, isEnabled = true, availabilityMode = "Always", timeRangesJson = (string?)null, overrideReasoningEffort = "", compatibilityRules = Array.Empty<object>() }
            },
            accessKeys = new[] { new { id = Guid.Parse("cccc0000-0000-0000-0000-000000000004"), keyName = "kestrel", plainKey = ConfigAccessKey, accessKeyHash = keyHash, maskedValue = "kestrel-...", isEnabled = true, allowedRouteNames = "" } },
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
        var launcher = new CoreProcessLauncher(process, port);

        // 持续排空输出管道，防止缓冲区写满阻塞 Core（stderr 保留尾部供排查）。
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
            // 等待 /health 就绪。
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

            throw new TimeoutException("Core 进程未在预期时间内就绪");
        }
        catch
        {
            launcher.Dispose();
            throw;
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