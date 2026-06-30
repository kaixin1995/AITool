using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.Proxy;

/// <summary>
/// Anthropic 代理入口集成测试，验证鉴权、缓存失效和运行时设置都会按真实入口生效。
/// </summary>
public sealed class AnthropicProxyControllerTests
{
    /// <summary>
    /// 验证 Anthropic 客户端获取模型列表时会返回兼容格式的数据。
    /// </summary>
    [Fact]
    public async Task Get_models_returns_anthropic_model_list()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService();
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Add("x-api-key", "anthropic-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("data")[0].GetProperty("id").GetString().Should().Be("claude-proxy");
        document.RootElement.GetProperty("data")[0].GetProperty("type").GetString().Should().Be("model");
        document.RootElement.GetProperty("has_more").GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// 验证 count_tokens 接口会返回基于请求文本估算出的输入 token 数量。
    /// </summary>
    [Fact]
    public async Task Post_count_tokens_returns_estimated_input_tokens()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService();
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages/count_tokens")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"system\":\"You are helpful\",\"messages\":[{\"role\":\"user\",\"content\":\"hello world\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("input_tokens").GetInt32().Should().BeGreaterThan(0);
    }

    /// <summary>
    /// 验证 Messages 接口会使用 x-api-key 鉴权，并把运行时参数传给转发层。
    /// </summary>
    [Fact]
    public async Task Post_messages_uses_x_api_key_and_runtime_settings_for_forward_request()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService();
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":128,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        fakeForwardService.Requests.Should().ContainSingle();
        fakeForwardService.Requests[0].ProtocolType.Should().Be("Anthropic");
        fakeForwardService.Requests[0].TargetModelName.Should().Be("claude-3-7-sonnet-real");
        fakeForwardService.Requests[0].RequestTimeoutSeconds.Should().Be(11);
        fakeForwardService.Requests[0].RetryCount.Should().Be(4);

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("anthropic-proxy-ok");
    }

    /// <summary>
    /// 验证 Bearer 鉴权和 Anthropic 协议头都会被正确透传。
    /// </summary>
    [Fact]
    public async Task Post_messages_accepts_bearer_key_and_forwards_anthropic_headers()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService();
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":128,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new global::System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "anthropic-test-key");
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Add("anthropic-beta", "token-counting-2024-11-01");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fakeForwardService.Requests.Should().ContainSingle();
        fakeForwardService.Requests[0].ForwardHeaders["anthropic-version"].Should().Be("2023-06-01");
        fakeForwardService.Requests[0].ForwardHeaders["anthropic-beta"].Should().Be("token-counting-2024-11-01");
    }

    /// <summary>
    /// 验证 Anthropic 原生流式透传会返回完整事件流，并正确记录用量。
    /// </summary>
    [Fact]
    public async Task Post_messages_stream_passthroughs_anthropic_sse_and_records_usage()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService
        {
            AnthropicStreamingLines =
            [
                "event: message_start",
                "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_test\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-3-7-sonnet-real\",\"usage\":{\"input_tokens\":6,\"cache_read_input_tokens\":2,\"output_tokens\":0},\"content\":[]}}",
                string.Empty,
                "event: content_block_start",
                "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
                string.Empty,
                "event: content_block_delta",
                "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"anthropic-direct-stream\"}}",
                string.Empty,
                "event: content_block_stop",
                "data: {\"type\":\"content_block_stop\",\"index\":0}",
                string.Empty,
                "event: message_delta",
                "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":5},\"delta\":{\"stop_reason\":\"end_turn\"}}",
                string.Empty,
                "event: message_stop",
                "data: {\"type\":\"message_stop\"}",
                string.Empty
            ]
        };
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService, "Anthropic");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":128,\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        fakeForwardService.Requests.Should().ContainSingle();
        fakeForwardService.Requests[0].ProtocolType.Should().Be("Anthropic");
        fakeForwardService.Requests[0].EnableStreaming.Should().BeTrue();
        body.Should().Contain("event: message_start");
        body.Should().Contain("anthropic-direct-stream");
        body.Should().Contain("event: message_delta");
        body.Should().Contain("event: message_stop");

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logs = await db.ProxyUsageLogs.OrderBy(x => x.AttemptIndex).ToListAsync();

        logs.Should().ContainSingle();
        logs[0].Status.Should().Be("success");
        logs[0].IsStreaming.Should().BeTrue();
        logs[0].InputTokens.Should().Be(6);
        logs[0].CachedTokens.Should().Be(2);
        logs[0].OutputTokens.Should().Be(5);
    }

    /// <summary>
    /// 验证 OpenAI 流式桥接到 Anthropic 时会保留仅包含换行的分片内容。
    /// </summary>
    [Fact]
    public async Task Post_messages_preserves_whitespace_only_stream_chunks_when_bridging_openai_stream()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService
        {
            OpenAiStreamingLines =
            [
                "data: {\"choices\":[{\"delta\":{\"content\":\"```bash\"}}]}",
                string.Empty,
                "data: {\"choices\":[{\"delta\":{\"content\":\"\\n\"}}]}",
                string.Empty,
                "data: {\"choices\":[{\"delta\":{\"content\":\"curl test\"}}],\"usage\":{\"prompt_tokens\":6,\"prompt_tokens_details\":{\"cached_tokens\":0},\"completion_tokens\":2}}",
                string.Empty,
                "data: [DONE]",
                string.Empty
            ]
        };
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService, "OpenAI");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":128,\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("\\u0060\\u0060\\u0060bash");
        body.Should().Contain("\"text\":\"\\n\"");
        body.Should().Contain("curl test");
        body.Should().NotContain("bashcurl");
    }

    /// <summary>
    /// 验证 OpenAI 流式桥接到 Anthropic 时会保留仅包含空格的分片内容。
    /// </summary>
    [Fact]
    public async Task Post_messages_preserves_space_only_stream_chunks_when_bridging_openai_stream()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService
        {
            OpenAiStreamingLines =
            [
                "data: {\"choices\":[{\"delta\":{\"content\":\"A\"}}]}",
                string.Empty,
                "data: {\"choices\":[{\"delta\":{\"content\":\" \"}}]}",
                string.Empty,
                "data: {\"choices\":[{\"delta\":{\"content\":\"B\"}}],\"usage\":{\"prompt_tokens\":6,\"prompt_tokens_details\":{\"cached_tokens\":0},\"completion_tokens\":2}}",
                string.Empty,
                "data: [DONE]",
                string.Empty
            ]
        };
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService, "OpenAI");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":128,\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("\"text\":\" \"");
        body.Should().NotContain("\"text\":\"AB\"");
    }

    /// <summary>
    /// 验证访问密钥被禁用后，Messages 请求会返回未授权。
    /// </summary>
    [Fact]
    public async Task Post_messages_returns_unauthorized_after_access_key_is_disabled()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService();
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var initialResponse = await SendMessagesAsync(client);
        initialResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var toggleResponse = await client.PostAsync("/api/admin/access-keys/toggle/99999999-9999-9999-9999-999999999999", null);
        toggleResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var disabledResponse = await SendMessagesAsync(client);
        disabledResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// 验证删除路由入口后，再次请求会返回找不到可用路由。
    /// </summary>
    [Fact]
    public async Task Post_messages_returns_not_found_after_route_entry_is_deleted()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService();
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var initialResponse = await SendMessagesAsync(client);
        initialResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var deleteResponse = await client.PostAsync(
            "/api/admin/route-rules/entries/delete",
            new StringContent("{\"entryName\":\"claude-proxy\"}", Encoding.UTF8, "application/json"));
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterDeleteResponse = await SendMessagesAsync(client);
        var body = await afterDeleteResponse.Content.ReadAsStringAsync();

        afterDeleteResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        body.Should().Contain("没有可用的路由");
    }

    /// <summary>
    /// 验证混合路由场景下，请求会按配置顺序尝试并最终切换到可用的 Anthropic 直连流。
    /// </summary>
    [Fact]
    public async Task Post_messages_prefers_anthropic_route_before_openai_bridge_when_available()
    {
        var fakeForwardService = new AnthropicFallbackStreamProxyForwardService();
        await using var factory = new AnthropicProxyFallbackWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":128,\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        // 当前路由顺序以后台配置为准，因此会先尝试第一条 OpenAI 兼容流，再回退到后面的 Anthropic 直连流式路由。
        fakeForwardService.StreamAttemptCount.Should().Be(2);
        fakeForwardService.NonStreamAttemptCount.Should().Be(0);
        body.Should().Contain("event: message_start");
        body.Should().Contain("anthropic-fallback-ok");
        body.Should().Contain("event: message_stop");
    }

    /// <summary>
    /// 验证 tool_result 内容块会被转换回 OpenAI 协议中的 tool 消息。
    /// </summary>
    [Fact]
    public async Task Post_messages_converts_tool_result_content_blocks_back_to_openai_tool_message()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService();
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService, "OpenAI");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":128,\"messages\":[{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_123\",\"name\":\"Glob\",\"input\":{\"path\":\"test001\"}}]},{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"toolu_123\",\"content\":[{\"type\":\"text\",\"text\":\"file-a.txt\\nfile-b.txt\"}]}]}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        fakeForwardService.Requests.Should().ContainSingle();
        fakeForwardService.Requests[0].PreparedRequestBody.Should().Contain("\"role\":\"tool\"");
        fakeForwardService.Requests[0].PreparedRequestBody.Should().Contain("\"tool_call_id\":\"toolu_123\"");
        fakeForwardService.Requests[0].PreparedRequestBody.Should().Contain("file-a.txt\\nfile-b.txt");
    }

    /// <summary>
    /// 验证 OpenAI 工具调用流能够桥接成 Anthropic 的 tool_use 事件序列。
    /// </summary>
    [Fact]
    public async Task Post_messages_bridges_openai_tool_call_stream_to_anthropic_tool_use_events()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService
        {
            OpenAiStreamingLines =
            [
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_123\",\"function\":{\"name\":\"Bash\",\"arguments\":\"{\\u0022command\\u0022:\\u0022ec\"}}]}}]}",
                string.Empty,
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"ho hi\\u0022}\"}}]},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":6,\"prompt_tokens_details\":{\"cached_tokens\":1},\"completion_tokens\":3}}",
                string.Empty,
                "data: [DONE]",
                string.Empty
            ]
        };
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService, "OpenAI");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":128,\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("\"type\":\"tool_use\"");
        body.Should().Contain("\"name\":\"Bash\"");
        body.Should().Contain("\"type\":\"input_json_delta\"");
        body.Should().Contain("\"partial_json\":\"{\\u0022command\\u0022:\\u0022ec\"");
        body.Should().Contain("\"partial_json\":\"ho hi\\u0022}\"");
        body.Should().Contain("\"stop_reason\":\"tool_use\"");
        body.Should().Contain("event: content_block_stop");
        body.Should().Contain("event: message_stop");
    }

    /// <summary>
    /// 验证多个 OpenAI 工具调用增量可以分别桥接成对应的 Anthropic 工具事件。
    /// </summary>
    [Fact]
    public async Task Post_messages_bridges_multiple_openai_tool_call_deltas_to_anthropic_tool_use_events()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService
        {
            OpenAiStreamingLines =
            [
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"Glob\",\"arguments\":\"{\\u0022path\\u0022:\\u0022src\"}},{\"index\":1,\"id\":\"call_2\",\"function\":{\"name\":\"Read\",\"arguments\":\"{\\u0022file_path\\u0022:\\u0022README\"}}]}}]}",
                string.Empty,
                "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\u0022}\"}},{\"index\":1,\"function\":{\"arguments\":\".md\\u0022}\"}}]},\"finish_reason\":\"tool_calls\"}],\"usage\":{\"prompt_tokens\":6,\"prompt_tokens_details\":{\"cached_tokens\":1},\"completion_tokens\":4}}",
                string.Empty,
                "data: [DONE]",
                string.Empty
            ]
        };
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService, "OpenAI");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":128,\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("\"name\":\"Glob\"");
        body.Should().Contain("\"name\":\"Read\"");
        body.Should().Contain("\"partial_json\":\"{\\u0022path\\u0022:\\u0022src\"");
        body.Should().Contain("\"partial_json\":\"{\\u0022file_path\\u0022:\\u0022README\"");
        body.Should().Contain("event: message_stop");
    }

    /// <summary>
    /// 验证带多行结构的 OpenAI SSE 块能够正确桥接成 Anthropic 事件流。
    /// </summary>
    [Fact]
    public async Task Post_messages_bridges_multiline_openai_sse_block_to_anthropic_events()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService
        {
            OpenAiStreamingLines =
            [
                "event: message",
                "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}",
                string.Empty,
                "event: message",
                "data: {\"choices\":[{\"delta\":{\"content\":\" world\"}}],\"usage\":{\"prompt_tokens\":6,\"prompt_tokens_details\":{\"cached_tokens\":1},\"completion_tokens\":2}}",
                string.Empty,
                "data: [DONE]",
                string.Empty
            ]
        };
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService, "OpenAI");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":128,\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("event: message_start");
        body.Should().Contain("\"text\":\"Hello\"");
        body.Should().Contain("\"text\":\" world\"");
        body.Should().Contain("event: message_stop");
    }

    /// <summary>
    /// 验证多个 Anthropic 文本块和思考块会合并成 OpenAI 响应内容。
    /// </summary>
    [Fact]
    public async Task Post_messages_accumulates_multiple_anthropic_text_and_thinking_blocks_into_openai_response()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService
        {
            ForwardResultFactory = _ => new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = "{\"id\":\"msg_multi\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"step-1\"},{\"type\":\"thinking\",\"thinking\":\"step-2\"},{\"type\":\"text\",\"text\":\"hello\"},{\"type\":\"text\",\"text\":\" world\"}],\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":6,\"cache_read_input_tokens\":1,\"output_tokens\":3}}",
                InputTokens = 6,
                CachedTokens = 1,
                OutputTokens = 3
            }
        };
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService, "Anthropic");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new global::System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "anthropic-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Should().Be("hello world");
        document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("reasoning_content").GetString().Should().Be("step-1\nstep-2");
    }

    /// <summary>
    /// 验证即使上游返回的是完整 OpenAI 响应对象，也能桥接成 Anthropic 事件流。
    /// </summary>
    [Fact]
    public async Task Post_messages_bridges_non_chunked_openai_stream_response_to_anthropic_events()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService
        {
            OpenAiStreamingLines =
            [
                "data: {\"id\":\"chatcmpl_full\",\"choices\":[{\"message\":{\"content\":\"full-response\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":6,\"prompt_tokens_details\":{\"cached_tokens\":1},\"completion_tokens\":2}}",
                string.Empty
            ]
        };
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService, "OpenAI");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":128,\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("event: message_start");
        body.Should().Contain("event: content_block_start");
        body.Should().Contain("\"type\":\"text_delta\"");
        body.Should().Contain("full-response");
        body.Should().Contain("\"stop_reason\":\"end_turn\"");
        body.Should().Contain("event: message_stop");
    }

    /// <summary>
    /// 验证首个 OpenAI choice 为空时，会回退到后续非空 choice 作为结果。
    /// </summary>
    [Fact]
    public async Task Post_messages_prefers_non_empty_openai_choice_when_first_choice_is_empty()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService
        {
            ForwardResultFactory = _ => new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = "{\"choices\":[{\"message\":{\"content\":\"\"},\"finish_reason\":\"stop\"},{\"message\":{\"content\":\"openai-second-choice\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":6,\"prompt_tokens_details\":{\"cached_tokens\":2},\"completion_tokens\":9}}",
                InputTokens = 6,
                CachedTokens = 2,
                OutputTokens = 9
            }
        };
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService, "OpenAI");
        using var client = factory.CreateClient();

        var response = await SendMessagesAsync(client);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("openai-second-choice");
    }

    /// <summary>
    /// 验证多 choice 的 OpenAI 流式内容能够桥接成 Anthropic 事件流。
    /// </summary>
    [Fact]
    public async Task Post_messages_bridges_multi_choice_openai_stream_content_to_anthropic_events()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService
        {
            OpenAiStreamingLines =
            [
                "data: {\"choices\":[{\"index\":0,\"delta\":{}},{\"index\":1,\"delta\":{\"content\":\"from-second-choice\"}}],\"usage\":{\"prompt_tokens\":6,\"prompt_tokens_details\":{\"cached_tokens\":1},\"completion_tokens\":2}}",
                string.Empty,
                "data: [DONE]",
                string.Empty
            ]
        };
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService, "OpenAI");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":128,\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain("from-second-choice");
        body.Should().Contain("event: message_stop");
    }

    /// <summary>
    /// 验证仅存在 OpenAI 站点时，Anthropic 入口会自动走兼容转发。
    /// </summary>
    [Fact]
    public async Task Post_messages_bridges_to_openai_route_when_only_openai_site_exists()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService();
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService, "OpenAI");
        using var client = factory.CreateClient();

        var response = await SendMessagesAsync(client);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        fakeForwardService.Requests.Should().ContainSingle();
        fakeForwardService.Requests[0].ProtocolType.Should().Be("OpenAI");
        fakeForwardService.Requests[0].PreparedRequestBody.Should().Contain("\"model\":\"claude-3-7-sonnet-real\"");

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("type").GetString().Should().Be("message");
        document.RootElement.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("openai-bridged-ok");
        document.RootElement.GetProperty("usage").GetProperty("input_tokens").GetInt32().Should().Be(6);
        document.RootElement.GetProperty("usage").GetProperty("cache_read_input_tokens").GetInt32().Should().Be(2);
        document.RootElement.GetProperty("usage").GetProperty("output_tokens").GetInt32().Should().Be(9);
    }

    /// <summary>
    /// 验证仅存在 Responses 站点时，Anthropic 入口会自动走 Responses 兼容转发。
    /// </summary>
    [Fact]
    public async Task Post_messages_bridges_to_responses_route_when_only_responses_site_exists()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService
        {
            ForwardResultFactory = static request => new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = "{\"id\":\"resp_test\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"model\":\"gpt-4.1-real\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"responses-bridged-ok\"}]}],\"usage\":{\"input_tokens\":6,\"input_tokens_details\":{\"cached_tokens\":2},\"output_tokens\":9,\"total_tokens\":15}}",
                InputTokens = 6,
                CachedTokens = 2,
                OutputTokens = 9
            }
        };
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService, "Responses");
        using var client = factory.CreateClient();

        var response = await SendMessagesAsync(client);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        fakeForwardService.Requests.Should().ContainSingle();
        fakeForwardService.Requests[0].ProtocolType.Should().Be("Responses");
        fakeForwardService.Requests[0].TargetPath.Should().Be("/v1/responses");
        fakeForwardService.Requests[0].PreparedRequestBody.Should().Contain("\"input\"");
        fakeForwardService.Requests[0].PreparedRequestBody.Should().Contain("\"max_output_tokens\":64");

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("type").GetString().Should().Be("message");
        document.RootElement.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("responses-bridged-ok");
        document.RootElement.GetProperty("usage").GetProperty("input_tokens").GetInt32().Should().Be(6);
        document.RootElement.GetProperty("usage").GetProperty("cache_read_input_tokens").GetInt32().Should().Be(2);
        document.RootElement.GetProperty("usage").GetProperty("output_tokens").GetInt32().Should().Be(9);
    }

    /// <summary>
    /// 验证 Responses 流式事件能够实时转换为 Anthropic SSE。
    /// </summary>
    [Fact]
    public async Task Post_messages_stream_bridges_responses_events_to_anthropic_sse()
    {
        var fakeForwardService = new AnthropicFakeProxyForwardService
        {
            OpenAiStreamingLines =
            [
                "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\"}}]}",
                string.Empty,
                "data: {\"choices\":[{\"delta\":{\"content\":\"responses-anthropic-stream\"}}],\"usage\":{\"prompt_tokens\":6,\"prompt_tokens_details\":{\"cached_tokens\":2},\"completion_tokens\":9}}",
                string.Empty,
                "data: [DONE]",
                string.Empty
            ]
        };
        await using var factory = new AnthropicProxyWebApplicationFactory(fakeForwardService, "Responses");
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":128,\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        fakeForwardService.Requests.Should().ContainSingle();
        fakeForwardService.Requests[0].ProtocolType.Should().Be("Responses");
        fakeForwardService.Requests[0].TargetPath.Should().Be("/v1/responses");
        fakeForwardService.Requests[0].EnableStreaming.Should().BeTrue();
        body.Should().Contain("event: message_start");
        body.Should().Contain("responses-anthropic-stream");
        body.Should().Contain("event: message_delta");
        body.Should().Contain("event: message_stop");
    }

    /// <summary>
    /// 发送一条默认的 Anthropic Messages 请求，便于复用基础测试输入。
    /// </summary>
    private static Task<HttpResponseMessage> SendMessagesAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{\"model\":\"claude-proxy\",\"max_tokens\":64,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", "anthropic-test-key");
        return client.SendAsync(request);
    }
}

