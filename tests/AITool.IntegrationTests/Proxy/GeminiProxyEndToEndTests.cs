using System.Net;
using System.Security.Cryptography;
using System.Text;
using AITool.Domain.Google;
using AITool.Domain.Models;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.Proxy;

/// <summary>
/// Gemini 站点端到端代理链路验证：真实 OpenAiProxyController + 真实 ProxyForwardService + mock HTTP。
/// 覆盖控制器层接线——协议解析（OpenAI 客户端 → Gemini 桥接）、PrepareRequestBody 封套（含 project 注入）、
/// v1internal 目标路径、GeminiCLI UA 请求头、HasUsableResponse 判定与响应转换（Gemini → OpenAI）。
/// </summary>
public sealed class GeminiProxyEndToEndTests
{
    private const string GeminiUpstreamJson =
        "{\"response\":{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":\"你好，来自 Gemini\"}]},\"finishReason\":\"STOP\"}],\"usageMetadata\":{\"promptTokenCount\":50,\"cachedContentTokenCount\":10,\"candidatesTokenCount\":6,\"thoughtsTokenCount\":2}}}";

    private static readonly Guid SiteId = Guid.Parse("21212121-2121-2121-2121-212121212121");
    private static readonly Guid GoogleAccountId = Guid.Parse("23232323-2323-2323-2323-232323232323");
    private static readonly Guid AccessKeyId = Guid.Parse("35353535-3535-3535-3535-353535353535");
    private static readonly Guid RouteRuleId = Guid.Parse("57575757-5757-5757-5757-575757575757");

