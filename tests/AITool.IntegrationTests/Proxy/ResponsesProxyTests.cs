using System.Net;
using System.Net.Http.Headers;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.Proxy;

/// <summary>
/// 验证 /v1/responses 端点在 OpenAI 透传和 Anthropic 桥接场景下的行为。
/// </summary>
public sealed class ResponsesProxyTests
{
    /// <summary>
    /// OpenAI 上游非流式请求应直接透传 Responses 格式。
    /// </summary>
    [Fact]
    public async Task Post_responses_passthrough_openai_non_streaming()
    {
        var fakeForwardService = new ResponsesFakeProxyForwardService();
        await using var factory = new ResponsesWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"auto\",\"input\":\"hello\"}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        // 转发到上游时应使用 /v1/responses 路径
        fakeForwardService.Requests.Should().ContainSingle();
        fakeForwardService.Requests[0].TargetPath.Should().Be("/v1/responses");
        fakeForwardService.Requests[0].ProtocolType.Should().Be("OpenAI");
    }

    /// <summary>
    /// Anthropic 上游非流式请求应将 Responses 转为 Chat Completions 后转发。
    /// </summary>
    [Fact]
    public async Task Post_responses_bridges_to_anthropic_non_streaming()
    {
        var fakeForwardService = new ResponsesFakeProxyForwardService
        {
            IsAnthropicOnly = true
        };
        await using var factory = new ResponsesWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"auto\",\"input\":\"hello\",\"instructions\":\"be helpful\"}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        // 转发到 Anthropic 上游
        fakeForwardService.Requests.Should().ContainSingle();
        fakeForwardService.Requests[0].ProtocolType.Should().Be("Anthropic");

        // 转换后的请求体应包含 messages 和 system
        var prepared = fakeForwardService.Requests[0].PreparedRequestBody;
        prepared.Should().NotBeNullOrWhiteSpace();
        using var preparedDoc = JsonDocument.Parse(prepared!);
        preparedDoc.RootElement.GetProperty("messages").GetArrayLength().Should().BeGreaterThan(0);

        // 最终响应应该是 Responses 格式
        using var resultDoc = JsonDocument.Parse(body);
        resultDoc.RootElement.GetProperty("object").GetString().Should().Be("response");
        resultDoc.RootElement.GetProperty("status").GetString().Should().Be("completed");
        resultDoc.RootElement.GetProperty("output").GetArrayLength().Should().BeGreaterThan(0);
    }

    /// <summary>
    /// OpenAI 上游流式请求应直接透传 Responses SSE 事件。
    /// </summary>
    [Fact]
    public async Task Post_responses_passthrough_openai_streaming()
    {
        var fakeForwardService = new ResponsesFakeProxyForwardService
        {
            StreamingLines =
            [
                "data: {\"id\":\"resp_test\",\"object\":\"response\",\"created\":1,\"model\":\"auto\",\"status\":\"in_progress\",\"output\":[],\"type\":\"response.created\"}",
                string.Empty,
                "data: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\",\"output_index\":0,\"content_index\":0}",
                string.Empty,
                "data: [DONE]",
                string.Empty
            ]
        };
        await using var factory = new ResponsesWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"auto\",\"input\":\"hello\",\"stream\":true}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

        fakeForwardService.Requests.Should().ContainSingle();
        fakeForwardService.Requests[0].TargetPath.Should().Be("/v1/responses");
        fakeForwardService.Requests[0].EnableStreaming.Should().BeTrue();

        body.Should().Contain("response.output_text.delta");
        body.Should().Contain("hello");
        body.Should().Contain("[DONE]");
    }

    /// <summary>
    /// Anthropic 上游流式请求应将 Anthropic SSE 实时转换为 Responses SSE 事件。
    /// </summary>
    [Fact]
    public async Task Post_responses_bridges_anthropic_stream_to_responses_events()
    {
        var fakeForwardService = new ResponsesFakeProxyForwardService
        {
            IsAnthropicOnly = true,
            StreamingLines =
            [
                "event: message_start",
                "data: {\"message\":{\"usage\":{\"input_tokens\":10,\"cache_read_input_tokens\":2,\"output_tokens\":0}}}",
                string.Empty,
                "event: content_block_start",
                "data: {\"content_block\":{\"type\":\"text\",\"text\":\"\"},\"index\":0}",
                string.Empty,
                "event: content_block_delta",
                "data: {\"delta\":{\"type\":\"text_delta\",\"text\":\"bridged-response\"},\"index\":0}",
                string.Empty,
                "event: content_block_stop",
                "data: {\"index\":0}",
                string.Empty,
                "event: message_delta",
                "data: {\"usage\":{\"output_tokens\":5},\"delta\":{\"stop_reason\":\"end_turn\"}}",
                string.Empty,
                "event: message_stop",
                "data: {\"type\":\"message_stop\"}",
                string.Empty
            ]
        };
        await using var factory = new ResponsesWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"auto\",\"input\":\"hello\",\"stream\":true}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");

