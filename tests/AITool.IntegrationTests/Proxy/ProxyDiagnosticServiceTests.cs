using System.Text;
using System.Text.Json.Nodes;
using AITool.Application.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.Proxy;

public class ProxyDiagnosticServiceTests : IDisposable
{
    private readonly ProxyDiagnosticService _unitTestService;

    public ProxyDiagnosticServiceTests()
    {
        _unitTestService = new ProxyDiagnosticService(NullLogger<ProxyDiagnosticService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            _unitTestService.ClearAllDumps();
        }
        catch
        {
        }
    }

    [Fact]
    public async Task RecordDiagnostic_failed_request_generates_reproduction_file_and_memory_item()
    {
        var service = _unitTestService;

        var requestId = Guid.NewGuid();
        var traceId = Guid.NewGuid();
        var headers = new HeaderDictionary
        {
            { "Authorization", "Bearer sk-1234567890abcdef" },
            { "User-Agent", "DeepSeekHarness/1.0" }
        };

        var rawClientBody = "{\"model\":\"1M\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}";
        var preparedBody = "{\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"hello\"}]}]}";
        var errorBody = "{\"error\":{\"code\":400,\"message\":\"Request contains an invalid argument.\"}}";

        var context = new ProxyDiagnosticContext
        {
            RequestId = requestId,
            TraceId = traceId,
            ClientProtocol = "OpenAI",
            RequestSource = "deepseek-harness",
            ClientIp = "127.0.0.1",
            UserAgent = "DeepSeekHarness/1.0",
            RequestPath = "/v1/chat/completions",
            RouteName = "1M",
            TargetSiteId = Guid.NewGuid(),
            TargetSiteName = "GeminiPro1",
            TargetBaseUrl = "https://daily-cloudcode-pa.googleapis.com",
            RequestModel = "1M",
            AttemptedModel = "gemini-3.7-flash-high",
            UpstreamProtocol = "Gemini",
            ForwardingMode = "bridge",
            ClientHeaders = ProxyDiagnosticContext.SnapshotHeaders(headers),
            RawClientRequestBody = rawClientBody,
            PreparedRequestBody = preparedBody,
            Result = new ProxyForwardResult
            {
                Success = false,
                StatusCode = 400,
                ErrorMessage = "Request contains an invalid argument.",
                ResponseBody = errorBody,
                IsStreaming = true,
                TotalDurationMs = 1450
            }
        };

        // Act
        service.RecordDiagnostic(context);

        // Wait for async background writer to complete with polling timeout
        var failureDump = await WaitForDumpAsync(service, d => d.Category == "failure" && d.RouteName == "1M", TimeSpan.FromSeconds(3));

        // Assert
        Assert.NotNull(failureDump);
        Assert.Equal("GeminiPro1", failureDump.SiteName);
        Assert.Equal("gemini-3.7-flash-high", failureDump.AttemptedModel);
        Assert.Equal(400, failureDump.StatusCode);
        Assert.False(failureDump.Success);

        // Check read content
        var content = service.ReadDumpContent(failureDump.FileName);
        Assert.NotNull(content);

        var json = JsonNode.Parse(content);
        Assert.NotNull(json);
        Assert.Equal("failed", json["status"]?.ToString());
        Assert.Equal("1M", json["diagnostic"]?["routeName"]?.ToString());
        Assert.Equal("GeminiPro1", json["diagnostic"]?["siteName"]?.ToString());
        Assert.Equal("gemini-3.7-flash-high", json["diagnostic"]?["attemptedModel"]?.ToString());
        Assert.Equal("Gemini", json["diagnostic"]?["upstreamProtocol"]?.ToString());
        Assert.Equal("OpenAI", json["diagnostic"]?["clientProtocol"]?.ToString());
        Assert.Contains("bridge", json["diagnostic"]?["forwardingMode"]?.ToString());

        // Header authorization was masked
        var authHeader = json["clientHeaders"]?["Authorization"]?.ToString();
        Assert.NotNull(authHeader);
        Assert.DoesNotContain("1234567890", authHeader);
        Assert.Contains("...", authHeader);

        // Request and response bodies are captured in full
        Assert.NotNull(json["clientRequestBody"]);
        Assert.NotNull(json["preparedRequestBody"]);
        Assert.NotNull(json["upstreamResponseBody"]);
    }

    [Fact]
    public async Task Success_sampling_is_disabled_by_default_and_can_be_temporarily_enabled_for_at_most_10_minutes()
    {
        var service = _unitTestService;

        // 1. Initial state: disabled
        var initialStatus = service.GetSuccessSamplingStatus();
        Assert.False(initialStatus.Enabled);
        Assert.Equal(0, initialStatus.RemainingSeconds);

        var context = new ProxyDiagnosticContext
        {
            RequestId = Guid.NewGuid(),
            ClientProtocol = "OpenAI",
            RequestPath = "/v1/chat/completions",
            RouteName = "gemini-3.7",
            TargetSiteName = "GeminiMain",
            RequestModel = "gemini-3.7",
            AttemptedModel = "gemini-3.7-flash",
            UpstreamProtocol = "Gemini",
            ForwardingMode = "bridge",
            RawClientRequestBody = "{\"model\":\"gemini-3.7\",\"messages\":[]}",
            PreparedRequestBody = "{\"contents\":[]}",
            Result = new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = "{\"candidates\":[]}",
                TotalDurationMs = 500
            }
        };

