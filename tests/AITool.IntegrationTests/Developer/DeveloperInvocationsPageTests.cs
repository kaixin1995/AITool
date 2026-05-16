using System.Net;
using System.Security.Cryptography;
using System.Text;
using AITool.Application.Proxy;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.Developer;

/// <summary>
/// 调用调试页集成测试，验证开关控制与内存记录展示。
/// </summary>
public sealed class DeveloperInvocationsPageTests
{
    /// <summary>
    /// 验证关闭开发者功能后，请求调用调试页会返回未找到。
    /// </summary>
    [Fact]
    public async Task Get_invocations_page_returns_not_found_when_feature_is_disabled()
    {
        await using var factory = new DeveloperInvocationsWebApplicationFactory(false, new DeveloperInvocationsFakeProxyForwardService());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/Developer/Invocations");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// 验证启用开发者功能后，调用调试页会展示最新的请求和响应内容。
    /// </summary>
    [Fact]
    public async Task Get_invocations_page_shows_latest_request_and_response_when_feature_is_enabled()
    {
        var fakeForwardService = new DeveloperInvocationsFakeProxyForwardService();
        await using var factory = new DeveloperInvocationsWebApplicationFactory(true, fakeForwardService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"model\":\"debug-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hello debug\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new global::System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "debug-key");
        request.Headers.UserAgent.ParseAdd("claude-code/1.0");
        request.Headers.Add("X-AITool-Source", "claude-code");

        var invokeResponse = await client.SendAsync(request);
        invokeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var pageResponse = await client.GetAsync("/Admin/Developer/Invocations");
        var html = await pageResponse.Content.ReadAsStringAsync();
        var listResponse = await client.GetAsync("/Admin/Developer/Invocations?handler=List");
        var payload = await listResponse.Content.ReadAsStringAsync();

        pageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("调用调试");
        html.Should().Contain("自动刷新（5 秒）");
        html.Should().Contain("调用记录按需加载中");
        html.Should().Contain("setTimeout(function () {");
        html.Should().Contain("refreshTraceList();");
        payload.Should().Contain("claude-code");
        payload.Should().Contain("debug-model");
        payload.Should().Contain("debug-upstream-model");
        payload.Should().Contain("Debug Site");
        payload.Should().Contain("debug-ok");
        payload.Should().Contain("hello debug");
    }

    /// <summary>
    /// 验证调用调试页包含自动刷新相关脚本标记。
    /// </summary>
    [Fact]
    public async Task Get_invocations_page_contains_auto_refresh_script_markers()
    {
        var fakeForwardService = new DeveloperInvocationsFakeProxyForwardService();
        await using var factory = new DeveloperInvocationsWebApplicationFactory(true, fakeForwardService);
        using var client = factory.CreateClient();

        var pageResponse = await client.GetAsync("/Admin/Developer/Invocations");
        var html = await pageResponse.Content.ReadAsStringAsync();

        pageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("refreshTraceList");
        html.Should().Contain("ensureRefreshTimer");
        html.Should().Contain("renderAttempts(entry, index) || '<div class=\"trace-info-panel\">当前还没有命中任何路由尝试。</div>'");
        html.Should().Contain("autoRefreshToggle.checked && !hasLoadedListOnce");
        html.Should().Contain("setTimeout(function () {");
        html.Should().NotContain("' + renderAttempts(entry, index) + '");
    }

    /// <summary>
    /// 验证调用调试页默认不会自动开启刷新。
    /// </summary>
    [Fact]
    public async Task Get_invocations_page_does_not_enable_auto_refresh_by_default()
    {
        var fakeForwardService = new DeveloperInvocationsFakeProxyForwardService();
        await using var factory = new DeveloperInvocationsWebApplicationFactory(true, fakeForwardService);
        using var client = factory.CreateClient();

        var pageResponse = await client.GetAsync("/Admin/Developer/Invocations");
        var html = await pageResponse.Content.ReadAsStringAsync();

        pageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("id=\"autoRefreshToggle\"");
        html.Should().NotContain("id=\"autoRefreshToggle\" checked");
    }

    /// <summary>
    /// 验证启用开发者功能后，列表接口会返回调用记录数据。
    /// </summary>
    [Fact]
    public async Task Get_list_returns_entries_payload_when_feature_is_enabled()
    {
        var fakeForwardService = new DeveloperInvocationsFakeProxyForwardService();
        await using var factory = new DeveloperInvocationsWebApplicationFactory(true, fakeForwardService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"model\":\"debug-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hello ajax\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new global::System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "debug-key");

        var invokeResponse = await client.SendAsync(request);
        invokeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var listResponse = await client.GetAsync("/Admin/Developer/Invocations?handler=List");
        var payload = await listResponse.Content.ReadAsStringAsync();

        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        payload.Should().Contain("totalCount");
        payload.Should().Contain("failedCount");
        payload.Should().Contain("pendingCount");
        payload.Should().Contain("hello ajax");
        payload.Should().Contain("debug-upstream-model");
    }

    /// <summary>
    /// 验证调用调试页会展示最终成功前的全部路由尝试记录。
    /// </summary>
    [Fact]
    public async Task Get_invocations_page_shows_all_route_attempts_before_final_success()
    {
        var fakeForwardService = new DeveloperInvocationsFakeProxyForwardService();
        fakeForwardService.EnqueueResult(new ProxyForwardResult
        {
            Success = false,
            StatusCode = 500,
            ErrorMessage = "first route failed",
            ResponseBody = "{\"error\":\"first\"}",
            TotalDurationMs = 111
        });
        fakeForwardService.EnqueueResult(new ProxyForwardResult
        {
            Success = true,
            StatusCode = 200,
            ResponseBody = "{\"choices\":[{\"message\":{\"content\":\"second-ok\"}}]}",
            TotalDurationMs = 222
        });

        await using var factory = new DeveloperInvocationsWebApplicationFactory(true, fakeForwardService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"model\":\"debug-model\",\"messages\":[{\"role\":\"user\",\"content\":\"route chain\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new global::System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "debug-key");

        var invokeResponse = await client.SendAsync(request);
        invokeResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var listResponse = await client.GetAsync("/Admin/Developer/Invocations?handler=List");
        var payload = await listResponse.Content.ReadAsStringAsync();

        payload.Should().Contain("first route failed");
        payload.Should().Contain("second-ok");
        payload.Should().Contain("failedAttemptCount");
        payload.Should().Contain("successAttemptCount");
        payload.Should().Contain("debug-upstream-model-2");
        payload.Should().Contain("\"forwardingMode\":\"direct\"");
    }

    /// <summary>
    /// 验证启用开发者功能后，Anthropic 流式调用会返回带事件类型的调试数据。
    /// </summary>
    [Fact]
    public async Task Get_list_returns_anthropic_stream_payload_with_typed_events_when_feature_is_enabled()
    {
        var fakeForwardService = new DeveloperInvocationsFakeProxyForwardService
        {
            Result = new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                IsStreaming = true,
                ResponseBody = "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"!\"}}],\"usage\":{\"prompt_tokens\":10,\"prompt_tokens_details\":{\"cached_tokens\":2},\"completion_tokens\":3}}\n\ndata: [DONE]\n\n",
                InputTokens = 10,
                CachedTokens = 2,
                OutputTokens = 3,
                TotalDurationMs = 50
            }
        };
        await using var factory = new DeveloperInvocationsWebApplicationFactory(true, fakeForwardService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"debug-model\",\"max_tokens\":64,\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"stream hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "debug-key");
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        payload.Should().Contain("event: message_start");
        payload.Should().Contain("\"type\":\"message_start\"");
        payload.Should().Contain("event: content_block_start");
        payload.Should().Contain("\"type\":\"content_block_start\"");
        payload.Should().Contain("event: content_block_delta");
        payload.Should().Contain("\"type\":\"content_block_delta\"");
        payload.Should().Contain("event: content_block_stop");
        payload.Should().Contain("\"type\":\"content_block_stop\"");
        payload.Should().Contain("event: message_delta");
        payload.Should().Contain("\"type\":\"message_delta\"");
        payload.Should().Contain("event: message_stop");
        payload.Should().Contain("\"type\":\"message_stop\"");
        payload.Should().Contain("\"content\":[]");
        payload.Should().Contain("\"cache_creation_input_tokens\":0");
    }

    /// <summary>
    /// 验证上游缺少 DONE 事件时，系统仍会补齐 Anthropic 流式结束事件。
    /// </summary>
    [Fact]
    public async Task Get_anthropic_stream_appends_closing_events_when_upstream_done_is_missing()
    {
        var fakeForwardService = new DeveloperInvocationsFakeProxyForwardService
        {
            Result = new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                IsStreaming = true,
                HasStartedStreaming = true,
                IsStreamInterrupted = true,
                ResponseBody = "data: {\"choices\":[{\"delta\":{\"content\":\"Partial\"}}]}\n\n",
                InputTokens = 8,
                CachedTokens = 1,
                OutputTokens = 2,
                TotalDurationMs = 80,
                ErrorMessage = "stream interrupted before DONE"
            }
        };
        await using var factory = new DeveloperInvocationsWebApplicationFactory(true, fakeForwardService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"debug-model\",\"max_tokens\":64,\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"stream hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "debug-key");
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        payload.Should().Contain("event: message_delta");
        payload.Should().Contain("event: message_stop");
        payload.Should().Contain("\"type\":\"message_stop\"");

        var listResponse = await client.GetAsync("/Admin/Developer/Invocations?handler=List");
        var listPayload = await listResponse.Content.ReadAsStringAsync();
        listPayload.Should().Contain("\"forwardingMode\":\"bridge\"");
    }
}
/// <summary>
/// 开发者调用追踪页面的集成测试宿主，提供隔离数据库和伪造转发服务。
/// </summary>
internal sealed class DeveloperInvocationsWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// 保存当前测试使用的临时数据库路径。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-developer-invocations-{Guid.NewGuid():N}.db");
    /// <summary>
    /// 标记当前测试是否启用开发者功能。
    /// </summary>
    private readonly bool _developerFeaturesEnabled;
    /// <summary>
    /// 保存当前测试注入的模拟代理转发服务。
    /// </summary>
    private readonly DeveloperInvocationsFakeProxyForwardService _fakeForwardService;

    /// <summary>
    /// 创建调用调试页测试宿主，并记录功能开关和模拟转发服务。
    /// </summary>
    public DeveloperInvocationsWebApplicationFactory(bool developerFeaturesEnabled, DeveloperInvocationsFakeProxyForwardService fakeForwardService)
    {
        _developerFeaturesEnabled = developerFeaturesEnabled;
        _fakeForwardService = fakeForwardService;
    }

    /// <summary>
    /// 配置调用调试页测试所需的服务和数据库。
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
            services.RemoveAll<IProxyForwardService>();
            services.AddSingleton<IProxyForwardService>(_fakeForwardService);
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
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var siteId = Guid.Parse("90909090-9090-9090-9090-909090909090");
        var accessKeyRaw = "debug-key";
        var accessKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessKeyRaw)));

        db.Sites.Add(new Site
        {
            Id = siteId,
            Name = "Debug Site",
            BaseUrl = "https://debug.example.com",
            ApiKey = "debug-site-key",
            ProtocolType = "OpenAI",
            SupportsOpenAi = true,
            SupportsAnthropic = false,
            IsEnabled = true
        });

        db.ProxyAccessKeys.Add(new ProxyAccessKey
        {
            Id = Guid.Parse("91919191-9191-9191-9191-919191919191"),
            KeyName = "debug",
            PlainKey = accessKeyRaw,
            AccessKeyHash = accessKeyHash,
            MaskedValue = "sk-***debug",
            IsEnabled = true
        });

        db.ProxyRouteRules.Add(new ProxyRouteRule
        {
            Id = Guid.Parse("92929292-9292-9292-9292-929292929292"),
            ExternalModelName = "debug-model",
            UpstreamModelName = "debug-upstream-model",
            SiteId = siteId,
            SiteModelName = "debug-site-model",
            Priority = 0,
            ModelPriority = 0,
            InstancePriority = 0,
            IsEnabled = true
        });

        db.ProxyRouteRules.Add(new ProxyRouteRule
        {
            Id = Guid.Parse("93939393-9393-9393-9393-939393939393"),
            ExternalModelName = "debug-model",
            UpstreamModelName = "debug-upstream-model-2",
            SiteId = siteId,
            SiteModelName = "debug-site-model-2",
            Priority = 1,
            ModelPriority = 1,
            InstancePriority = 1,
            IsEnabled = true
        });

        db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            ProxyRequestTimeoutSeconds = 30,
            ProxyRetryCount = 1,
            DetectionRequestTimeoutSeconds = 60,
            DetectionRetryCount = 0,
            DetectionConcurrency = 1,
            CircuitBreakerFailureThreshold = 5,
            CircuitBreakerRecoveryMinutes = 2,
            UsageLogRetentionDays = 7,
            UsageLogAutoCleanupEnabled = true,
            DeveloperFeaturesEnabled = _developerFeaturesEnabled
        });

        await db.SaveChangesAsync();
    }
}

