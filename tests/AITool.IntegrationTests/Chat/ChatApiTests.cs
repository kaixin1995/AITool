using System.Net;
using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
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
/// 聊天测试接口集成测试，覆盖跨协议非流式发送的准确性。
/// </summary>
public sealed class ChatApiTests
{
    /// <summary>
    /// 验证发送聊天消息到 Anthropic 目标时，会按路由协议构造请求。
    /// </summary>
    [Fact]
    public async Task Post_send_uses_route_protocol_for_anthropic_targets()
    {
        var fakeForwardService = new ChatFakeProxyForwardService();
        await using var factory = new ChatWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/admin/chat/send",
            new StringContent(
                $"{{\"modelId\":\"{ChatWebApplicationFactory.ModelId}\",\"message\":\"hello\",\"enableReasoning\":false,\"enableStreaming\":false}}",
                Encoding.UTF8,
                "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        fakeForwardService.Requests.Should().HaveCount(1);
        fakeForwardService.Requests[0].ProtocolType.Should().Be("Anthropic");
        fakeForwardService.Requests[0].RequestBody.Should().Contain("\"messages\"");

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("content").GetString().Should().Be("anthropic-ok");
        document.RootElement.GetProperty("inputTokens").GetInt32().Should().Be(3);
        document.RootElement.GetProperty("outputTokens").GetInt32().Should().Be(5);
    }

    /// <summary>
    /// 验证发送聊天消息到 Responses-only 目标时，会构造 Responses 请求并命中 responses 路径。
    /// </summary>
    [Fact]
    public async Task Post_send_uses_responses_protocol_for_responses_only_targets()
    {
        var fakeForwardService = new ChatFakeProxyForwardService();
        await using var factory = new ChatWebApplicationFactory(fakeForwardService, null, "Responses");
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/admin/chat/send",
            new StringContent(
                $"{{\"modelId\":\"{ChatWebApplicationFactory.ModelId}\",\"message\":\"hello\",\"enableReasoning\":false,\"enableStreaming\":false}}",
                Encoding.UTF8,
                "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        fakeForwardService.Requests.Should().HaveCount(1);
        fakeForwardService.Requests[0].ProtocolType.Should().Be("Responses");
        fakeForwardService.Requests[0].RequestBody.Should().Contain("\"input\"");
        fakeForwardService.Requests[0].TargetPath.Should().Be("/v1/responses");

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("content").GetString().Should().Be("responses-chat-ok");
        document.RootElement.GetProperty("inputTokens").GetInt32().Should().Be(5);
        document.RootElement.GetProperty("outputTokens").GetInt32().Should().Be(7);
    }

    /// <summary>
    /// 模型下拉的候选站点模型应返回站点名和模型名。
    /// </summary>
    [Fact]
    public async Task Get_model_targets_returns_site_and_model_names()
    {
        var fakeForwardService = new ChatFakeProxyForwardService();
        await using var factory = new ChatWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/admin/chat/models/{ChatWebApplicationFactory.ModelId}/targets");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetArrayLength().Should().Be(2);
        document.RootElement[0].GetProperty("siteName").GetString().Should().Be("Anthropic Site");
        document.RootElement[0].GetProperty("siteModelName").GetString().Should().Be(ChatWebApplicationFactory.SiteModelName);
    }

    /// <summary>
    /// 指定站点模型时应直发到选中的实例，不再走合并路由。
    /// </summary>
    [Fact]
    public async Task Post_send_uses_selected_site_model_target()
    {
        var fakeForwardService = new ChatFakeProxyForwardService();
        await using var factory = new ChatWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/admin/chat/send",
            new StringContent(
                $"{{\"modelId\":\"{ChatWebApplicationFactory.ModelId}\",\"mappingId\":\"{ChatWebApplicationFactory.SecondMappingId}\",\"message\":\"hello\",\"enableReasoning\":false,\"enableStreaming\":false}}",
                Encoding.UTF8,
                "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        fakeForwardService.Requests.Should().HaveCount(1);
        fakeForwardService.Requests[0].ProtocolType.Should().Be("OpenAI");
        fakeForwardService.Requests[0].TargetModelName.Should().Be(ChatWebApplicationFactory.SecondSiteModelName);
        fakeForwardService.Requests[0].RequestBody.Should().Contain(ChatWebApplicationFactory.SecondSiteModelName);
    }

    /// <summary>
    /// 验证流式聊天会使用 Anthropic SSE 协议，并返回解析后的事件流。
    /// </summary>
    [Fact]
    public async Task Post_send_stream_uses_anthropic_sse_protocol_and_returns_stream_events()
    {
        var fakeForwardService = new ChatFakeProxyForwardService();
        var fakeHttpFactory = new ChatFakeHttpClientFactory(new ChatStreamingHttpMessageHandler());
        await using var factory = new ChatWebApplicationFactory(fakeForwardService, fakeHttpFactory);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/admin/chat/send-stream",
            new StringContent(
                $"{{\"modelId\":\"{ChatWebApplicationFactory.ModelId}\",\"message\":\"hello-stream\",\"enableReasoning\":true,\"enableStreaming\":true}}",
                Encoding.UTF8,
                "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        fakeHttpFactory.Handler.Requests.Should().HaveCount(1);
        fakeHttpFactory.Handler.Requests[0].RequestUri!.AbsoluteUri.Should().EndWith("/v1/messages");
        fakeHttpFactory.Handler.Requests[0].Headers.Contains("x-api-key").Should().BeTrue();
        fakeHttpFactory.Handler.Requests[0].Headers.Authorization.Should().BeNull();
        body.Should().Contain("event: reasoning");
        body.Should().Contain("ponder");
        body.Should().Contain("event: token");
        body.Should().Contain("stream-ok");
        body.Should().Contain("event: meta");
        body.Should().Contain("\"success\":true");
        body.Should().Contain("\"inputTokens\":4");
        body.Should().Contain("\"outputTokens\":6");
        body.Should().Contain("event: done");
    }

    /// <summary>
    /// 验证流式聊天命中 Responses-only 目标时，会打到 responses 路径并把 Responses SSE 转回聊天流。
    /// </summary>
    [Fact]
    public async Task Post_send_stream_uses_responses_protocol_and_returns_stream_events()
    {
        var fakeForwardService = new ChatFakeProxyForwardService();
        var fakeHttpFactory = new ChatFakeHttpClientFactory(new ChatStreamingHttpMessageHandler("Responses"));
        await using var factory = new ChatWebApplicationFactory(fakeForwardService, fakeHttpFactory, "Responses");
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/admin/chat/send-stream",
            new StringContent(
                $"{{\"modelId\":\"{ChatWebApplicationFactory.ModelId}\",\"message\":\"hello-stream\",\"enableReasoning\":false,\"enableStreaming\":true}}",
                Encoding.UTF8,
                "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        fakeHttpFactory.Handler.Requests.Should().HaveCount(1);
        fakeHttpFactory.Handler.Requests[0].RequestUri!.AbsoluteUri.Should().EndWith("/v1/responses");
        fakeHttpFactory.Handler.Requests[0].Content!.ReadAsStringAsync().GetAwaiter().GetResult().Should().Contain("\"input\"");
        body.Should().Contain("event: token");
        body.Should().Contain("responses-stream-ok");
        body.Should().Contain("event: meta");
        body.Should().Contain("\"success\":true");
        body.Should().Contain("\"inputTokens\":5");
        body.Should().Contain("\"outputTokens\":7");
        body.Should().Contain("event: done");
    }

    /// <summary>
    /// 验证 Admin/Chat 的非流式请求也会进入统一并发统计，便于调试页看到真实占用。
    /// </summary>
    [Fact]
    public async Task Post_send_is_tracked_by_model_concurrency_limiter()
    {
        var fakeForwardService = new ChatFakeProxyForwardService
        {
            RequestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
            ContinueRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var factory = new ChatWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();
        var limiter = factory.Services.GetRequiredService<ModelConcurrencyLimiter>();

        var sendTask = client.PostAsync(
            "/api/admin/chat/send",
            new StringContent(
                $"{{\"modelId\":\"{ChatWebApplicationFactory.ModelId}\",\"message\":\"hello-concurrency\",\"enableReasoning\":false,\"enableStreaming\":false}}",
                Encoding.UTF8,
                "application/json"));

        await fakeForwardService.RequestStarted!.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var activeSnapshots = limiter.ListRecent(ModelConcurrencyLimiter.RecentRetention);
        activeSnapshots.Should().ContainSingle(x => x.SiteModelName == "claude-3-7-sonnet" && x.ActiveCount == 1);

        fakeForwardService.ContinueRequest!.SetResult(true);
        using var response = await sendTask;
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        limiter.ListRecent(ModelConcurrencyLimiter.RecentRetention)
            .Should().ContainSingle(x => x.SiteModelName == "claude-3-7-sonnet" && x.ActiveCount == 0);
    }

    /// <summary>
    /// 后台修改并发上限后，应立即影响后续新请求，而不必等待缓存自然过期。
    /// </summary>
    [Fact]
    public async Task Put_concurrency_applies_new_limit_immediately()
    {
        var fakeForwardService = new ChatFakeProxyForwardService();
        await using var factory = new ChatWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();
        var limiter = factory.Services.GetRequiredService<ModelConcurrencyLimiter>();

        using var firstHandle = await limiter.AcquireAsync(
            factory.Services,
            ChatWebApplicationFactory.SiteId,
            ChatWebApplicationFactory.SiteModelName,
            ConcurrencyAcquireMode.SkipOnFull,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
        firstHandle.Acquired.Should().BeTrue();

        using var blockedHandle = await limiter.AcquireAsync(
            factory.Services,
            ChatWebApplicationFactory.SiteId,
            ChatWebApplicationFactory.SiteModelName,
            ConcurrencyAcquireMode.SkipOnFull,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
        blockedHandle.Acquired.Should().BeFalse();

        using var response = await client.PutAsync(
            $"/api/admin/models/mappings/{ChatWebApplicationFactory.MappingId}/concurrency",
            new StringContent("{\"maxConcurrency\":2}", Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var secondHandle = await limiter.AcquireAsync(
            factory.Services,
            ChatWebApplicationFactory.SiteId,
            ChatWebApplicationFactory.SiteModelName,
            ConcurrencyAcquireMode.SkipOnFull,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
        secondHandle.Acquired.Should().BeTrue();
    }
}

/// <summary>
/// 用于构建 ChatWebApplicationFactory 对应的测试宿主，并准备隔离的测试数据。
/// </summary>
internal sealed class ChatWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// 当前测试使用的模型标识。
    /// </summary>
    internal static readonly Guid ModelId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    /// <summary>
    /// 当前测试使用的站点标识。
    /// </summary>
    internal static readonly Guid SiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    /// <summary>
    /// 当前测试使用的站点模型映射标识。
    /// </summary>
    internal static readonly Guid MappingId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    /// <summary>
    /// 第二个站点模型映射标识。
    /// </summary>
    internal static readonly Guid SecondMappingId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    /// <summary>
    /// 当前测试使用的站点模型名称。
    /// </summary>
    internal const string SiteModelName = "claude-3-7-sonnet";
    /// <summary>
    /// 第二个站点模型名称。
    /// </summary>
    internal const string SecondSiteModelName = "gpt-4.1-mini";
    /// <summary>
    /// 保存当前测试使用的临时数据库路径。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-chat-{Guid.NewGuid():N}.db");
    /// <summary>
    /// 保存当前测试注入的模拟代理转发服务。
    /// </summary>
    private readonly ChatFakeProxyForwardService _fakeForwardService;
    /// <summary>
    /// 保存当前测试注入的 HttpClientFactory。
    /// </summary>
    private readonly IHttpClientFactory? _httpClientFactory;

    /// <summary>
    /// 保存当前测试站点声明的协议类型。
    /// </summary>
    private readonly string _siteProtocol;

    /// <summary>
    /// 创建聊天接口测试宿主，并使用默认的 HTTP 客户端工厂。
    /// </summary>
    public ChatWebApplicationFactory(ChatFakeProxyForwardService fakeForwardService)
        : this(fakeForwardService, null, "Anthropic")
    {
    }

    /// <summary>
    /// 创建聊天接口测试宿主，并注入模拟转发服务和可选的 HTTP 客户端工厂。
    /// </summary>
    public ChatWebApplicationFactory(ChatFakeProxyForwardService fakeForwardService, IHttpClientFactory? httpClientFactory, string siteProtocol = "Anthropic")
    {
        _fakeForwardService = fakeForwardService;
        _httpClientFactory = httpClientFactory;
        _siteProtocol = siteProtocol;
    }

    /// <summary>
    /// 配置聊天接口测试所需的服务和数据库。
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);
            services.RemoveAll<IProxyForwardService>();
            services.AddSingleton<IProxyForwardService>(_fakeForwardService);
            if (_httpClientFactory is not null)
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton(_httpClientFactory);
            }
        });
    }

    /// <summary>
    /// 创建客户端后初始化当前测试场景的数据。
    /// </summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        SeedAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 准备当前测试场景所需的数据。
    /// </summary>
    private async Task SeedAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SqlSugarSetup.InitializeDatabase(db.Client);

        db.Sites.AddRange(
            new Site
            {
                Id = SiteId,
                Name = "Anthropic Site",
                BaseUrl = "https://anthropic.example.com",
                ApiKey = "anthropic-key",
                ProtocolType = _siteProtocol,
                SupportsOpenAi = string.Equals(_siteProtocol, "OpenAI", StringComparison.OrdinalIgnoreCase),
                SupportsAnthropic = string.Equals(_siteProtocol, "Anthropic", StringComparison.OrdinalIgnoreCase),
                IsEnabled = true
            },
            new Site
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Name = "OpenAI Site",
                BaseUrl = "https://openai.example.com",
                ApiKey = "openai-key",
                ProtocolType = "OpenAI",
                SupportsOpenAi = true,
                SupportsAnthropic = false,
                IsEnabled = true
            });

        db.ModelLibraryItems.Add(new ModelLibraryItem
        {
            Id = ModelId,
            ModelName = "chat-anthropic",
            DisplayName = "Chat Anthropic",
            IsEnabled = true
        });

        db.ProxyRouteRules.Add(new ProxyRouteRule
        {
            ExternalModelName = "chat-anthropic",
            UpstreamModelName = SiteModelName,
            SiteId = SiteId,
            SiteModelName = SiteModelName,
            Priority = 0,
            ModelPriority = 0,
            InstancePriority = 0,
            IsEnabled = true
        });

        db.SiteModelMappings.AddRange(
            new SiteModelMapping
            {
                Id = MappingId,
                SiteId = SiteId,
                ModelLibraryItemId = ModelId,
                RemoteModelName = SiteModelName,
                IsEnabled = true,
                MaxConcurrency = 1
            },
            new SiteModelMapping
            {
                Id = SecondMappingId,
                SiteId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                ModelLibraryItemId = ModelId,
                RemoteModelName = SecondSiteModelName,
                IsEnabled = true,
                MaxConcurrency = 2
            });

        db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            ProxyRequestTimeoutSeconds = 8,
            ProxyRetryCount = 1,
            DetectionRequestTimeoutSeconds = 60,
            DetectionRetryCount = 0,
            DetectionConcurrency = 1,
            CircuitBreakerFailureThreshold = 5,
            CircuitBreakerRecoveryMinutes = 2,
            UsageLogRetentionDays = 7,
            UsageLogAutoCleanupEnabled = true
        });

        await db.SaveChangesAsync();
    }
}

/// <summary>
/// 用于模拟代理转发结果，支撑 ChatFakeProxyForwardService 相关断言。
/// </summary>
internal sealed class ChatFakeProxyForwardService : IProxyForwardService
{
    /// <summary>
    /// 记录测试期间收到的代理转发请求。
    /// </summary>
    public List<ProxyForwardRequest> Requests { get; } = [];
    /// <summary>
    /// 可选的开始通知，用于观测请求进入转发阶段后的并发状态。
    /// </summary>
    public TaskCompletionSource<bool>? RequestStarted { get; set; }
    /// <summary>
    /// 可选的继续开关，用于延迟返回结果，便于断言请求进行中的并发数。
    /// </summary>
    public TaskCompletionSource<bool>? ContinueRequest { get; set; }