        // 2. Act: Record while disabled -> should NOT record sample
        service.RecordDiagnostic(context);
        await Task.Delay(100);

        var dumpsWhileDisabled = service.ListRecentDumps(10);
        Assert.DoesNotContain(dumpsWhileDisabled, d => d.Category == "sample" && d.RouteName == "gemini-3.7");

        // 3. Act: Enable sampling for 10 minutes
        var enabledStatus = service.EnableSuccessSampling(15); // should be clamped to 10 min
        Assert.True(enabledStatus.Enabled);
        Assert.InRange(enabledStatus.RemainingSeconds, 590, 600);

        // 4. Act: Record while enabled -> should record sample
        service.RecordDiagnostic(context);
        var sampleDump = await WaitForDumpAsync(service, d => d.Category == "sample" && d.RouteName == "gemini-3.7", TimeSpan.FromSeconds(3));
        Assert.NotNull(sampleDump);
        Assert.True(sampleDump.Success);

        // 5. Act: Disable sampling
        var disabledStatus = service.DisableSuccessSampling();
        Assert.False(disabledStatus.Enabled);
        Assert.Equal(0, disabledStatus.RemainingSeconds);

        // 6. Act: Test prune and clear
        var cleared = service.ClearAllDumps();
        Assert.True(cleared >= 0);
    }

    [Fact]
    public async Task EndToEnd_failing_and_successful_proxy_requests_automatically_record_diagnostics_and_samples()
    {
        var fakeForwardService = new FakeProxyForwardServiceForDiagnostics { ReturnSuccess = false };
        await using var factory = new DiagnosticTestWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var diagnosticService = factory.Services.GetRequiredService<IProxyDiagnosticService>();

        try
        {
            // 1. Act: Send failing request -> automatically dumped even if sampling is disabled
            var failRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = new StringContent("{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"will fail\"}]}", Encoding.UTF8, "application/json")
            };
            failRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "openai-cross-key");

            var failResponse = await client.SendAsync(failRequest);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, failResponse.StatusCode);

            // Wait for background async file write
            var failItem = await WaitForDumpAsync(diagnosticService, x => x.Category == "failure" && x.RouteName == "auto", TimeSpan.FromSeconds(3));
            Assert.NotNull(failItem);
            Assert.Equal("Anthropic Only Site", failItem.SiteName);
            Assert.Equal(400, failItem.StatusCode);
            Assert.False(failItem.Success);

            // 2. Act: Enable sampling for successful requests
            diagnosticService.EnableSuccessSampling(10);
            fakeForwardService.ReturnSuccess = true;

            var okRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = new StringContent("{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"will succeed\"}]}", Encoding.UTF8, "application/json")
            };
            okRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "openai-cross-key");

            var okResponse = await client.SendAsync(okRequest);
            Assert.Equal(System.Net.HttpStatusCode.OK, okResponse.StatusCode);

            // Wait for background async file write
            var successItem = await WaitForDumpAsync(diagnosticService, x => x.Category == "sample" && x.RouteName == "auto", TimeSpan.FromSeconds(3));
            Assert.NotNull(successItem);
            Assert.Equal("Anthropic Only Site", successItem.SiteName);
            Assert.True(successItem.Success);

            // 3. Verify dump content retrieval
            var dumpJson = diagnosticService.ReadDumpContent(failItem.FileName);
            Assert.NotNull(dumpJson);
            Assert.Contains("Mock invalid parameter", dumpJson);
            Assert.Contains("will fail", dumpJson);
        }
        finally
        {
            diagnosticService.ClearAllDumps();
        }
    }

    private static async Task<ProxyDiagnosticDumpItem?> WaitForDumpAsync(
        IProxyDiagnosticService service,
        Func<ProxyDiagnosticDumpItem, bool> predicate,
        TimeSpan timeout)
    {
        var start = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - start < timeout)
        {
            var item = service.ListRecentDumps(50).FirstOrDefault(predicate);
            if (item != null) return item;
            await Task.Delay(30);
        }
        return service.ListRecentDumps(50).FirstOrDefault(predicate);
    }

    [Fact]
    public async Task EndToEnd_failure_dumps_are_skipped_when_developer_features_disabled()
    {
        // 开发者功能关闭时（与查看转储的管理端点门控对齐），失败请求不落盘、不进内存清单。
        var fakeForwardService = new FakeProxyForwardServiceForDiagnostics { ReturnSuccess = false };
        await using var factory = new DiagnosticTestWebApplicationFactory(fakeForwardService, developerFeaturesEnabled: false);
        using var client = factory.CreateClient();

        var diagnosticService = factory.Services.GetRequiredService<IProxyDiagnosticService>();

        var failRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"dev off\"}]}", Encoding.UTF8, "application/json")
        };
        failRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "openai-cross-key");

        var failResponse = await client.SendAsync(failRequest);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, failResponse.StatusCode);

        // 轮询一小段时间确认没有异步落盘（避免"尚未写完"的假阴性）。
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            Assert.Empty(diagnosticService.ListRecentDumps(50));
            await Task.Delay(100);
        }
    }

    private sealed class FakeProxyForwardServiceForDiagnostics : IProxyForwardService
    {
        public bool ReturnSuccess { get; set; } = true;

        public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
        {
            if (ReturnSuccess)
            {
                return Task.FromResult(new ProxyForwardResult
                {
                    Success = true,
                    StatusCode = 200,
                    ResponseBody = "{\"content\":[{\"type\":\"text\",\"text\":\"success-content\"}],\"usage\":{\"input_tokens\":5,\"cache_read_input_tokens\":0,\"output_tokens\":5}}",
                    InputTokens = 5,
                    OutputTokens = 5
                });
            }

            return Task.FromResult(new ProxyForwardResult
            {
                Success = false,
                StatusCode = 400,
                ErrorMessage = "Mock invalid parameter in upstream model",
                ResponseBody = "{\"error\":{\"message\":\"Mock invalid parameter in upstream model\"}}"
            });
        }

        public Task<ProxyForwardResult> ForwardStreamingAsync(ProxyForwardRequest request, Func<string, CancellationToken, Task> onSseDataAsync, CancellationToken cancellationToken = default)
        {
            return ForwardAsync(request, cancellationToken);
        }

        public Task<ProxyForwardResult> ForwardStreamingWithStateAsync(ProxyForwardRequest request, Func<string, CancellationToken, Task> onSseDataAsync, Func<ProxyForwardResult, Task>? onStreamStartedAsync = null, CancellationToken cancellationToken = default)
        {
            return ForwardAsync(request, cancellationToken);
        }
    }

    private sealed class DiagnosticTestWebApplicationFactory : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-diag-{Guid.NewGuid():N}.db");
        private readonly IProxyForwardService _fakeForwardService;
        private readonly bool _developerFeaturesEnabled;

        public DiagnosticTestWebApplicationFactory(IProxyForwardService fakeForwardService, bool developerFeaturesEnabled = true)
        {
            _fakeForwardService = fakeForwardService;
            _developerFeaturesEnabled = developerFeaturesEnabled;
        }

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);
                Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.RemoveAll<IProxyForwardService>(services);
                services.AddSingleton<IProxyForwardService>(_fakeForwardService);
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
            var db = scope.ServiceProvider.GetRequiredService<AITool.Infrastructure.Persistence.AppDbContext>();
            AITool.Infrastructure.Persistence.SqlSugarSetup.InitializeDatabase(db.Client);

            var siteId = Guid.Parse("12121212-1212-1212-1212-121212121212");
            var accessKeyRaw = "openai-cross-key";
            var accessKeyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(accessKeyRaw)));

            db.Sites.Add(new AITool.Domain.Sites.Site
            {
                Id = siteId,
                Name = "Anthropic Only Site",
                BaseUrl = "https://anthropic-only.example.com",
                ApiKey = "anthropic-only-key",
                ProtocolType = "Anthropic",
                SupportsAnthropic = true,
                IsEnabled = true
            });

            db.ProxyAccessKeys.Add(new AITool.Domain.Proxy.ProxyAccessKey
            {
                Id = Guid.Parse("34343434-3434-3434-3434-343434343434"),
                KeyName = "openai-cross",
                PlainKey = accessKeyRaw,
                AccessKeyHash = accessKeyHash,
                MaskedValue = "sk-***cross",
                IsEnabled = true
            });

            db.ProxyRouteRules.Add(new AITool.Domain.Proxy.ProxyRouteRule
            {
                Id = Guid.Parse("56565656-5656-5656-5656-565656565656"),
                ExternalModelName = "auto",
                UpstreamModelName = "claude-3-7-sonnet",
                SiteId = siteId,
                SiteModelName = "claude-3-7-sonnet-real",
                IsEnabled = true
            });

            db.Client.Deleteable<AITool.Domain.Operations.SystemRuntimeSettings>().Where(x => x.Id == 1).ExecuteCommand();
            db.SystemRuntimeSettings.Add(new AITool.Domain.Operations.SystemRuntimeSettings
            {
                Id = 1,
                ProxyRequestTimeoutSeconds = 30,
                ProxyRetryCount = 0,
                DeveloperFeaturesEnabled = _developerFeaturesEnabled
            });
        }
    }
}
