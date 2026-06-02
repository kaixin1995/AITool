using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.Proxy;

/// <summary>
/// 代理回退链路集成测试，验证按顺序 fallback 并记录每次尝试日志
/// </summary>
public sealed class ProxyFallbackFlowTests
{
    /// <summary>
    /// 验证主路由入口列表会返回入口名称和候选数量。
    /// </summary>
    [Fact]
    public async Task Get_entries_returns_master_entry_names_with_candidate_counts()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/admin/route-rules/entries");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("\"entryName\":\"chat-prod\"");
        body.Should().Contain("\"candidateCount\":2");
    }

    /// <summary>
    /// 验证新建空入口后，入口列表中能够立即看到该记录。
    /// </summary>
    [Fact]
    public async Task Post_entries_creates_empty_master_entry_visible_in_entry_list()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();

        var createResponse = await client.PostAsync(
            "/api/admin/route-rules/entries",
            new StringContent("{\"entryName\":\"auto\"}", Encoding.UTF8, "application/json"));

        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var listResponse = await client.GetAsync("/api/admin/route-rules/entries");
        var listBody = await listResponse.Content.ReadAsStringAsync();

        listBody.Should().Contain("\"entryName\":\"auto\"");
        listBody.Should().Contain("\"candidateCount\":0");
    }

    /// <summary>
    /// 验证删除入口时会一并移除该入口下的全部路由规则。
    /// </summary>
    [Fact]
    public async Task Delete_entry_removes_all_rules_for_that_master_entry()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();

        var deleteResponse = await client.PostAsync(
            "/api/admin/route-rules/entries/delete",
            new StringContent("{\"entryName\":\"chat-prod\"}", Encoding.UTF8, "application/json"));

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var remaining = await db.ProxyRouteRules.CountAsync(x => x.ExternalModelName == "chat-prod");

        remaining.Should().Be(0);
    }

    /// <summary>
    /// 验证保存路由时支持为同一入口配置多组上游模型。
    /// </summary>
    [Fact]
    public async Task Save_route_rules_accepts_multiple_upstream_model_groups()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/admin/route-rules/save",
            new StringContent(
                "{\"externalModelName\":\"chat-prod\",\"rules\":[{\"upstreamModelName\":\"gpt-5.5\",\"siteId\":\"11111111-1111-1111-1111-111111111111\",\"siteModelName\":\"gpt-5.5-a\"},{\"upstreamModelName\":\"glm-5.1\",\"siteId\":\"22222222-2222-2222-2222-222222222222\",\"siteModelName\":\"glm-5.1-a\"}]}",
                Encoding.UTF8,
                "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rules = await db.ProxyRouteRules
            .Where(x => x.ExternalModelName == "chat-prod")
            .OrderBy(x => x.Priority)
            .ToListAsync();

        rules.Should().HaveCount(2);
        rules[0].UpstreamModelName.Should().Be("gpt-5.5");
        rules[0].ModelPriority.Should().Be(0);
        rules[0].InstancePriority.Should().Be(0);
        rules[1].UpstreamModelName.Should().Be("glm-5.1");
        rules[1].ModelPriority.Should().Be(1);
        rules[1].InstancePriority.Should().Be(0);
    }

    /// <summary>
    /// 验证未传时间配置的候选规则会按全天可用保存，兼容旧页面和旧调用。
    /// </summary>
    [Fact]
    public async Task Save_route_rules_defaults_missing_availability_to_all_day()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/admin/route-rules/save",
            new StringContent(
                "{\"externalModelName\":\"chat-prod\",\"rules\":[{\"upstreamModelName\":\"gpt-5.5\",\"siteId\":\"11111111-1111-1111-1111-111111111111\",\"siteModelName\":\"gpt-5.5-a\"}]}",
                Encoding.UTF8,
                "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rule = await db.ProxyRouteRules.SingleAsync(x => x.ExternalModelName == "chat-prod");

        rule.AvailabilityMode.Should().Be("AllDay");
        rule.TimeRangesJson.Should().BeEmpty();
    }

    /// <summary>
    /// 验证时间配置保存后能从列表接口以小写字段重新读回，保证页面刷新后仍可解析。
    /// </summary>
    [Fact]
    public async Task Save_route_rules_persists_availability_time_range_for_reload()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/admin/route-rules/save",
            new StringContent(
                "{\"externalModelName\":\"chat-prod\",\"rules\":[{\"upstreamModelName\":\"gpt-5.5\",\"siteId\":\"11111111-1111-1111-1111-111111111111\",\"siteModelName\":\"gpt-5.5-a\",\"availabilityMode\":\"Unavailable\",\"timeRangesJson\":\"[{\\\"start\\\":\\\"14:00\\\",\\\"end\\\":\\\"18:59\\\"}]\"}]}",
                Encoding.UTF8,
                "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rule = await db.ProxyRouteRules.SingleAsync(x => x.ExternalModelName == "chat-prod");

        rule.AvailabilityMode.Should().Be("Unavailable");
        rule.TimeRangesJson.Should().Contain("\"start\":\"14:00\"");
        rule.TimeRangesJson.Should().Contain("\"end\":\"18:59\"");

        var listResponse = await client.GetAsync("/api/admin/route-rules/list?modelName=chat-prod");
        var listBody = await listResponse.Content.ReadAsStringAsync();

        listBody.Should().Contain("\"availabilityMode\":\"Unavailable\"");
        listBody.Should().Contain("\\\"start\\\":\\\"14:00\\\"");
        listBody.Should().Contain("\\\"end\\\":\\\"18:59\\\"");
    }

    /// <summary>
    /// 验证规则列表在首次读取后，再次保存路由仍会立即返回最新顺序。
    /// </summary>
    [Fact]
    public async Task Get_route_rule_list_refreshes_immediately_after_save()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();

        var warmupResponse = await client.GetAsync("/api/admin/route-rules/list?modelName=chat-prod");
        var warmupBody = await warmupResponse.Content.ReadAsStringAsync();
        warmupResponse.StatusCode.Should().Be(HttpStatusCode.OK, warmupBody);
        warmupBody.IndexOf("gpt-5.5-a", StringComparison.Ordinal).Should().BeLessThan(warmupBody.IndexOf("glm-5.1-a", StringComparison.Ordinal));

        var saveResponse = await client.PostAsync(
            "/api/admin/route-rules/save",
            new StringContent(
                "{\"externalModelName\":\"chat-prod\",\"rules\":[{\"upstreamModelName\":\"glm-5.1\",\"siteId\":\"22222222-2222-2222-2222-222222222222\",\"siteModelName\":\"glm-5.1-a\"},{\"upstreamModelName\":\"gpt-5.5\",\"siteId\":\"11111111-1111-1111-1111-111111111111\",\"siteModelName\":\"gpt-5.5-a\"}]}",
                Encoding.UTF8,
                "application/json"));
        saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var refreshedResponse = await client.GetAsync("/api/admin/route-rules/list?modelName=chat-prod");
        var refreshedBody = await refreshedResponse.Content.ReadAsStringAsync();
        refreshedResponse.StatusCode.Should().Be(HttpStatusCode.OK, refreshedBody);
        refreshedBody.IndexOf("glm-5.1-a", StringComparison.Ordinal).Should().BeLessThan(refreshedBody.IndexOf("gpt-5.5-a", StringComparison.Ordinal));
    }

    /// <summary>
    /// 验证路由页面包含搜索框，并且不会直接渲染调试用协议表达式。
    /// </summary>
    [Fact]
    public async Task Get_routes_page_contains_search_box_and_hides_protocol_rendering_text()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/Routes");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("搜索站点或模型");
        html.Should().NotContain("item.protocolType");
    }

    /// <summary>
    /// 验证路由规则页面会串行保存拖拽结果，避免快速拖动时旧顺序覆盖新顺序。
    /// </summary>
    [Fact]
    public async Task Get_routes_page_serializes_queue_save_requests()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/Routes");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("var _pendingRouteSave = null;");
        html.Should().Contain("var _routeSaveInFlight = false;");
        html.Should().Contain("function flushRouteSaveQueue()");
        html.Should().Contain("if (_routeSaveInFlight || !_pendingRouteSave)");
        html.Should().Contain("var saveRequest = _pendingRouteSave;");
        html.Should().Contain("function syncEntryCandidateCount(entryName, candidateCount)");
    }

    /// <summary>
    /// 验证首条路由失败后会回退到下一条路由，并完整记录每次尝试日志。
    /// </summary>
    [Fact]
    public async Task Post_chat_completions_falls_back_to_next_route_and_persists_attempt_logs()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"model\":\"chat-prod\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("success-from-second-route");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logs = await db.ProxyUsageLogs.OrderBy(x => x.AttemptIndex).ToListAsync();

        logs.Should().HaveCount(2);
        logs[0].AttemptedModel.Should().Be("gpt-5.5");
        logs[0].Status.Should().Be("fail");
        logs[0].FallbackTriggered.Should().BeTrue();
        logs[1].AttemptedModel.Should().Be("glm-5.1");
        logs[1].Status.Should().Be("success");
        logs[1].IsFinalResult.Should().BeTrue();
    }

    /// <summary>
    /// 验证客户端主动取消时，不会继续回退到后续候选模型。
    /// </summary>
    [Fact]
    public async Task Post_chat_completions_does_not_fallback_when_request_is_canceled()
    {
        var fakeForwardService = new FakeProxyForwardService
        {
            ForwardResultFactory = _ => new ProxyForwardResult
            {
                Success = false,
                IsCanceled = true,
                ErrorMessage = "A task was canceled."
            }
        };
        await using var factory = new ProxyFallbackWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"model\":\"chat-prod\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fakeForwardService.Requests.Should().ContainSingle();
        fakeForwardService.Requests[0].TargetModelName.Should().Be("gpt-5.5-a");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logs = await db.ProxyUsageLogs.ToListAsync();
        logs.Should().BeEmpty();
    }

    /// <summary>
    /// 验证候选规则命中不可用时间段时，代理会直接跳过并请求下一顺位。
    /// </summary>
    [Fact]
    public async Task Post_chat_completions_skips_route_in_unavailable_time_range()
    {
        var fakeForwardService = new FakeProxyForwardService
        {
            ForwardResultFactory = _ => new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = "{\"choices\":[{\"message\":{\"content\":\"success-from-available-route\"}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2}}",
                InputTokens = 1,
                OutputTokens = 2
            }
        };
        await using var factory = new ProxyFallbackWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var firstRule = await db.ProxyRouteRules.SingleAsync(x => x.ExternalModelName == "chat-prod" && x.Priority == 0);
            firstRule.AvailabilityMode = "Unavailable";
            firstRule.TimeRangesJson = "[{\"start\":\"00:00\",\"end\":\"23:59\"}]";
            await db.SaveChangesAsync();
        }

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"model\":\"chat-prod\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        fakeForwardService.Requests.Should().ContainSingle();
        fakeForwardService.Requests[0].TargetModelName.Should().Be("glm-5.1-a");
    }

    /// <summary>
    /// 验证访问密钥被禁用后，请求模型列表会返回未授权。
    /// </summary>
    [Fact]
    public async Task Get_models_returns_unauthorized_after_access_key_is_disabled()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();

        var authorizedRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        authorizedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");

        var initialResponse = await client.SendAsync(authorizedRequest);
        var initialBody = await initialResponse.Content.ReadAsStringAsync();

        initialResponse.StatusCode.Should().Be(HttpStatusCode.OK, initialBody);
        initialBody.Should().Contain("\"id\":\"chat-prod\"");

        var toggleResponse = await client.PostAsync("/api/admin/access-keys/toggle/33333333-3333-3333-3333-333333333333", null);
        toggleResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var disabledRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        disabledRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");

        var disabledResponse = await client.SendAsync(disabledRequest);
        disabledResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// 验证删除入口后，模型列表会及时刷新并移除对应模型。
    /// </summary>
    [Fact]
    public async Task Get_models_refreshes_after_route_entry_is_deleted()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();

        var beforeRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        beforeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
        var beforeResponse = await client.SendAsync(beforeRequest);
        var beforeBody = await beforeResponse.Content.ReadAsStringAsync();

        beforeResponse.StatusCode.Should().Be(HttpStatusCode.OK, beforeBody);
        beforeBody.Should().Contain("\"id\":\"chat-prod\"");

        var deleteResponse = await client.PostAsync(
            "/api/admin/route-rules/entries/delete",
            new StringContent("{\"entryName\":\"chat-prod\"}", Encoding.UTF8, "application/json"));

        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        afterRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");
        var afterResponse = await client.SendAsync(afterRequest);
        var afterBody = await afterResponse.Content.ReadAsStringAsync();

        afterResponse.StatusCode.Should().Be(HttpStatusCode.OK, afterBody);
        afterBody.Should().NotContain("\"id\":\"chat-prod\"");
    }

    /// <summary>
    /// 验证聊天补全请求会使用当前运行时设置中的超时与重试参数。
    /// </summary>
    [Fact]
    public async Task Post_chat_completions_uses_runtime_settings_for_forward_request()
    {
        var fakeForwardService = new FakeProxyForwardService();
        await using var factory = new ProxyFallbackWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"model\":\"chat-prod\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fakeForwardService.Requests.Should().HaveCount(2);
        fakeForwardService.Requests[0].RequestTimeoutSeconds.Should().Be(9);
        fakeForwardService.Requests[0].RetryCount.Should().Be(2);
        fakeForwardService.Requests[1].RequestTimeoutSeconds.Should().Be(9);
        fakeForwardService.Requests[1].RetryCount.Should().Be(2);
    }

    /// <summary>
    /// 验证手动调整后的路由顺序会被保存，并被后续请求直接采用。
    /// </summary>
    [Fact]
    public async Task Save_route_rules_persists_latest_manual_order_used_by_followup_request()
    {
        var fakeForwardService = new FakeProxyForwardService();
        await using var factory = new ProxyFallbackWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var saveResponse = await client.PostAsync(
            "/api/admin/route-rules/save",
            new StringContent(
                "{\"externalModelName\":\"chat-prod\",\"rules\":[{\"upstreamModelName\":\"glm-5.1\",\"siteId\":\"22222222-2222-2222-2222-222222222222\",\"siteModelName\":\"glm-5.1-a\"},{\"upstreamModelName\":\"gpt-5.5\",\"siteId\":\"11111111-1111-1111-1111-111111111111\",\"siteModelName\":\"gpt-5.5-a\"}]}",
                Encoding.UTF8,
                "application/json"));

        saveResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"model\":\"chat-prod\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fakeForwardService.Requests.Should().HaveCount(2);
        fakeForwardService.Requests[0].TargetModelName.Should().Be("glm-5.1-a");
    }

    /// <summary>
    /// 验证同一个站点可以在同一入口中配置多条不同的候选规则。
    /// </summary>
    [Fact]
    public async Task Save_route_rules_allows_same_site_to_appear_multiple_times()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/admin/route-rules/save",
            new StringContent(
                "{\"externalModelName\":\"chat-prod\",\"rules\":[{\"upstreamModelName\":\"gpt-5.5\",\"siteId\":\"11111111-1111-1111-1111-111111111111\",\"siteModelName\":\"gpt-5.5-a\"},{\"upstreamModelName\":\"gpt-5.5\",\"siteId\":\"11111111-1111-1111-1111-111111111111\",\"siteModelName\":\"gpt-5.5-b\"}]}",
                Encoding.UTF8,
                "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rules = await db.ProxyRouteRules
            .Where(x => x.ExternalModelName == "chat-prod")
            .OrderBy(x => x.Priority)
            .ToListAsync();

        rules.Should().HaveCount(2);
        rules[0].SiteId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        rules[1].SiteId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        rules[0].SiteModelName.Should().Be("gpt-5.5-a");
        rules[1].SiteModelName.Should().Be("gpt-5.5-b");
    }

    /// <summary>
    /// 验证 OpenAI 流式透传会原样返回 SSE，并正确记录用量。
    /// </summary>
    [Fact]
    public async Task Post_chat_completions_stream_passthroughs_openai_sse_and_records_usage()
    {
        var fakeForwardService = new FakeProxyForwardService
        {
            StreamingLines =
            [
                "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}",
                string.Empty,
                "data: {\"choices\":[{\"delta\":{\"content\":\" world\"}}],\"usage\":{\"prompt_tokens\":4,\"prompt_tokens_details\":{\"cached_tokens\":2},\"completion_tokens\":3}}",
                string.Empty,
                "data: [DONE]",
                string.Empty
            ]
        };
        await using var factory = new ProxyFallbackWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"model\":\"chat-prod\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        fakeForwardService.Requests.Should().ContainSingle();
        fakeForwardService.Requests[0].PreparedRequestBody.Should().Contain("\"stream_options\":{\"include_usage\":true}");
        body.Should().Contain("\"content\":\"Hello\"");
        body.Should().Contain("\"content\":\" world\"");
        body.Should().Contain("\"completion_tokens\":3");
        body.Should().Contain("data: [DONE]");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logs = await db.ProxyUsageLogs.OrderBy(x => x.AttemptIndex).ToListAsync();

        logs.Should().ContainSingle();
        logs[0].Status.Should().Be("success");
        logs[0].IsStreaming.Should().BeTrue();
        logs[0].InputTokens.Should().Be(4);
        logs[0].CachedTokens.Should().Be(2);
        logs[0].OutputTokens.Should().Be(3);
    }

    /// <summary>
    /// 验证流式响应一旦写出首个分片，即使后续中断也不会再回退到下一条路由。
    /// </summary>
    [Fact]
    public async Task Post_chat_completions_stream_does_not_fallback_after_first_chunk_is_written_then_interrupted()
    {
        var fakeForwardService = new FakeProxyForwardService
        {
            StreamingLines =
            [
                "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}",
                string.Empty
            ],
            StreamingResultFactory = _ => new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                IsStreaming = true,
                HasStartedStreaming = true
            }
        };
        await using var factory = new ProxyFallbackWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"model\":\"chat-prod\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("partial");
        fakeForwardService.Requests.Should().ContainSingle();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logs = await db.ProxyUsageLogs.OrderBy(x => x.AttemptIndex).ToListAsync();

        logs.Should().ContainSingle();
        logs[0].AttemptedModel.Should().Be("gpt-5.5");
        logs[0].Status.Should().Be("fail");
        logs[0].IsStreamInterrupted.Should().BeTrue();
        logs[0].FallbackTriggered.Should().BeTrue();
    }
}