/// <summary>
/// 构建 Anthropic 代理集成测试的 WebApplicationFactory，使用隔离数据库和伪造转发服务。
/// </summary>
internal sealed class AnthropicProxyWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// 保存当前测试使用的临时数据库文件路径。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-anthropic-proxy-{Guid.NewGuid():N}.db");
    /// <summary>
    /// 保存当前测试使用的伪造转发服务。
    /// </summary>
    private readonly AnthropicFakeProxyForwardService _fakeForwardService;
    /// <summary>
    /// 保存当前测试站点声明的协议类型。
    /// </summary>
    private readonly string _siteProtocol;

    /// <summary>
    /// 初始化 Anthropic 代理测试宿主。
    /// </summary>
    public AnthropicProxyWebApplicationFactory(AnthropicFakeProxyForwardService fakeForwardService, string siteProtocol = "Anthropic")
    {
        _fakeForwardService = fakeForwardService;
        _siteProtocol = siteProtocol;
    }

    /// <summary>
    /// 重写测试宿主依赖，接入隔离数据库和伪造转发服务。
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
        SqlSugarSetup.InitializeDatabase(db.Client);

        var siteId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var accessKeyRaw = "anthropic-test-key";
        var accessKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessKeyRaw)));

        db.Sites.Add(new Site
        {
            Id = siteId,
            Name = "Anthropic Proxy Site",
            BaseUrl = "https://anthropic-proxy.example.com",
            ApiKey = "site-anthropic-key",
            ProtocolType = _siteProtocol,
            SupportsOpenAi = string.Equals(_siteProtocol, "OpenAI", StringComparison.OrdinalIgnoreCase),
            SupportsAnthropic = string.Equals(_siteProtocol, "Anthropic", StringComparison.OrdinalIgnoreCase),
            IsEnabled = true
        });

        db.ProxyAccessKeys.Add(new ProxyAccessKey
        {
            Id = Guid.Parse("99999999-9999-9999-9999-999999999999"),
            KeyName = "anthropic",
            PlainKey = accessKeyRaw,
            AccessKeyHash = accessKeyHash,
            MaskedValue = "sk-***ropic",
            IsEnabled = true
        });

        db.ProxyRouteEntries.Add(new ProxyRouteEntry
        {
            EntryName = "claude-proxy"
        });

        db.ProxyRouteRules.Add(new ProxyRouteRule
        {
            Id = Guid.Parse("88888888-8888-8888-8888-888888888888"),
            ExternalModelName = "claude-proxy",
            UpstreamModelName = "claude-3-7-sonnet",
            SiteId = siteId,
            SiteModelName = "claude-3-7-sonnet-real",
            Priority = 0,
            ModelPriority = 0,
            InstancePriority = 0,
            IsEnabled = true
        });

        // 单例表 Id=1 可能已被启动逻辑创建，先删除避免唯一约束冲突。


        db.Client.Deleteable<SystemRuntimeSettings>().Where(x => x.Id == 1).ExecuteCommand();


        db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            ProxyRequestTimeoutSeconds = 11,
            ProxyRetryCount = 4,
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
/// 构建混合路由回退场景的 WebApplicationFactory，先配一条 OpenAI 站点再配一条 Anthropic 站点。
/// </summary>
internal sealed class AnthropicProxyFallbackWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// 保存当前测试使用的临时数据库文件路径。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-anthropic-proxy-fallback-{Guid.NewGuid():N}.db");
    /// <summary>
    /// 保存混合路由回退场景下使用的伪造转发服务。
    /// </summary>
    private readonly AnthropicFallbackStreamProxyForwardService _fakeForwardService;

    /// <summary>
    /// 初始化混合路由回退测试宿主。
    /// </summary>
    public AnthropicProxyFallbackWebApplicationFactory(AnthropicFallbackStreamProxyForwardService fakeForwardService)
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
            IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);
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
        SqlSugarSetup.InitializeDatabase(db.Client);

        var openAiSiteId = Guid.Parse("71717171-7171-7171-7171-717171717171");
        var anthropicSiteId = Guid.Parse("72727272-7272-7272-7272-727272727272");
        var accessKeyRaw = "anthropic-test-key";
        var accessKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessKeyRaw)));

        db.Sites.AddRange(
            new Site
            {
                Id = openAiSiteId,
                Name = "OpenAI First",
                BaseUrl = "https://openai-first.example.com",
                ApiKey = "site-openai-key",
                ProtocolType = "OpenAI",
                SupportsOpenAi = true,
                SupportsAnthropic = false,
                IsEnabled = true
            },
            new Site
            {
                Id = anthropicSiteId,
                Name = "Anthropic Second",
                BaseUrl = "https://anthropic-second.example.com",
                ApiKey = "site-anthropic-key",
                ProtocolType = "Anthropic",
                SupportsOpenAi = false,
                SupportsAnthropic = true,
                IsEnabled = true
            });

        db.ProxyAccessKeys.Add(new ProxyAccessKey
        {
            Id = Guid.Parse("99999999-9999-9999-9999-999999999999"),
            KeyName = "anthropic",
            PlainKey = accessKeyRaw,
            AccessKeyHash = accessKeyHash,
            MaskedValue = "sk-***ropic",
            IsEnabled = true
        });

        db.ProxyRouteEntries.Add(new ProxyRouteEntry
        {
            EntryName = "claude-proxy"
        });

        db.ProxyRouteRules.AddRange(
            new ProxyRouteRule
            {
                Id = Guid.Parse("73737373-7373-7373-7373-737373737373"),
                ExternalModelName = "claude-proxy",
                UpstreamModelName = "claude-openai-primary",
                SiteId = openAiSiteId,
                SiteModelName = "claude-openai-primary-real",
                Priority = 0,
                ModelPriority = 0,
                InstancePriority = 0,
                IsEnabled = true
            },
            new ProxyRouteRule
            {
                Id = Guid.Parse("74747474-7474-7474-7474-747474747474"),
                ExternalModelName = "claude-proxy",
                UpstreamModelName = "claude-anthropic-secondary",
                SiteId = anthropicSiteId,
                SiteModelName = "claude-anthropic-secondary-real",
                Priority = 1,
                ModelPriority = 1,
                InstancePriority = 1,
                IsEnabled = true
            });

        // 单例表 Id=1 可能已被启动逻辑创建，先删除避免唯一约束冲突。


        db.Client.Deleteable<SystemRuntimeSettings>().Where(x => x.Id == 1).ExecuteCommand();


        db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            ProxyRequestTimeoutSeconds = 11,
            ProxyRetryCount = 0,
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
/// 伪造代理转发服务，用于验证 Anthropic 入口的鉴权、参数传递和协议转换。
/// </summary>
internal sealed class AnthropicFakeProxyForwardService : IProxyForwardService
{
    /// <summary>
    /// 记录测试期间收到的转发请求。
    /// </summary>
    public List<ProxyForwardRequest> Requests { get; } = [];
    /// <summary>
    /// 保存测试时返回的 OpenAI 流式响应片段。
    /// </summary>
    public List<string>? OpenAiStreamingLines { get; set; }
    /// <summary>
    /// 保存测试时返回的 Anthropic 流式响应片段。
    /// </summary>
    public List<string>? AnthropicStreamingLines { get; set; }
    /// <summary>
    /// 保存测试时返回的 Responses 流式响应片段。
    /// </summary>
    public List<string>? ResponsesStreamingLines { get; set; }
    /// <summary>
    /// 允许按请求动态生成非流式转发结果，便于覆盖特定断言场景。
    /// </summary>
    public Func<ProxyForwardRequest, ProxyForwardResult>? ForwardResultFactory { get; set; }
    /// <summary>
    /// 允许按请求动态生成流式转发结果，便于覆盖中断、补尾等特殊场景。
    /// </summary>
    public Func<ProxyForwardRequest, ProxyForwardResult>? StreamingResultFactory { get; set; }