    /// <summary>
    /// 根据请求协议返回对应的兼容响应，验证聊天页非流式调用不会误用 OpenAI 协议。
    /// </summary>
    public async Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(new ProxyForwardRequest
        {
            TargetBaseUrl = request.TargetBaseUrl,
            TargetApiKey = request.TargetApiKey,
            ProtocolType = request.ProtocolType,
            TargetModelName = request.TargetModelName,
            RequestBody = request.RequestBody,
            PreparedRequestBody = request.PreparedRequestBody,
            EnableStreaming = request.EnableStreaming,
            RequestTimeoutSeconds = request.RequestTimeoutSeconds,
            RetryCount = request.RetryCount,
            TargetPath = request.TargetPath
        });

        RequestStarted?.TrySetResult(true);
        if (ContinueRequest is not null)
        {
            await ContinueRequest.Task.WaitAsync(cancellationToken);
        }

        if (request.ProtocolType == "Anthropic")
        {
            return new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = "{\"content\":[{\"type\":\"text\",\"text\":\"anthropic-ok\"}],\"usage\":{\"input_tokens\":3,\"output_tokens\":5}}",
                InputTokens = 3,
                OutputTokens = 5
            };
        }

        if (request.ProtocolType == "Responses")
        {
            return new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = "{\"id\":\"resp_test\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"model\":\"response-model\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"responses-chat-ok\"}]}],\"usage\":{\"input_tokens\":5,\"input_tokens_details\":{\"cached_tokens\":1},\"output_tokens\":7,\"total_tokens\":12}}",
                InputTokens = 5,
                CachedTokens = 1,
                OutputTokens = 7
            };
        }

        return new ProxyForwardResult
        {
            Success = true,
            StatusCode = 200,
            ResponseBody = "{\"choices\":[{\"message\":{\"content\":\"openai-ok\"}}],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":4}}",
            InputTokens = 2,
            OutputTokens = 4
        };
    }

    /// <summary>
    /// 复用非流式结果，并按行回放为模拟的流式数据。
    /// </summary>
    public async Task<ProxyForwardResult> ForwardStreamingAsync(
        ProxyForwardRequest request,
        Func<string, CancellationToken, Task> onSseDataAsync,
        CancellationToken cancellationToken = default)
    {
        var result = await ForwardAsync(request, cancellationToken);
        foreach (var line in result.ResponseBody.Replace("\r\n", "\n").Split('\n'))
        {
            await onSseDataAsync(line, cancellationToken);
        }

        result.IsStreaming = true;
        return result;
    }
}