/// <summary>
/// 用于构建 ProxyFallbackWebApplicationFactory 对应的测试宿主，并准备隔离的测试数据。
/// </summary>
internal sealed class ProxyFallbackWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// 保存当前测试使用的临时数据库文件路径。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-proxy-fallback-{Guid.NewGuid():N}.db");
    /// <summary>
    /// 保存当前测试使用的伪造转发服务。
    /// </summary>
    private readonly FakeProxyForwardService _fakeForwardService;

    /// <summary>
    /// 初始化代理回退测试宿主。
    /// </summary>
    public ProxyFallbackWebApplicationFactory()
        : this(new FakeProxyForwardService())
    {
    }

    /// <summary>
    /// 初始化代理回退测试宿主。
    /// </summary>
    public ProxyFallbackWebApplicationFactory(FakeProxyForwardService fakeForwardService)
    {
        _fakeForwardService = fakeForwardService;
    }

    /// <summary>
    /// 重写测试宿主依赖，接入隔离数据库和伪造转发服务。
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
    /// 在客户端配置完成后执行测试数据初始化。
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

        var firstSite = new Site
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "Primary OpenAI",
            BaseUrl = "https://invalid-primary.example.com",
            ApiKey = "upstream-key-1",
            ProtocolType = "OpenAI",
            SupportsOpenAi = true,
            SupportsAnthropic = false,
            IsEnabled = true
        };
        var secondSite = new Site
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Name = "Fallback GLM",
            BaseUrl = "https://invalid-fallback.example.com",
            ApiKey = "upstream-key-2",
            ProtocolType = "OpenAI",
            SupportsOpenAi = true,
            SupportsAnthropic = false,
            IsEnabled = true
        };
        var thirdSite = new Site
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Name = "Primary OpenAI Replica",
            BaseUrl = "https://invalid-replica.example.com",
            ApiKey = "upstream-key-3",
            ProtocolType = "OpenAI",
            SupportsOpenAi = true,
            SupportsAnthropic = false,
            IsEnabled = true
        };

        var accessKeyRaw = "test-key";
        var accessKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessKeyRaw)));
        var proxyAccessKey = new ProxyAccessKey
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            KeyName = "integration",
            AccessKeyHash = accessKeyHash,
            MaskedValue = "sk-***key",
            IsEnabled = true
        };

        var routeRules = new[]
        {
            new ProxyRouteRule
            {
                ExternalModelName = "chat-prod",
                UpstreamModelName = "gpt-5.5",
                SiteId = firstSite.Id,
                SiteModelName = "gpt-5.5-a",
                Priority = 0,
                ModelPriority = 0,
                InstancePriority = 0,
                IsEnabled = true
            },
            new ProxyRouteRule
            {
                ExternalModelName = "chat-prod",
                UpstreamModelName = "glm-5.1",
                SiteId = secondSite.Id,
                SiteModelName = "glm-5.1-a",
                Priority = 1,
                ModelPriority = 1,
                InstancePriority = 0,
                IsEnabled = true
            }
        };

        db.Sites.AddRange(firstSite, secondSite, thirdSite);
        db.ProxyAccessKeys.Add(proxyAccessKey);
        db.ProxyRouteEntries.Add(new ProxyRouteEntry
        {
            EntryName = "chat-prod"
        });
        db.SiteModelMappings.AddRange(
            new SiteModelMapping
            {
                SiteId = firstSite.Id,
                ModelLibraryItemId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                RemoteModelName = "gpt-5.5-a",
                LastStatus = "ok",
                IsEnabled = true
            },
            new SiteModelMapping
            {
                SiteId = thirdSite.Id,
                ModelLibraryItemId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                RemoteModelName = "gpt-5.5-b",
                LastStatus = "ok",
                IsEnabled = true
            },
            new SiteModelMapping
            {
                SiteId = secondSite.Id,
                ModelLibraryItemId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                RemoteModelName = "glm-5.1-a",
                LastStatus = "ok",
                IsEnabled = true
            });
        db.ProxyRouteRules.AddRange(routeRules);
        db.SystemRuntimeSettings.Add(new AITool.Domain.Operations.SystemRuntimeSettings
        {
            Id = 1,
            ProxyRequestTimeoutSeconds = 9,
            ProxyRetryCount = 2,
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

    /// <summary>
    /// 释放测试过程中创建的资源。
    /// </summary>
    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
    }
}