    [Fact]
    public async Task Chat_completions_on_gemini_site_converts_request_and_response_end_to_end()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        await using var factory = new GeminiProxyWebApplicationFactory(request =>
        {
            captured = request;
            capturedBody = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(GeminiUpstreamJson, Encoding.UTF8, "application/json")
            };
        });
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(
                """{"model":"auto","messages":[{"role":"user","content":"say hi"}],"stream":false}""",
                Encoding.UTF8,
                "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();

        // —— 上游请求断言 ——
        captured.Should().NotBeNull();
        captured!.RequestUri!.AbsolutePath.Should().Be("/v1internal:generateContent", "Gemini 非流式走 v1internal 端点");
        captured.RequestUri.Host.Should().Be("cloudcode-pa.googleapis.com");
        captured.Headers.Authorization?.Scheme.Should().Be("Bearer");
        captured.Headers.Authorization?.Parameter.Should().Be("ya29-upstream-token");
        captured.Headers.TryGetValues("User-Agent", out var ua).Should().BeTrue();
        ua!.First().Should().StartWith("GeminiCLI/", "GeminiCLI 上游需要客户端仿真 UA");

        using var requestDoc = System.Text.Json.JsonDocument.Parse(capturedBody!);
        var requestRoot = requestDoc.RootElement;
        requestRoot.GetProperty("model").GetString().Should().Be("gemini-2.5-pro");
        requestRoot.GetProperty("project").GetString().Should().Be("e2e-project-123", "路由目标应注入 Google 账号的项目 ID");
        requestRoot.GetProperty("request").GetProperty("contents").GetArrayLength().Should().BeGreaterThan(0);
        requestRoot.GetProperty("request").GetProperty("safetySettings").GetArrayLength().Should().Be(10);

        // —— 客户端响应断言（Gemini → OpenAI 转换）——
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("object").GetString().Should().Be("chat.completion");
        var message = root.GetProperty("choices")[0].GetProperty("message");
        message.GetProperty("content").GetString().Should().Be("你好，来自 Gemini");
        root.GetProperty("choices")[0].GetProperty("finish_reason").GetString().Should().Be("stop");
        // usage 口径：input = 50-10；output = 6+2。
        root.GetProperty("usage").GetProperty("prompt_tokens").GetInt32().Should().Be(40);
        root.GetProperty("usage").GetProperty("prompt_tokens_details").GetProperty("cached_tokens").GetInt32().Should().Be(10);
        root.GetProperty("usage").GetProperty("completion_tokens").GetInt32().Should().Be(8);
    }

    [Fact]
    public async Task Refresh_quota_for_antigravity_account_returns_model_windows()
    {
        // Antigravity 额度链路：refresh-quota → fetchAvailableModels → 每模型剩余比例窗口持久化并在账号列表回显。
        const string quotaJson =
            "{\"models\":{\"gemini-3-pro-preview\":{\"quotaInfo\":{\"remainingFraction\":0.85,\"resetTime\":\"2026-08-20T02:30:00Z\"}},\"claude-sonnet-4-6\":{\"quotaInfo\":{\"remainingFraction\":0.05,\"resetTime\":\"2026-08-19T10:00:00Z\"}}}}";
        await using var factory = new GeminiProxyWebApplicationFactory(
            request => request.RequestUri!.AbsolutePath.Contains("fetchAvailableModels")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(quotaJson, Encoding.UTF8, "application/json")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(GeminiUpstreamJson, Encoding.UTF8, "application/json")
                },
            accountKind: "Antigravity",
            baseUrl: "https://daily-cloudcode-pa.googleapis.com");
        using var client = factory.CreateClient();

        using var refreshResponse = await client.PostAsync(
            $"/api/admin/google-accounts/accounts/{GoogleAccountId}/refresh-quota",
            content: null);
        var refreshBody = await refreshResponse.Content.ReadAsStringAsync();
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK, refreshBody);
        refreshBody.Should().Contain("gemini-3-pro-preview", "额度响应应包含模型窗口");

        using var listResponse = await client.GetAsync("/api/admin/google-accounts/accounts");
        var listBody = await listResponse.Content.ReadAsStringAsync();
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK, listBody);
        using var listDoc = System.Text.Json.JsonDocument.Parse(listBody);
        var account = listDoc.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == GoogleAccountId.ToString());
        var windows = account.GetProperty("windows");
        windows.GetArrayLength().Should().Be(2, "额度结果应持久化并在账号列表解析为窗口");
        windows[0].GetProperty("usedPercent").GetDouble().Should().BeApproximately(15d, 0.01);
        account.GetProperty("lastQuotaCheckedAt").GetString().Should().NotBeNullOrEmpty();
    }

    private sealed class GeminiProxyWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        private readonly string _accountKind;
        private readonly string _baseUrl;
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-gemini-e2e-{Guid.NewGuid():N}.db");

        public GeminiProxyWebApplicationFactory(
            Func<HttpRequestMessage, HttpResponseMessage> responder,
            string accountKind = "GeminiCli",
            string baseUrl = "https://cloudcode-pa.googleapis.com")
        {
            _responder = responder;
            _accountKind = accountKind;
            _baseUrl = baseUrl;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);

                // 真实 ProxyForwardService + 可控 handler（替换 IHttpClientFactory）。
                var handler = new StubHandler(_responder);
                var httpClient = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new SingletonHttpClientFactory(httpClient));
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            SeedAsync().GetAwaiter().GetResult();
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "gemini-e2e-key");
        }

        private async Task SeedAsync()
        {
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            SqlSugarSetup.InitializeDatabase(db.Client);

            db.Sites.Add(new Site
            {
                Id = SiteId,
                Name = "Gemini Site",
                BaseUrl = _baseUrl,
                ApiKey = "ya29-upstream-token",
                ProtocolType = "Gemini",
                SupportsOpenAi = false,
                SupportsAnthropic = false,
                SupportsResponses = false,
                ManagedSource = "Google",
                IsEnabled = true
            });

            db.GoogleAccounts.Add(new GoogleAccount
            {
                Id = GoogleAccountId,
                DisplayName = "Gemini E2E",
                Email = "e2e@example.com",
                AccountKind = _accountKind,
                ProjectId = "e2e-project-123",
                AccessToken = "ya29-upstream-token",
                RefreshToken = "rt",
                TokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
                LinkedSiteId = SiteId,
                IsEnabled = true
            });

            var modelId = Guid.Parse("a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1");
            db.ModelLibraryItems.Add(new ModelLibraryItem
            {
                Id = modelId,
                ModelName = "gemini-2.5-pro",
                DisplayName = "Gemini 2.5 Pro",
                IsEnabled = true
            });
            db.SiteModelMappings.Add(new SiteModelMapping
            {
                Id = Guid.Parse("b2b2b2b2-b2b2-b2b2-b2b2-b2b2b2b2b2b2"),
                SiteId = SiteId,
                ModelLibraryItemId = modelId,
                RemoteModelName = "gemini-2.5-pro",
                IsEnabled = true,
                MaxConcurrency = 0
            });

            var accessKeyRaw = "gemini-e2e-key";
            db.ProxyAccessKeys.Add(new ProxyAccessKey
            {
                Id = AccessKeyId,
                KeyName = "gemini-e2e",
                PlainKey = accessKeyRaw,
                AccessKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessKeyRaw))),
                MaskedValue = "sk-***e2e",
                IsEnabled = true
            });

            db.ProxyRouteRules.Add(new ProxyRouteRule
            {
                Id = RouteRuleId,
                ExternalModelName = "auto",
                UpstreamModelName = "gemini-2.5-pro",
                SiteId = SiteId,
                SiteModelName = "gemini-2.5-pro",
                Priority = 0,
                ModelPriority = 0,
                InstancePriority = 0,
                IsEnabled = true
            });

            db.Client.Deleteable<SystemRuntimeSettings>().Where(x => x.Id == 1).ExecuteCommand();
            db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
            {
                Id = 1,
                OAuthFeaturesEnabled = true,
                ProxyRequestTimeoutSeconds = 30,
                ProxyRetryCount = 0,
                DetectionRequestTimeoutSeconds = 60,
                DetectionRetryCount = 0,
                DetectionConcurrency = 1,
                CircuitBreakerFailureThreshold = 5,
                CircuitBreakerRecoveryMinutes = 2,
                UsageLogRetentionDays = 7,
                UsageLogAutoCleanupEnabled = true,
                DeveloperFeaturesEnabled = false
            });

            await db.SaveChangesAsync();
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private sealed class SingletonHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public SingletonHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }
}