    /// <summary>
    /// 使用固定成功响应，验证 Anthropic 入口会把真实运行时参数传递到转发层。
    /// </summary>
    public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
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
            TargetPath = request.TargetPath,
            ForwardHeaders = new Dictionary<string, string>(request.ForwardHeaders, StringComparer.OrdinalIgnoreCase)
        });

        var directResult = ForwardResultFactory?.Invoke(request);
        if (directResult is not null)
        {
            return Task.FromResult(directResult);
        }

        if (string.Equals(request.ProtocolType, "Responses", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = "{\"id\":\"resp_default\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"model\":\"gpt-4.1-real\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"responses-anthropic-ok\"}]}],\"usage\":{\"input_tokens\":6,\"input_tokens_details\":{\"cached_tokens\":2},\"output_tokens\":9,\"total_tokens\":15}}",
                InputTokens = 6,
                CachedTokens = 2,
                OutputTokens = 9
            });
        }

        if (string.Equals(request.ProtocolType, "OpenAI", StringComparison.Ordinal))
        {
            return Task.FromResult(new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = "{\"choices\":[{\"message\":{\"content\":\"openai-bridged-ok\"}}],\"usage\":{\"prompt_tokens\":6,\"prompt_tokens_details\":{\"cached_tokens\":2},\"completion_tokens\":9}}",
                InputTokens = 6,
                CachedTokens = 2,
                OutputTokens = 9
            });
        }

        return Task.FromResult(new ProxyForwardResult
        {
            Success = true,
            StatusCode = 200,
            ResponseBody = "{\"content\":[{\"type\":\"text\",\"text\":\"anthropic-proxy-ok\"}],\"usage\":{\"input_tokens\":6,\"output_tokens\":9}}",
            InputTokens = 6,
            OutputTokens = 9
        });
    }

    /// <summary>
    /// 模拟流式转发过程，并按协议回放测试设定的事件流。
    /// </summary>
    public async Task<ProxyForwardResult> ForwardStreamingAsync(
        ProxyForwardRequest request,
        Func<string, CancellationToken, Task> onSseDataAsync,
        CancellationToken cancellationToken = default)
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
            TargetPath = request.TargetPath,
            ForwardHeaders = new Dictionary<string, string>(request.ForwardHeaders, StringComparer.OrdinalIgnoreCase)
        });

        if (string.Equals(request.ProtocolType, "Responses", StringComparison.OrdinalIgnoreCase))
        {
            var responseLines = ResponsesStreamingLines ??
            [
                "event: response.created",
                "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_default\",\"object\":\"response\",\"created_at\":1,\"status\":\"in_progress\",\"model\":\"gpt-4.1-real\",\"output\":[],\"usage\":null}}",
                string.Empty,
                "event: response.output_text.delta",
                "data: {\"type\":\"response.output_text.delta\",\"delta\":\"responses-anthropic-stream\",\"output_index\":0,\"content_index\":0}",
                string.Empty,
                "event: response.completed",
                "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_default\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"model\":\"gpt-4.1-real\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"responses-anthropic-stream\"}]}],\"usage\":{\"input_tokens\":6,\"input_tokens_details\":{\"cached_tokens\":2},\"output_tokens\":9,\"total_tokens\":15}}}",
                string.Empty,
                "data: [DONE]",
                string.Empty
            ];

            foreach (var line in responseLines)
            {
                await onSseDataAsync(line, cancellationToken);
            }

            var responsesStreamingResult = StreamingResultFactory?.Invoke(request);
            if (responsesStreamingResult is not null)
            {
                responsesStreamingResult.ResponseBody = string.IsNullOrWhiteSpace(responsesStreamingResult.ResponseBody)
                    ? string.Join("\n", responseLines)
                    : responsesStreamingResult.ResponseBody;
                responsesStreamingResult.InputTokens = responsesStreamingResult.InputTokens == 0 ? 6 : responsesStreamingResult.InputTokens;
                responsesStreamingResult.CachedTokens = responsesStreamingResult.CachedTokens == 0 ? 2 : responsesStreamingResult.CachedTokens;
                responsesStreamingResult.OutputTokens = responsesStreamingResult.OutputTokens == 0 ? 9 : responsesStreamingResult.OutputTokens;
                responsesStreamingResult.IsStreaming = true;
                return responsesStreamingResult;
            }

            return new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = string.Join("\n", responseLines),
                InputTokens = 6,
                CachedTokens = 2,
                OutputTokens = 9,
                IsStreaming = true,
                HasStartedStreaming = true
            };
        }

        if (string.Equals(request.ProtocolType, "OpenAI", StringComparison.Ordinal))
        {
            var lines = OpenAiStreamingLines ??
            [
                "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}",
                string.Empty,
                "data: {\"choices\":[{\"delta\":{\"content\":\"!\"}}],\"usage\":{\"prompt_tokens\":6,\"prompt_tokens_details\":{\"cached_tokens\":2},\"completion_tokens\":9}}",
                string.Empty,
                "data: [DONE]",
                string.Empty
            ];

            foreach (var line in lines)
            {
                await onSseDataAsync(line, cancellationToken);
            }

            var streamingResult = StreamingResultFactory?.Invoke(request);
            if (streamingResult is not null)
            {
                streamingResult.ResponseBody = string.IsNullOrWhiteSpace(streamingResult.ResponseBody)
                    ? string.Join("\n", lines)
                    : streamingResult.ResponseBody;
                streamingResult.InputTokens = streamingResult.InputTokens == 0 ? 6 : streamingResult.InputTokens;
                streamingResult.CachedTokens = streamingResult.CachedTokens;
                streamingResult.OutputTokens = streamingResult.OutputTokens == 0 ? 9 : streamingResult.OutputTokens;
                streamingResult.IsStreaming = true;
                return streamingResult;
            }

            return new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = string.Join("\n", lines),
                InputTokens = 6,
                CachedTokens = 2,
                OutputTokens = 9,
                IsStreaming = true,
                HasStartedStreaming = true
            };
        }

        var anthropicLines = AnthropicStreamingLines;
        if (anthropicLines is not null)
        {
            foreach (var line in anthropicLines)
            {
                await onSseDataAsync(line, cancellationToken);
            }

            var anthropicStreamingResult = StreamingResultFactory?.Invoke(request);
            if (anthropicStreamingResult is not null)
            {
                anthropicStreamingResult.ResponseBody = string.IsNullOrWhiteSpace(anthropicStreamingResult.ResponseBody)
                    ? string.Join("\n", anthropicLines)
                    : anthropicStreamingResult.ResponseBody;
                anthropicStreamingResult.InputTokens = anthropicStreamingResult.InputTokens == 0 ? 6 : anthropicStreamingResult.InputTokens;
                anthropicStreamingResult.CachedTokens = anthropicStreamingResult.CachedTokens == 0 ? 2 : anthropicStreamingResult.CachedTokens;
                anthropicStreamingResult.OutputTokens = anthropicStreamingResult.OutputTokens == 0 ? 5 : anthropicStreamingResult.OutputTokens;
                anthropicStreamingResult.IsStreaming = true;
                return anthropicStreamingResult;
            }

            return new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = string.Join("\n", anthropicLines),
                InputTokens = 6,
                CachedTokens = 2,
                OutputTokens = 5,
                IsStreaming = true,
                HasStartedStreaming = true
            };
        }

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
/// 混合路由回退场景下模拟 OpenAI 失败、Anthropic 成功的伪造转发服务。
/// </summary>
internal sealed class AnthropicFallbackStreamProxyForwardService : IProxyForwardService
{
    /// <summary>
    /// 记录流式转发路径被调用的次数。
    /// </summary>
    public int StreamAttemptCount { get; private set; }
    /// <summary>
    /// 记录非流式转发路径被调用的次数。
    /// </summary>
    public int NonStreamAttemptCount { get; private set; }