/// <summary>
/// 用于为 ChatFakeHttpClientFactory 提供可控的 HttpClient 实例。
/// </summary>
internal sealed class ChatFakeHttpClientFactory : IHttpClientFactory
{
    /// <summary>
    /// 保存供测试断言使用的流式消息处理器。
    /// </summary>
    public ChatStreamingHttpMessageHandler Handler { get; }

    /// <summary>
    /// 创建可返回指定消息处理器的 HTTP 客户端工厂。
    /// </summary>
    public ChatFakeHttpClientFactory(ChatStreamingHttpMessageHandler handler)
    {
        Handler = handler;
    }

    /// <summary>
    /// 创建使用固定消息处理器的 HttpClient 实例。
    /// </summary>
    public HttpClient CreateClient(string name)
    {
        return new HttpClient(Handler, disposeHandler: false);
    }
}

/// <summary>
/// 用于模拟 ChatStreamingHttpMessageHandler 的底层 HTTP 响应。
/// </summary>
internal sealed class ChatStreamingHttpMessageHandler : HttpMessageHandler
{
    private readonly string _siteProtocol;

    /// <summary>
    /// 记录测试期间发送出去的 HTTP 请求。
    /// </summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    public ChatStreamingHttpMessageHandler(string siteProtocol = "Anthropic")
    {
        _siteProtocol = siteProtocol;
    }