        fakeForwardService.Requests.Should().ContainSingle();
        fakeForwardService.Requests[0].ProtocolType.Should().Be("Anthropic");
        fakeForwardService.Requests[0].EnableStreaming.Should().BeTrue();

        // 输出应包含 Responses 事件类型
        body.Should().Contain("response.created");
        body.Should().Contain("response.in_progress");
        body.Should().Contain("response.output_text.delta");
        body.Should().Contain("bridged-response");
        body.Should().Contain("response.completed");
    }

    /// <summary>
    /// Responses 请求中包含 reasoning 时应提取 effort 并传入日志。
    /// </summary>
    [Fact]
    public async Task Post_responses_extracts_reasoning_effort_to_usage_log()
    {
        var fakeForwardService = new ResponsesFakeProxyForwardService();
        await using var factory = new ResponsesWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"auto\",\"input\":\"hello\",\"reasoning\":{\"effort\":\"high\"}}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logs = await db.ProxyUsageLogs.ToListAsync();
        logs.Should().ContainSingle();
        logs[0].ReasoningEffort.Should().Be("high");
    }

    /// <summary>
    /// Responses 请求中包含 output_config.effort 时应提取并写入日志。
    /// </summary>
    [Fact]
    public async Task Post_responses_extracts_output_config_effort_to_usage_log()
    {
        var fakeForwardService = new ResponsesFakeProxyForwardService();
        await using var factory = new ResponsesWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"auto\",\"input\":\"hello\",\"output_config\":{\"effort\":\"high\"}}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logs = await db.ProxyUsageLogs.ToListAsync();
        logs.Should().ContainSingle();
        logs[0].ReasoningEffort.Should().Be("high");
    }

    /// <summary>
    /// Responses 的 input 为字符串时应正常解析。
    /// </summary>
    [Fact]
    public async Task Post_responses_handles_string_input()
    {
        var fakeForwardService = new ResponsesFakeProxyForwardService
        {
            IsAnthropicOnly = true
        };
        await using var factory = new ResponsesWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"auto\",\"input\":\"simple string input\"}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        // 转换后的请求体应包含 input 字符串作为 user 消息
        var prepared = fakeForwardService.Requests[0].PreparedRequestBody;
        prepared.Should().Contain("simple string input");
        prepared.Should().Contain("\"role\":\"user\"");
    }

    /// <summary>
    /// 无效的模型名应返回 404。
    /// </summary>
    [Fact]
    public async Task Post_responses_returns_404_when_no_route_matches()
    {
        var fakeForwardService = new ResponsesFakeProxyForwardService();
        await using var factory = new ResponsesWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"nonexistent-model\",\"input\":\"hello\"}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// 无效密钥应返回 401。
    /// </summary>
    [Fact]
    public async Task Post_responses_returns_401_when_key_is_invalid()
    {
        var fakeForwardService = new ResponsesFakeProxyForwardService();
        await using var factory = new ResponsesWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"auto\",\"input\":\"hello\"}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-key");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 不应调用转发服务
        fakeForwardService.Requests.Should().BeEmpty();
    }

    /// <summary>
    /// 透传非流式应返回完整 Responses 格式且 usage 日志记录正确。
    /// </summary>
    [Fact]
    public async Task Post_responses_passthrough_non_streaming_logs_usage()
    {
        var fakeForwardService = new ResponsesFakeProxyForwardService();
        await using var factory = new ResponsesWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"auto\",\"input\":\"hello\",\"stream\":false}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        // 返回体应为 Responses 格式
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("object").GetString().Should().Be("response");
        doc.RootElement.GetProperty("output").GetArrayLength().Should().BeGreaterThan(0);

        // 验证 usage 日志
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logs = await db.ProxyUsageLogs.ToListAsync();
        logs.Should().ContainSingle();
        logs[0].ProtocolType.Should().Be("OpenAI");
        logs[0].ForwardingMode.Should().Be("direct");
        logs[0].IsStreaming.Should().BeFalse();
        logs[0].Status.Should().Be("success");
        logs[0].IsFinalResult.Should().BeTrue();
    }

    /// <summary>
    /// 透传流式应返回 SSE 格式且 usage 日志记录正确。
    /// </summary>
    [Fact]
    public async Task Post_responses_passthrough_streaming_logs_usage()
    {
        var fakeForwardService = new ResponsesFakeProxyForwardService
        {
            StreamingLines =
            [
                "data: {\"id\":\"resp_test\",\"object\":\"response\",\"created\":1,\"model\":\"auto\",\"status\":\"in_progress\",\"output\":[],\"type\":\"response.created\"}",
                string.Empty,
                "data: {\"type\":\"response.output_text.delta\",\"delta\":\"hi\",\"output_index\":0,\"content_index\":0}",
                string.Empty,
                "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_test\",\"object\":\"response\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"hi\"}]}],\"usage\":{\"input_tokens\":5,\"input_tokens_details\":{\"cached_tokens\":1},\"output_tokens\":2,\"total_tokens\":7}}}",
                string.Empty,
                "data: [DONE]",
                string.Empty
            ]
        };
        await using var factory = new ResponsesWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"auto\",\"input\":\"hello\",\"stream\":true}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        // 验证 usage 日志
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logs = await db.ProxyUsageLogs.ToListAsync();
        logs.Should().ContainSingle();
        logs[0].IsStreaming.Should().BeTrue();
        logs[0].Status.Should().Be("success");
        logs[0].IsFinalResult.Should().BeTrue();
        logs[0].InputTokens.Should().Be(5);
        logs[0].CachedTokens.Should().Be(1);
        logs[0].OutputTokens.Should().Be(2);
    }

    /// <summary>
    /// 兼容中转非流式应将 Anthropic 响应转为 Responses 格式且 usage 日志记录正确。
    /// </summary>
    [Fact]
    public async Task Post_responses_bridge_non_streaming_logs_usage()
    {
        var fakeForwardService = new ResponsesFakeProxyForwardService
        {
            IsAnthropicOnly = true
        };
        await using var factory = new ResponsesWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"auto\",\"input\":\"hello\",\"stream\":false}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        // 返回体应为 Responses 格式
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("object").GetString().Should().Be("response");
        doc.RootElement.GetProperty("status").GetString().Should().Be("completed");

        // 转发到 Anthropic 上游
        fakeForwardService.Requests.Should().ContainSingle();
        fakeForwardService.Requests[0].ProtocolType.Should().Be("Anthropic");

        // 验证 usage 日志
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logs = await db.ProxyUsageLogs.ToListAsync();
        logs.Should().ContainSingle();
        logs[0].ForwardingMode.Should().Be("bridge");
        logs[0].IsStreaming.Should().BeFalse();
        logs[0].Status.Should().Be("success");
    }

    /// <summary>
    /// 兼容中转流式应将 Anthropic SSE 实时转为 Responses SSE 且 usage 日志记录正确。
    /// </summary>
    [Fact]
    public async Task Post_responses_bridge_streaming_logs_usage()
    {
        var fakeForwardService = new ResponsesFakeProxyForwardService
        {
            IsAnthropicOnly = true,
            StreamingLines =
            [
                "event: message_start",
                "data: {\"message\":{\"usage\":{\"input_tokens\":10,\"cache_read_input_tokens\":2,\"output_tokens\":0}}}",
                string.Empty,
                "event: content_block_start",
                "data: {\"content_block\":{\"type\":\"text\",\"text\":\"\"},\"index\":0}",
                string.Empty,
                "event: content_block_delta",
                "data: {\"delta\":{\"type\":\"text_delta\",\"text\":\"bridge-test\"},\"index\":0}",
                string.Empty,
                "event: content_block_stop",
                "data: {\"index\":0}",
                string.Empty,
                "event: message_delta",
                "data: {\"usage\":{\"output_tokens\":5},\"delta\":{\"stop_reason\":\"end_turn\"}}",
                string.Empty,
                "event: message_stop",
                "data: {\"type\":\"message_stop\"}",
                string.Empty
            ]
        };
        await using var factory = new ResponsesWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"auto\",\"input\":\"hello\",\"stream\":true}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        // 验证 usage 日志
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logs = await db.ProxyUsageLogs.ToListAsync();
        logs.Should().ContainSingle();
        logs[0].ForwardingMode.Should().Be("bridge");
        logs[0].IsStreaming.Should().BeTrue();
        logs[0].Status.Should().Be("success");
    }

    /// <summary>
    /// 缺少 model 字段应返回 400。
    /// </summary>
    [Fact]
    public async Task Post_responses_returns_400_when_model_is_missing()
    {
        var fakeForwardService = new ResponsesFakeProxyForwardService();
        await using var factory = new ResponsesWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"input\":\"hello\"}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");

        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        fakeForwardService.Requests.Should().BeEmpty();
    }

    /// <summary>
    /// 兼容中转非流式时，转换后的请求体应包含 Anthropic 格式的 messages 和 system。
    /// </summary>
    [Fact]
    public async Task Post_responses_bridge_converts_request_to_anthropic_format()
    {
        var fakeForwardService = new ResponsesFakeProxyForwardService
        {
            IsAnthropicOnly = true
        };
        await using var factory = new ResponsesWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"auto\",\"input\":\"hello\",\"instructions\":\"be helpful\",\"stream\":false}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        // 验证转发到 Anthropic 的请求体是 Anthropic 原生格式
        fakeForwardService.Requests.Should().ContainSingle();
        var prepared = fakeForwardService.Requests[0].PreparedRequestBody;
        prepared.Should().NotBeNullOrWhiteSpace();
        using var preparedDoc = JsonDocument.Parse(prepared!);

        // 应包含 messages 数组
        preparedDoc.RootElement.TryGetProperty("messages", out var messages).Should().BeTrue();
        messages.GetArrayLength().Should().BeGreaterThan(0);

        // Anthropic 格式应有 system 或 max_tokens 或 anthropic-version
        var hasAnthropicFields = preparedDoc.RootElement.TryGetProperty("system", out _)
            || preparedDoc.RootElement.TryGetProperty("max_tokens", out _);
        hasAnthropicFields.Should().BeTrue();
    }

    /// <summary>
    /// 兼容中转时应把 output_config.effort 透传为 Anthropic thinking 配置。
    /// </summary>
    [Fact]
    public async Task Post_responses_bridge_maps_output_config_effort_to_anthropic_thinking()
    {
        var fakeForwardService = new ResponsesFakeProxyForwardService
        {
            IsAnthropicOnly = true
        };
        await using var factory = new ResponsesWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"auto\",\"input\":\"hello\",\"output_config\":{\"effort\":\"high\"}}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        fakeForwardService.Requests.Should().ContainSingle();
        var prepared = fakeForwardService.Requests[0].PreparedRequestBody;
        prepared.Should().NotBeNullOrWhiteSpace();
        using var preparedDoc = JsonDocument.Parse(prepared!);
        preparedDoc.RootElement.TryGetProperty("thinking", out var thinking).Should().BeTrue();
        thinking.TryGetProperty("type", out var thinkingType).Should().BeTrue();
        thinkingType.GetString().Should().Be("enabled");
        thinking.TryGetProperty("budget_tokens", out var budgetTokens).Should().BeTrue();
        budgetTokens.GetInt32().Should().Be(4096);
    }

    /// <summary>
    /// 透传非流式时返回的 Responses 格式应包含 usage 信息。
    /// </summary>
    [Fact]
    public async Task Post_responses_passthrough_non_streaming_includes_usage()
    {
        var fakeForwardService = new ResponsesFakeProxyForwardService();
        await using var factory = new ResponsesWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"auto\",\"input\":\"hello\"}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        // 返回体应包含 usage
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("usage", out var usage).Should().BeTrue();
        usage.TryGetProperty("prompt_tokens", out _).Should().BeTrue();
        usage.TryGetProperty("completion_tokens", out _).Should().BeTrue();
    }

    /// <summary>
    /// 兼容中转非流式时返回的 Responses 格式应包含 usage 信息。
    /// </summary>
    [Fact]
    public async Task Post_responses_bridge_non_streaming_includes_usage()
    {
        var fakeForwardService = new ResponsesFakeProxyForwardService
        {
            IsAnthropicOnly = true
        };
        await using var factory = new ResponsesWebApplicationFactory(fakeForwardService);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent(
                "{\"model\":\"auto\",\"input\":\"hello\"}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        // 返回体应包含 usage，Responses 格式用 prompt_tokens / completion_tokens
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("usage", out var usage).Should().BeTrue();
        usage.TryGetProperty("prompt_tokens", out _).Should().BeTrue();
        usage.TryGetProperty("completion_tokens", out _).Should().BeTrue();
    }
}