    /// <summary>
    /// 模拟非流式转发结果，便于验证回退后的最终响应。
    /// </summary>
    public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
    {
        NonStreamAttemptCount++;
        return Task.FromResult(new ProxyForwardResult
        {
            Success = true,
            StatusCode = 200,
            ResponseBody = "{\"content\":[{\"type\":\"text\",\"text\":\"anthropic-fallback-ok\"}],\"usage\":{\"input_tokens\":4,\"output_tokens\":6}}",
            InputTokens = 4,
            OutputTokens = 6,
            IsStreaming = false
        });
    }

    /// <summary>
    /// 模拟混合路由场景下的流式转发结果，用于验证失败后继续尝试后续流式路由。
    /// </summary>
    public async Task<ProxyForwardResult> ForwardStreamingAsync(
        ProxyForwardRequest request,
        Func<string, CancellationToken, Task> onSseDataAsync,
        CancellationToken cancellationToken = default)
    {
        StreamAttemptCount++;
        if (string.Equals(request.ProtocolType, "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            var lines = new[]
            {
                "event: message_start",
                "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_fallback\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-anthropic-secondary-real\",\"usage\":{\"input_tokens\":4,\"output_tokens\":0},\"content\":[]}}",
                string.Empty,
                "event: content_block_start",
                "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}",
                string.Empty,
                "event: content_block_delta",
                "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"anthropic-fallback-ok\"}}",
                string.Empty,
                "event: content_block_stop",
                "data: {\"type\":\"content_block_stop\",\"index\":0}",
                string.Empty,
                "event: message_delta",
                "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":6},\"delta\":{\"stop_reason\":\"end_turn\"}}",
                string.Empty,
                "event: message_stop",
                "data: {\"type\":\"message_stop\"}",
                string.Empty
            };

            foreach (var line in lines)
            {
                await onSseDataAsync(line, cancellationToken);
            }

            return new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = string.Join("\n", lines),
                InputTokens = 4,
                OutputTokens = 6,
                IsStreaming = true,
                HasStartedStreaming = true,
                TotalDurationMs = 8
            };
        }

        return new ProxyForwardResult
        {
            Success = false,
            StatusCode = 502,
            ErrorMessage = "upstream failed before first chunk",
            IsStreaming = true,
            HasStartedStreaming = false,
            TotalDurationMs = 5
        };
    }
}