    /// <summary>
    /// 模拟上游流式响应，验证聊天链路会按真实协议解析思考与正文。
    /// </summary>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(CloneRequest(request));

        var payload = string.Equals(_siteProtocol, "Responses", StringComparison.OrdinalIgnoreCase)
            ? string.Join(
                "\n",
                "event: response.created",
                "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_stream\",\"object\":\"response\",\"created_at\":1,\"status\":\"in_progress\",\"model\":\"response-model\",\"output\":[],\"usage\":null}}",
                string.Empty,
                "event: response.output_text.delta",
                "data: {\"type\":\"response.output_text.delta\",\"delta\":\"responses-stream-ok\",\"output_index\":0,\"content_index\":0}",
                string.Empty,
                "event: response.completed",
                "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_stream\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"model\":\"response-model\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"responses-stream-ok\"}]}],\"usage\":{\"input_tokens\":5,\"input_tokens_details\":{\"cached_tokens\":1},\"output_tokens\":7,\"total_tokens\":12}}}",
                string.Empty,
                "data: [DONE]",
                string.Empty)
            : string.Join(
                "\n",
                "event: message_start",
                "data: {\"message\":{\"usage\":{\"input_tokens\":4,\"output_tokens\":0}}}",
                string.Empty,
                "event: content_block_delta",
                "data: {\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"ponder\"}}",
                string.Empty,
                "event: content_block_delta",
                "data: {\"delta\":{\"type\":\"text_delta\",\"text\":\"stream-ok\"}}",
                string.Empty,
                "event: message_delta",
                "data: {\"usage\":{\"output_tokens\":6}}",
                string.Empty,
                "data: [DONE]",
                string.Empty);

        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "text/event-stream")
        };

        return Task.FromResult(response);
    }

    /// <summary>
    /// 复制请求对象，便于在断言阶段读取请求内容。
    /// </summary>
    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(body, Encoding.UTF8);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}
