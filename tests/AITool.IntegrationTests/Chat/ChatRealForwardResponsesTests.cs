using System.Net;
using System.Text;
using AITool.Domain.Models;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net.Http.Headers;

namespace AITool.IntegrationTests.Chat;

/// <summary>
/// 用真实 ProxyForwardService + mock HTTP 验证 chat 非流式 Responses 端到端链路。
/// 复现"chat 页面访问 Responses 站点非流式空回复"。
/// </summary>
public sealed class ChatRealForwardResponsesTests
{
    // 真实 CPA 上游非流式 Responses 返回的 JSON（output 非空）
    private const string UpstreamResponsesJson = """
    {"id":"resp_1","object":"response","created_at":1786561763,"status":"completed","error":null,"model":"gpt-5.6-luna","output":[{"id":"msg_1","type":"message","status":"completed","content":[{"type":"output_text","annotations":[],"text":"Hi! How can I help?"}],"role":"assistant"}],"usage":{"input_tokens":5,"input_tokens_details":{"cached_tokens":1},"output_tokens":2,"total_tokens":7}}
    """;

    // 真实 Codex 上游流式 SSE（强制 stream=true）：
    // 关键特征——response.completed.response.output 为空 []，内容只通过 output_text.delta 推送。
    private const string CodexStreamingSse = """
    event: response.created
    data: {"type":"response.created","response":{"id":"resp_1","object":"response","status":"in_progress","output":[]}}

    event: response.output_text.delta
    data: {"type":"response.output_text.delta","delta":"Hi!","content_index":0,"output_index":0}

    event: response.output_text.done
    data: {"type":"response.output_text.done","text":"Hi!","content_index":0,"output_index":0}

    event: response.completed
    data: {"type":"response.completed","response":{"id":"resp_1","object":"response","status":"completed","output":[],"usage":{"input_tokens":7,"output_tokens":13,"total_tokens":20}}}

    """;

    private static readonly Guid SiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ModelId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task Post_send_responses_non_streaming_returns_content_from_upstream_json()
    {
        // CPA 站点：非流式直接返回 JSON
        await using var factory = new RealForwardFactory(
            "https://cpa.example.com", UpstreamResponsesJson, isSse: false);
        using var client = factory.CreateClient();

        var body = await SendChatAsync(client);

        using var document = System.Text.Json.JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue(body);
        root.GetProperty("content").GetString().Should().Be("Hi! How can I help?", body);
        root.GetProperty("inputTokens").GetInt32().Should().Be(5, body);
        root.GetProperty("outputTokens").GetInt32().Should().Be(2, body);
    }

    [Fact]
    public async Task Post_send_codex_non_streaming_aggregates_sse_and_returns_content()
    {
        // Codex 上游（chatgpt.com/backend-api/codex）强制 stream=true，返回 SSE。
        // response.completed.output 为空，内容在 output_text.delta 里。
        // chat 非流式必须聚合 SSE 并从 delta 重建 output，否则空回复。
        await using var factory = new RealForwardFactory(
            "https://chatgpt.com/backend-api/codex", CodexStreamingSse, isSse: true);
        using var client = factory.CreateClient();

        var body = await SendChatAsync(client);

        using var document = System.Text.Json.JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue(body);
        // 核心断言：从 delta 聚合的内容必须出现在 chat 响应里（非空回复）
        root.GetProperty("content").GetString().Should().Be("Hi!", body);
        root.GetProperty("inputTokens").GetInt32().Should().Be(7, body);
        root.GetProperty("outputTokens").GetInt32().Should().Be(13, body);
    }

    private static async Task<string> SendChatAsync(HttpClient client)
    {
        var response = await client.PostAsync(
            "/api/admin/chat/send",
            new StringContent(
                $"{{\"modelId\":\"{ModelId}\",\"message\":\"say hi\",\"enableReasoning\":false,\"enableStreaming\":false}}",
                Encoding.UTF8,
                "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return body;
    }

    private sealed class RealForwardFactory : WebApplicationFactory<Program>
    {
        private readonly string _baseUrl;
        private readonly string _upstreamBody;
        private readonly bool _isSse;
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"aitool-real-fwd-{Guid.NewGuid():N}.db");

        public RealForwardFactory(string baseUrl, string upstreamBody, bool isSse)
        {
            _baseUrl = baseUrl;
            _upstreamBody = upstreamBody;
            _isSse = isSse;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _dbPath);

                // 注入 mock HttpClientFactory，让真实 ProxyForwardService 拿到可控 handler
                var contentType = _isSse ? "text/event-stream" : "application/json";
                var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_upstreamBody, Encoding.UTF8, contentType)
                });
                var mockFactory = new SingletonHttpClientFactory(new HttpClient(handler));
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(mockFactory);
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

            db.Sites.Add(new Site
            {
                Id = SiteId,
                Name = "Responses Site",
                BaseUrl = _baseUrl,
                ApiKey = "sk-test",
                ProtocolType = "Responses",
                SupportsOpenAi = true,
                SupportsAnthropic = true,
                SupportsResponses = true,
                IsEnabled = true
            });
            db.ModelLibraryItems.Add(new ModelLibraryItem
            {
                Id = ModelId,
                ModelName = "gpt-5.6-luna",
                DisplayName = "GPT 5.6 Luna",
                IsEnabled = true
            });
            db.SiteModelMappings.Add(new SiteModelMapping
            {
                Id = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                SiteId = SiteId,
                ModelLibraryItemId = ModelId,
                RemoteModelName = "gpt-5.6-luna",
                IsEnabled = true,
                MaxConcurrency = 0
            });
            db.Client.Deleteable<SystemRuntimeSettings>().Where(x => x.Id == 1).ExecuteCommand();
            db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
            {
                Id = 1,
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
