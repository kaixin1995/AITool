using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
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
        html.Should().Contain("refreshTraceList(currentPage)");
        payload.Should().Contain("debug-model");
        payload.Should().Contain("debug-upstream-model");
        payload.Should().Contain("Debug Site");

        using var listDocument = JsonDocument.Parse(payload);
        var traceId = listDocument.RootElement.GetProperty("entries")[0].GetProperty("traceId").GetGuid();
        var detailResponse = await client.GetAsync($"/Admin/Developer/Invocations?handler=Detail&traceId={traceId}");
        var detailPayload = await detailResponse.Content.ReadAsStringAsync();

        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        detailPayload.Should().Contain("debug-ok");
        detailPayload.Should().Contain("hello debug");
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
        html.Should().Contain("renderAttempts(entry)");
        html.Should().Contain("当前还没有命中任何路由尝试");
        html.Should().Contain("autoRefreshToggle.checked");
        html.Should().Contain("isInvocationsTabActive()");
        html.Should().Contain("document.visibilityState === 'visible'");
        html.Should().Contain("document.addEventListener('visibilitychange'");
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
        payload.Should().Contain("debug-model");
        payload.Should().Contain("debug-upstream-model");
    }

    /// <summary>
    /// 验证关闭开发者功能后，并发检测接口也会返回未找到。
    /// </summary>
    [Fact]
    public async Task Get_concurrency_returns_not_found_when_feature_is_disabled()
    {
        await using var factory = new DeveloperInvocationsWebApplicationFactory(false, new DeveloperInvocationsFakeProxyForwardService());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/Developer/Invocations?handler=Concurrency");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// 验证当前模型并发检测接口只返回最近 6 小时内出现过的模型，并包含最大并发和排队数。
    /// </summary>
    [Fact]
    public async Task Get_concurrency_returns_live_active_entries_when_feature_is_enabled()
    {
        await using var factory = new DeveloperInvocationsWebApplicationFactory(true, new DeveloperInvocationsFakeProxyForwardService());
        using var client = factory.CreateClient();
        var limiter = factory.Services.GetRequiredService<ModelConcurrencyLimiter>();
        var siteId = Guid.Parse("90909090-9090-9090-9090-909090909090");

        using var handle = await limiter.AcquireAsync(
            factory.Services,
            siteId,
            "debug-site-model",
            ConcurrencyAcquireMode.SkipOnFull,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);

        handle.Acquired.Should().BeTrue();

        var response = await client.GetAsync("/Admin/Developer/Invocations?handler=Concurrency");
        var payload = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(payload);
        var items = document.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);

        var first = items[0];
        first.GetProperty("modelName").GetString().Should().Be("debug-site-model");
        first.GetProperty("siteName").GetString().Should().Be("Debug Site");
        first.GetProperty("activeCount").GetInt32().Should().Be(1);
        first.GetProperty("maxConcurrency").GetInt32().Should().Be(5);
        first.GetProperty("queueCount").GetInt32().Should().Be(0);
    }

    /// <summary>
    /// 验证当前模型并发检测接口会保留最近 6 小时内归零的模型，并显示 0 并发。
    /// </summary>
    [Fact]
    public async Task Get_concurrency_keeps_recent_zero_count_entries_when_feature_is_enabled()
    {
        await using var factory = new DeveloperInvocationsWebApplicationFactory(true, new DeveloperInvocationsFakeProxyForwardService());
        using var client = factory.CreateClient();
        var limiter = factory.Services.GetRequiredService<ModelConcurrencyLimiter>();
        var siteId = Guid.Parse("90909090-9090-9090-9090-909090909090");

        using (var handle = await limiter.AcquireAsync(
            factory.Services,
            siteId,
            "debug-site-model",
            ConcurrencyAcquireMode.SkipOnFull,
            TimeSpan.FromSeconds(10),
            CancellationToken.None))
        {
            handle.Acquired.Should().BeTrue();
        }

        var response = await client.GetAsync("/Admin/Developer/Invocations?handler=Concurrency");
        var payload = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(payload);
        var items = document.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        var debugModel = items[0];
        debugModel.GetProperty("modelName").GetString().Should().Be("debug-site-model");
        debugModel.GetProperty("siteName").GetString().Should().Be("Debug Site");
        debugModel.GetProperty("activeCount").GetInt32().Should().Be(0);
    }

    /// <summary>
    /// 验证页面已包含当前模型并发检测页签及其自动刷新脚本。
    /// </summary>
    [Fact]
    public async Task Get_invocations_page_contains_concurrency_tab_and_refresh_script_markers()
    {
        await using var factory = new DeveloperInvocationsWebApplicationFactory(true, new DeveloperInvocationsFakeProxyForwardService());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/Developer/Invocations");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("当前模型并发数检测");
        html.Should().Contain("developerConcurrencyPane");
        html.Should().Contain("refreshConcurrencyTable");
        html.Should().Contain("?handler=Concurrency");
    }

    /// <summary>
    /// 验证并发接口仅在最近 6 小时内出现过多个模型时，对排队项优先排序。
    /// </summary>
    [Fact]
    public async Task Get_concurrency_returns_queue_count_and_sorts_queued_first()
    {
        await using var factory = new DeveloperInvocationsWebApplicationFactory(true, new DeveloperInvocationsFakeProxyForwardService());
        using var client = factory.CreateClient();
        var limiter = factory.Services.GetRequiredService<ModelConcurrencyLimiter>();
        var siteId = Guid.Parse("90909090-9090-9090-9090-909090909090");

        // 先让无限制模型进入最近记录，便于验证排序。
        using var otherHandle = await limiter.AcquireAsync(
            factory.Services,
            siteId,
            "debug-site-model-2",
            ConcurrencyAcquireMode.SkipOnFull,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
        otherHandle.Acquired.Should().BeTrue();

        // debug-site-model 配置了 MaxConcurrency=5，需占满全部槽位后才能触发排队。
        var handles = new List<ConcurrencyAcquireResult>();
        for (var i = 0; i < 5; i++)
        {
            var h = await limiter.AcquireAsync(
                factory.Services,
                siteId,
                "debug-site-model",
                ConcurrencyAcquireMode.SkipOnFull,
                TimeSpan.FromSeconds(10),
                CancellationToken.None);
            h.Acquired.Should().BeTrue();
            handles.Add(h);
        }

        var waitingTask = limiter.AcquireAsync(
            factory.Services,
            siteId,
            "debug-site-model",
            ConcurrencyAcquireMode.WaitForSlot,
            TimeSpan.FromSeconds(30),
            CancellationToken.None).AsTask();

        await Task.Delay(200);

        var response = await client.GetAsync("/Admin/Developer/Invocations?handler=Concurrency");
        var payload = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(payload);
        var items = document.RootElement.GetProperty("items");

        items.GetArrayLength().Should().Be(2);

        var first = items[0];
        first.GetProperty("modelName").GetString().Should().Be("debug-site-model");
        first.GetProperty("queueCount").GetInt32().Should().BeGreaterThan(0);
        first.GetProperty("activeCount").GetInt32().Should().Be(5);

        var second = items[1];
        second.GetProperty("modelName").GetString().Should().Be("debug-site-model-2");
        second.GetProperty("queueCount").GetInt32().Should().Be(0);

        foreach (var h in handles) h.Dispose();
        await waitingTask;
        (await waitingTask).Dispose();
    }

    /// <summary>
    /// 验证并发接口对最近 6 小时内出现过的无限制模型返回 null 最大并发。
    /// </summary>
    [Fact]
    public async Task Get_concurrency_returns_null_for_unlimited_recent_model()
    {
        await using var factory = new DeveloperInvocationsWebApplicationFactory(true, new DeveloperInvocationsFakeProxyForwardService());
        using var client = factory.CreateClient();
        var limiter = factory.Services.GetRequiredService<ModelConcurrencyLimiter>();
        var siteId = Guid.Parse("90909090-9090-9090-9090-909090909090");

        using var handle = await limiter.AcquireAsync(
            factory.Services,
            siteId,
            "debug-site-model-2",
            ConcurrencyAcquireMode.SkipOnFull,
            TimeSpan.FromSeconds(10),
            CancellationToken.None);
        handle.Acquired.Should().BeTrue();

        var response = await client.GetAsync("/Admin/Developer/Invocations?handler=Concurrency");
        var payload = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(payload);
        var items = document.RootElement.GetProperty("items");

        items.GetArrayLength().Should().Be(1);
        items[0].GetProperty("modelName").GetString().Should().Be("debug-site-model-2");
        items[0].GetProperty("maxConcurrency").ValueKind.Should().Be(JsonValueKind.Null);
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

        payload.Should().Contain("failedAttemptCount");
        payload.Should().Contain("successAttemptCount");
        payload.Should().Contain("debug-upstream-model-2");

        using var listDocument = JsonDocument.Parse(payload);
        var traceId = listDocument.RootElement.GetProperty("entries")[0].GetProperty("traceId").GetGuid();
        var detailResponse = await client.GetAsync($"/Admin/Developer/Invocations?handler=Detail&traceId={traceId}");
        var detailPayload = await detailResponse.Content.ReadAsStringAsync();

        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        detailPayload.Should().Contain("first route failed");
        detailPayload.Should().Contain("second-ok");
        detailPayload.Should().Contain("\"forwardingMode\":\"direct\"");
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
        using var listDocument = JsonDocument.Parse(listPayload);
        var traceId = listDocument.RootElement.GetProperty("entries")[0].GetProperty("traceId").GetGuid();
        var detailResponse = await client.GetAsync($"/Admin/Developer/Invocations?handler=Detail&traceId={traceId}");
        var detailPayload = await detailResponse.Content.ReadAsStringAsync();

        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        detailPayload.Should().Contain("\"forwardingMode\":\"bridge\"");
    }

    /// <summary>
    /// 验证调用调试页会从缓存渲染默认密钥和默认调试模型。
    /// </summary>
    [Fact]
    public async Task Get_invocations_page_renders_default_access_key_and_debug_model_from_cache()
    {
        await using var factory = new DeveloperInvocationsWebApplicationFactory(true, new DeveloperInvocationsFakeProxyForwardService());
        using var client = factory.CreateClient();

        var pageResponse = await client.GetAsync("/Admin/Developer/Invocations");
        var html = await pageResponse.Content.ReadAsStringAsync();

        pageResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        // 默认密钥应直接来自缓存中的启用项。
        html.Should().Contain("value=\"debug-key\"");
        // 调试模型清单应包含已启用路由的对外模型名。
        html.Should().Contain("debug-model");
    }

    /// <summary>
    /// 验证新增/启停访问密钥后，调用调试页能立即拿到新的默认密钥（缓存失效）。
    /// </summary>
    [Fact]
    public async Task Get_invocations_page_refreshes_default_access_key_after_invalidation()
    {
        await using var factory = new DeveloperInvocationsWebApplicationFactory(true, new DeveloperInvocationsFakeProxyForwardService());
        using var client = factory.CreateClient();

        // 先预热缓存。
        var firstResponse = await client.GetAsync("/Admin/Developer/Invocations");
        (await firstResponse.Content.ReadAsStringAsync()).Should().Contain("value=\"debug-key\"");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // 禁用原密钥并新增一条 KeyName 字典序更靠前的启用密钥。
            var existing = await db.ProxyAccessKeys.FirstAsync();
            existing.IsEnabled = false;
            db.ProxyAccessKeys.Add(new ProxyAccessKey
            {
                Id = Guid.NewGuid(),
                KeyName = "a-new-debug",
                PlainKey = "new-debug-key",
                AccessKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("new-debug-key"))),
                MaskedValue = "sk-***new",
                IsEnabled = true
            });
            await db.SaveChangesAsync();
        }

        // 直接清缓存以模拟 AccessKeysApiController 的失效行为。
        factory.Services.GetRequiredService<ProxyRequestMetadataCache>().InvalidateAccessKeys();

        var refreshed = await client.GetAsync("/Admin/Developer/Invocations");
        var html = await refreshed.Content.ReadAsStringAsync();

        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("value=\"new-debug-key\"");
        html.Should().NotContain("value=\"debug-key\"");
    }
}
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
            IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);
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
        SqlSugarSetup.InitializeDatabase(db.Client);

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

        db.SiteModelMappings.Add(new SiteModelMapping
        {
            Id = Guid.Parse("94949494-9494-9494-9494-949494949494"),
            SiteId = siteId,
            RemoteModelName = "debug-site-model",
            IsEnabled = true,
            MaxConcurrency = 5
        });

        db.SiteModelMappings.Add(new SiteModelMapping
        {
            Id = Guid.Parse("95959595-9595-9595-9595-959595959595"),
            SiteId = siteId,
            RemoteModelName = "debug-site-model-2",
            IsEnabled = true,
            MaxConcurrency = 0
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