/// <summary>
/// 伪造的代理转发服务，用于在集成测试中模拟转发行为并记录调用参数。
/// </summary>
internal sealed class DeveloperInvocationsFakeProxyForwardService : IProxyForwardService
{
    /// <summary>
    /// 按顺序保存待返回的模拟转发结果。
    /// </summary>
    private readonly Queue<ProxyForwardResult> _queuedResults = new();

    /// <summary>
    /// 保存默认返回的模拟转发结果。
    /// </summary>
    public ProxyForwardResult Result { get; set; } = new()
    {
        Success = true,
        StatusCode = 200,
        ResponseBody = "{\"choices\":[{\"message\":{\"content\":\"debug-ok\"}}],\"usage\":{\"prompt_tokens\":5,\"prompt_tokens_details\":{\"cached_tokens\":1},\"completion_tokens\":7}}",
        InputTokens = 5,
        CachedTokens = 1,
        OutputTokens = 7,
        TotalDurationMs = 123
    };

    /// <summary>
    /// 向模拟结果队列追加待返回的结果。
    /// </summary>
    public void EnqueueResult(ProxyForwardResult result)
    {
        _queuedResults.Enqueue(result);
    }

    /// <summary>
    /// 优先返回排队的模拟结果，否则返回默认结果。
    /// </summary>
    public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
    {
        if (_queuedResults.Count > 0)
        {
            return Task.FromResult(_queuedResults.Dequeue());
        }

        return Task.FromResult(Result);
    }

    /// <summary>
    /// 按行回放模拟响应内容，供流式调用调试场景使用。
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

        return result;
    }
}