/// <summary>
/// 用于模拟代理转发结果，支撑 FakeProxyForwardService 相关断言。
/// </summary>
internal sealed class FakeProxyForwardService : IProxyForwardService
{
    /// <summary>
    /// 记录当前测试中已经发生的转发尝试次数。
    /// </summary>
    private int _attemptCount;

    /// <summary>
    /// 保存测试期间捕获到的转发请求。
    /// </summary>
    public List<ProxyForwardRequest> Requests { get; } = new();
    /// <summary>
    /// 保存测试时返回的流式响应片段。
    /// </summary>
    public List<string>? StreamingLines { get; set; }
    /// <summary>
    /// 允许按请求动态生成非流式转发结果，便于模拟首路由失败等场景。
    /// </summary>
    public Func<ProxyForwardRequest, ProxyForwardResult>? ForwardResultFactory { get; set; }
    /// <summary>
    /// 允许按请求动态生成流式转发结果，便于覆盖中断与透传场景。
    /// </summary>
    public Func<ProxyForwardRequest, ProxyForwardResult>? StreamingResultFactory { get; set; }

    /// <summary>
    /// 使用固定的两次结果模拟主路由失败、备路由成功的回退链路
    /// </summary>
    public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(CloneRequest(request));

        var customResult = ForwardResultFactory?.Invoke(request);
        if (customResult is not null)
        {
            return Task.FromResult(customResult);
        }