/// <summary>
/// Responses 端点集成测试的 WebApplicationFactory。
/// </summary>
internal sealed class ResponsesWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-responses-test-{Guid.NewGuid():N}.db");
    private readonly ResponsesFakeProxyForwardService _fakeForwardService;

    public ResponsesWebApplicationFactory(ResponsesFakeProxyForwardService fakeForwardService)
    {
        _fakeForwardService = fakeForwardService;
    }

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

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        SeedAsync().GetAwaiter().GetResult();
    }

    private async Task SeedAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var openAiSiteId = Guid.Parse("a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1");
        var anthropicSiteId = Guid.Parse("b2b2b2b2-b2b2-b2b2-b2b2-b2b2b2b2b2b2");
        var accessKeyRaw = "responses-test-key";
        var accessKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessKeyRaw)));

        if (!_fakeForwardService.IsAnthropicOnly)
        {
            db.Sites.Add(new Site
            {
                Id = openAiSiteId,
                Name = "OpenAI Site",
                BaseUrl = "https://openai.example.com",
                ApiKey = "openai-key",
                ProtocolType = "OpenAI",
                SupportsOpenAi = true,
                SupportsAnthropic = false,
                IsEnabled = true
            });

            db.ProxyRouteRules.Add(new ProxyRouteRule
            {
                Id = Guid.Parse("d4d4d4d4-d4d4-d4d4-d4d4-d4d4d4d4d4d4"),
                ExternalModelName = "auto",
                UpstreamModelName = "gpt-4.1",
                SiteId = openAiSiteId,
                SiteModelName = "gpt-4.1-real",
                Priority = 0,
                ModelPriority = 0,
                InstancePriority = 0,
                IsEnabled = true
            });
        }

        db.Sites.Add(new Site
        {
            Id = anthropicSiteId,
            Name = "Anthropic Site",
            BaseUrl = "https://anthropic.example.com",
            ApiKey = "anthropic-key",
            ProtocolType = "Anthropic",
            SupportsOpenAi = false,
            SupportsAnthropic = true,
            IsEnabled = true
        });

        db.ProxyAccessKeys.Add(new ProxyAccessKey
        {
            Id = Guid.Parse("c3c3c3c3-c3c3-c3c3-c3c3-c3c3c3c3c3c3"),
            KeyName = "responses-test",
            PlainKey = accessKeyRaw,
            AccessKeyHash = accessKeyHash,
            MaskedValue = "sk-***test",
            IsEnabled = true
        });

        db.ProxyRouteRules.Add(new ProxyRouteRule
        {
            Id = Guid.Parse("e5e5e5e5-e5e5-e5e5-e5e5-e5e5e5e5e5e5"),
            ExternalModelName = "auto",
            UpstreamModelName = "claude-3-7-sonnet",
            SiteId = anthropicSiteId,
            SiteModelName = "claude-3-7-sonnet-real",
            Priority = 1,
            ModelPriority = 1,
            InstancePriority = 0,
            IsEnabled = true
        });

        db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            ProxyRequestTimeoutSeconds = 12,
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
/// Responses 端点集成测试的模拟转发服务。
/// </summary>
internal sealed class ResponsesFakeProxyForwardService : IProxyForwardService
{
    public List<ProxyForwardRequest> Requests { get; } = [];
    public List<string>? StreamingLines { get; set; }
    public bool IsAnthropicOnly { get; set; }

    public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(CloneRequest(request));

        if (string.Equals(request.ProtocolType, "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = "{\"content\":[{\"type\":\"text\",\"text\":\"anthropic-responses-ok\"}],\"usage\":{\"input_tokens\":10,\"cache_read_input_tokens\":2,\"output_tokens\":5}}",
                InputTokens = 10,
                CachedTokens = 2,
                OutputTokens = 5
            });
        }

        return Task.FromResult(new ProxyForwardResult
        {
            Success = true,
            StatusCode = 200,
            ResponseBody = "{\"id\":\"resp_test123\",\"object\":\"response\",\"created\":1,\"status\":\"completed\",\"model\":\"auto\",\"output\":[{\"type\":\"message\",\"id\":\"msg_test\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"responses-ok\"}]}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15,\"prompt_tokens_details\":{\"cached_tokens\":0}}}",
            InputTokens = 10,
            OutputTokens = 5
        });
    }

    public async Task<ProxyForwardResult> ForwardStreamingAsync(
        ProxyForwardRequest request,
        Func<string, CancellationToken, Task> onSseDataAsync,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(CloneRequest(request));

        var lines = StreamingLines ??
        [
            "data: {\"id\":\"resp_default\",\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"default-stream\"}}]}",
            string.Empty,
            "data: [DONE]",
            string.Empty
        ];

        foreach (var line in lines)
        {
            await onSseDataAsync(line, cancellationToken);
        }

        return new ProxyForwardResult
        {
            Success = true,
            StatusCode = 200,
            ResponseBody = string.Join("\n", lines),
            InputTokens = 10,
            CachedTokens = 2,
            OutputTokens = 5,
            IsStreaming = true,
            HasStartedStreaming = true
        };
    }

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