        _attemptCount++;
        if (_attemptCount == 1)
        {
            return Task.FromResult(new ProxyForwardResult
            {
                Success = false,
                StatusCode = 500,
                ErrorMessage = "first route failed"
            });
        }

        return Task.FromResult(new ProxyForwardResult
        {
            Success = true,
            StatusCode = 200,
            ResponseBody = "{\"choices\":[{\"message\":{\"content\":\"success-from-second-route\"}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2}}",
            InputTokens = 1,
            OutputTokens = 2
        });
    }

    /// <summary>
    /// 模拟流式转发过程，并按测试设定回放 OpenAI SSE 分片。
    /// </summary>
    public async Task<ProxyForwardResult> ForwardStreamingAsync(
        ProxyForwardRequest request,
        Func<string, CancellationToken, Task> onSseDataAsync,
        CancellationToken cancellationToken = default)
    {
        if (StreamingLines is not null || StreamingResultFactory is not null)
        {
            Requests.Add(CloneRequest(request));
            var lines = StreamingLines ?? [];
            foreach (var line in lines)
            {
                await onSseDataAsync(line, cancellationToken);
            }

            var streamingResult = StreamingResultFactory?.Invoke(request) ?? new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = string.Join("\n", lines),
                IsStreaming = true,
                HasStartedStreaming = lines.Any(line => !string.IsNullOrEmpty(line))
            };
            streamingResult.ResponseBody = string.IsNullOrWhiteSpace(streamingResult.ResponseBody)
                ? string.Join("\n", lines)
                : streamingResult.ResponseBody;
            streamingResult.IsStreaming = true;
            return streamingResult;
        }

        var result = await ForwardAsync(request, cancellationToken);
        foreach (var line in result.ResponseBody.Replace("\r\n", "\n").Split('\n'))
        {
            await onSseDataAsync(line, cancellationToken);
        }

        result.IsStreaming = true;
        return result;
    }

    /// <summary>
    /// 复制转发请求的关键字段，避免测试断言读到被后续修改的引用。
    /// </summary>
    private static ProxyForwardRequest CloneRequest(ProxyForwardRequest request)
    {
        return new ProxyForwardRequest
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
            TargetPath = request.TargetPath,
            ForwardHeaders = new Dictionary<string, string>(request.ForwardHeaders, StringComparer.OrdinalIgnoreCase)
        };
    }
}
