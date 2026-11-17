using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using AITool.Application.Proxy;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.Proxy;

public sealed class GatewayEnhancementTests
{
    [Fact]
    public async Task Settings_reject_stale_revision_and_never_return_secrets_from_admin_list()
    {
        await using var factory = new ResponsesWebApplicationFactory(new ResponsesFakeProxyForwardService());
        using var client = factory.CreateClient();
        var settings = factory.Services.GetRequiredService<GatewayEnhancementSettings>();
        var original = settings.Current;
        await settings.SaveAsync(new GatewayEnhancementOptions { SearchApiKey = "private-search-key" }, CancellationToken.None);
        var stale = () => settings.SaveAsync(original, CancellationToken.None, original.Revision);
        await stale.Should().ThrowAsync<InvalidOperationException>();
        using var response = await client.GetAsync("/api/admin/gateway-enhancements");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().NotContain("private-search-key").And.Contain("********");
        settings.Current.SearchApiKey.Should().Be("private-search-key");
    }

    [Fact]
    public async Task Amount_budget_blocks_unpriced_usage_in_the_current_window()
    {
        var fake = new ResponsesFakeProxyForwardService();
        await using var factory = new ResponsesWebApplicationFactory(fake);
        using var client = factory.CreateClient();
        var keyId = Guid.Parse("c3c3c3c3-c3c3-c3c3-c3c3-c3c3c3c3c3c3");
        await factory.Services.GetRequiredService<GatewayEnhancementSettings>().SaveAsync(new GatewayEnhancementOptions {
            Budgets = [new GatewayKeyBudget { KeyId = keyId, Period = "month", CostLimitUsd = 10 }] }, CancellationToken.None);
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Client.Insertable(new GatewayRequestMetric {
            AccessKeyId = keyId, StartedUtcTicks = DateTimeOffset.UtcNow.UtcTicks, Tokens = 100, Unpriced = true }).ExecuteCommandAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses") {
            Content = new StringContent("{\"model\":\"auto\",\"input\":\"hi\"}", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        using var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        fake.Requests.Should().BeEmpty();
    }
    [Fact]
    public async Task Vision_tool_uses_authenticated_gateway_route_without_forwarding_images_elsewhere()
    {
        var handler = new RemoteHandler(async request =>
        {
            request.RequestUri!.Host.Should().Be("127.0.0.1");
            request.RequestUri.AbsolutePath.Should().Be("/v1/chat/completions");
            request.Headers.Authorization!.Parameter.Should().Be("responses-test-key");
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            body["model"]!.ToString().Should().Be("vision-route");
            body["messages"]![0]!["content"]![1]!["image_url"]!["url"]!.ToString().Should().Be("https://image.example/test.png");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"description\"}}]}", Encoding.UTF8, "application/json") };
        });
        await using var factory = new ResponsesWebApplicationFactory(new ResponsesFakeProxyForwardService());
        _ = factory.CreateClient();
        var settings = factory.Services.GetRequiredService<GatewayEnhancementSettings>();
        await settings.SaveAsync(new GatewayEnhancementOptions { VisionRoute = "vision-route",
            ToolAllowedKeyIds = [Guid.Parse("c3c3c3c3-c3c3-c3c3-c3c3-c3c3c3c3c3c3")] }, CancellationToken.None);
        await using var scope = factory.Services.CreateAsyncScope();
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Scheme = "http"; http.Connection.LocalPort = 15029;
        http.Request.Headers.Authorization = "Bearer responses-test-key";
        var controller = new AITool.Web.Controllers.Proxy.GatewayToolsController(settings,
            factory.Services.GetRequiredService<ProxyRequestMetadataCache>(), new RemoteClients(handler))
        { ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http } };
        var result = await controller.Describe(new AITool.Web.Controllers.Proxy.GatewayToolRequest {
            ImageUrl = "https://image.example/test.png", Query = "describe" }, CancellationToken.None);
        var content = result.Should().BeOfType<Microsoft.AspNetCore.Mvc.ContentResult>().Subject;
        content.StatusCode.Should().Be(200); content.Content.Should().Contain("description");
    }
    [Fact]
    public async Task Mcp_tool_pagination_is_collected_and_session_is_released()
    {
        var deleted = false;
        var handler = new RemoteHandler(async request =>
        {
            if (request.Method == HttpMethod.Delete) { deleted = true; return new HttpResponseMessage(HttpStatusCode.NoContent); }
            var rpc = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            var method = rpc["method"]!.ToString();
            if (method == "notifications/initialized") return new HttpResponseMessage(HttpStatusCode.Accepted);
            var result = method == "initialize" ? "{}" : rpc["params"]?["cursor"] is null
                ? "{\"tools\":[{\"name\":\"one\",\"inputSchema\":{\"type\":\"object\"}}],\"nextCursor\":\"next\"}"
                : "{\"tools\":[{\"name\":\"two\",\"inputSchema\":{\"type\":\"object\"}}]}";
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + result + "}", Encoding.UTF8, "application/json") };
            response.Headers.Add("Mcp-Session-Id", "test-session"); return response;
        });
        await using var factory = new ResponsesWebApplicationFactory(new ResponsesFakeProxyForwardService(), configureServices: services =>
        { services.RemoveAll<IHttpClientFactory>(); services.AddSingleton<IHttpClientFactory>(new RemoteClients(handler)); });
        using var client = factory.CreateClient();
        await factory.Services.GetRequiredService<GatewayEnhancementSettings>().SaveAsync(new GatewayEnhancementOptions { McpServers = [
            new GatewayMcpServer { Name = "test", Url = "https://mcp.example", AllowedKeyIds = [Guid.Parse("c3c3c3c3-c3c3-c3c3-c3c3-c3c3c3c3c3c3")] }] }, CancellationToken.None);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp/aitool") { Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        using var response = await client.SendAsync(request);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        body["result"]!["tools"]!.AsArray().Should().HaveCount(2);
        deleted.Should().BeTrue();
    }
    [Fact]
    public async Task Search_requires_tool_permission_and_returns_bounded_results()
    {
        var handler = new RemoteHandler(request =>
        {
            request.RequestUri!.Query.Should().Contain("format=json").And.Contain("q=hello");
            request.Headers.Authorization!.Parameter.Should().Be("search-secret");
            var results = new JsonArray(Enumerable.Range(0, 15).Select(i => (JsonNode)new JsonObject {
                ["title"] = "hit " + i, ["url"] = "https://example.test/" + i, ["content"] = "excerpt" }).ToArray());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["results"] = results }.ToJsonString(), Encoding.UTF8, "application/json") });
        });
        await using var factory = new ResponsesWebApplicationFactory(new ResponsesFakeProxyForwardService(), configureServices: services =>
        { services.RemoveAll<IHttpClientFactory>(); services.AddSingleton<IHttpClientFactory>(new RemoteClients(handler)); });
        using var client = factory.CreateClient();
        var settings = factory.Services.GetRequiredService<GatewayEnhancementSettings>();
        var options = new GatewayEnhancementOptions { SearchEndpoint = "https://search.example/search", SearchApiKey = "search-secret" };
        await settings.SaveAsync(options, CancellationToken.None);
        async Task<HttpResponseMessage> Search()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/v1/tools/search") { Content = new StringContent("{\"query\":\"hello\"}", Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
            return await client.SendAsync(request);
        }
        using var denied = await Search(); denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        options.ToolAllowedKeyIds = [Guid.Parse("c3c3c3c3-c3c3-c3c3-c3c3-c3c3c3c3c3c3")];
        await settings.SaveAsync(options, CancellationToken.None);
        using var allowed = await Search(); allowed.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await allowed.Content.ReadAsStringAsync())!["results"]!.AsArray().Should().HaveCount(10);
    }

    [Fact]
    public async Task Tool_http_reader_rejects_oversized_body_before_returning_it()
    {
        using var content = new StringContent(new string('x', 100));
        var read = () => GatewayToolHttp.ReadAsync(content, 10, CancellationToken.None);
        await read.Should().ThrowAsync<HttpRequestException>();
    }
    [Fact]
    public async Task Websocket_budget_settles_each_turn_before_admitting_the_next()
    {
        var fake = new ResponsesFakeProxyForwardService { StreamingLines = [
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_budget\",\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"hello\"}]}],\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}}", ""] };
        await using var factory = new ResponsesWebApplicationFactory(fake);
        _ = factory.CreateClient();
        var settings = factory.Services.GetRequiredService<GatewayEnhancementSettings>();
        await settings.SaveAsync(new GatewayEnhancementOptions { Budgets = [new GatewayKeyBudget {
            KeyId = Guid.Parse("c3c3c3c3-c3c3-c3c3-c3c3-c3c3c3c3c3c3"), TokenLimit = 10 }] }, CancellationToken.None);
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => { r.Headers["Authorization"] = "Bearer responses-test-key"; r.Headers["Session-Id"] = "budget-session"; };
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/v1/responses"), CancellationToken.None);
        async Task Send(string body) => await socket.SendAsync(Encoding.UTF8.GetBytes(body), WebSocketMessageType.Text, true, CancellationToken.None);
        async Task<string> Receive()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var buffer = new byte[32_000];
            var read = await socket.ReceiveAsync(buffer, timeout.Token);
            return Encoding.UTF8.GetString(buffer, 0, read.Count);
        }
        await Send("""{"type":"response.create","model":"auto","input":[{"type":"message","role":"user","content":"hi"}]}""");
        (await Receive()).Should().Contain("response.completed");
        await Send("""{"type":"response.append","input":[{"type":"message","role":"user","content":"again"}]}""");
        JsonNode.Parse(await Receive())!["status"]!.GetValue<int>().Should().Be(429);
        fake.Requests.Should().ContainSingle();
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await using var scope = factory.Services.CreateAsyncScope();
        var metrics = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Client.Queryable<GatewayRequestMetric>().ToListAsync();
        metrics.Should().ContainSingle(); metrics[0].Tokens.Should().Be(15); metrics[0].ResponseBytes.Should().BeGreaterThan(0);
    }
    [Fact]
    public async Task Mcp_initializes_upstream_and_only_exposes_servers_allowed_for_the_key()
    {
        var methods = new List<string>();
        var handler = new RemoteHandler(async request =>
        {
            var json = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            var method = json["method"]!.ToString(); methods.Add(method);
            if (method == "notifications/initialized") return new HttpResponseMessage(HttpStatusCode.Accepted);
            var result = method == "initialize" ? "{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"serverInfo\":{\"name\":\"test\",\"version\":\"1\"}}"
                : method == "tools/list" ? "{\"tools\":[{\"name\":\"read\",\"inputSchema\":{\"type\":\"object\"}}]}"
                : "{\"content\":[{\"type\":\"text\",\"text\":\"result\"}]}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + result + "}", Encoding.UTF8, "application/json") };
        });
        await using var factory = new ResponsesWebApplicationFactory(new ResponsesFakeProxyForwardService(), configureServices: services =>
        { services.RemoveAll<IHttpClientFactory>(); services.AddSingleton<IHttpClientFactory>(new RemoteClients(handler)); });
        using var client = factory.CreateClient();
        var settings = factory.Services.GetRequiredService<GatewayEnhancementSettings>();
        var key = Guid.Parse("c3c3c3c3-c3c3-c3c3-c3c3-c3c3c3c3c3c3");
        await settings.SaveAsync(new GatewayEnhancementOptions { McpServers = [
            new GatewayMcpServer { Name = "allowed", Url = "https://mcp.example/rpc", AllowedKeyIds = [key] },
            new GatewayMcpServer { Name = "denied", Url = "https://denied.example/rpc" }] }, CancellationToken.None);
        async Task<JsonNode> Rpc(string body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp/aitool") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
            var response = await client.SendAsync(request); response.StatusCode.Should().Be(HttpStatusCode.OK);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        }
        var list = await Rpc("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        list["result"]!["tools"]!.AsArray().Should().HaveCount(1);
        list["result"]!["tools"]![0]!["name"]!.ToString().Should().Be("allowed__read");
        methods.Should().Equal("initialize", "notifications/initialized", "tools/list");
        var call = await Rpc("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"allowed__read","arguments":{}}}""");
        call["error"].Should().BeNull(call.ToJsonString());
        call["result"]!["content"]![0]!["text"]!.ToString().Should().Be("result");
        var denied = await Rpc("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"denied__read","arguments":{}}}""");
        denied["error"]!["code"]!.GetValue<int>().Should().Be(-32602);
    }

    private sealed class RemoteHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request); }
    private sealed class RemoteClients(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }
    [Fact]
    public void Loop_detector_cuts_repeated_prose_but_not_tool_arguments_or_code()
    {
        var detector = new ReplyLoopDetector();
        var hit = false;
        for (var i = 0; i < 300; i++) hit |= detector.Observe("""{"choices":[{"delta":{"reasoning_content":"Now writing. "}}]}""");
        hit.Should().BeTrue();
        var tools = new ReplyLoopDetector();
        for (var i = 0; i < 300; i++)
            tools.Observe("""{"choices":[{"delta":{"tool_calls":[{"function":{"arguments":"Now writing. "}}]}}]}""").Should().BeFalse();
        var code = new ReplyLoopDetector();
        for (var i = 0; i < 300; i++) code.Observe("""{"choices":[{"delta":{"content":"var x = 42;\n"}}]}""").Should().BeFalse();
    }

    [Fact]
    public void Budget_windows_are_utc_calendar_windows()
    {
        var now = new DateTimeOffset(2026, 11, 13, 23, 0, 0, TimeSpan.Zero);
        GatewayBudgetGate.WindowStart("day", now).Should().Be(new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero));
        GatewayBudgetGate.WindowStart("week", now).Day.Should().Be(9);
        GatewayBudgetGate.WindowStart("month", now).Day.Should().Be(1);
    }

    [Fact]
    public async Task Heartbeat_only_writes_between_complete_sse_events()
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Response.ContentType = "text/event-stream";
        await using var inner = new MemoryStream();
        await using var output = new GatewayResponseStream(inner, http, new GatewayEnhancementOptions { KeepAliveEnabled = true, KeepAliveSeconds = 1 });
        await output.WriteAsync(Encoding.UTF8.GetBytes("data: {\"partial\":"));
        await Task.Delay(1200);
        Encoding.UTF8.GetString(inner.ToArray()).Should().NotContain("keepalive");
        await output.WriteAsync(Encoding.UTF8.GetBytes("true}\n\n"));
        await Task.Delay(1200);
        await output.StopAsync();
        Encoding.UTF8.GetString(inner.ToArray()).Should().Contain("true}\n\n: keepalive\n\n");
    }

    [Fact]
    public void Extended_head_buffer_retains_more_warmup_events_until_content()
    {
        var buffer = new SseHeadBuffer(true);
        for (var i = 0; i < 12; i++) buffer.TryHold("event: response.created\ndata: {\"type\":\"response.created\"}\n\n").Should().BeTrue();
        buffer.TryHold("event: response.output_text.delta\ndata: {\"delta\":\"hi\"}\n\n").Should().BeFalse();
        buffer.Drain().Should().HaveCount(12);
    }

    [Fact]
    public async Task Configured_token_budget_rejects_before_upstream_send()
    {
        var fake = new ResponsesFakeProxyForwardService();
        await using var factory = new ResponsesWebApplicationFactory(fake);
        using var client = factory.CreateClient();
        var settings = factory.Services.GetRequiredService<GatewayEnhancementSettings>();
        var before = settings.Current;
        try
        {
            var keyId = Guid.Parse("c3c3c3c3-c3c3-c3c3-c3c3-c3c3c3c3c3c3");
            await settings.SaveAsync(new GatewayEnhancementOptions { Budgets = [new GatewayKeyBudget { KeyId = keyId, Period = "day", TokenLimit = 10 }] }, CancellationToken.None);
            await using var scope = factory.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Client.Insertable(new GatewayRequestMetric
            { AccessKeyId = keyId, StartedUtcTicks = DateTimeOffset.UtcNow.UtcTicks, Tokens = 10 }).ExecuteCommandAsync();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
            { Content = new StringContent("""{"model":"auto","input":"hi"}""", Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
            var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
            (await response.Content.ReadAsStringAsync()).Should().Contain("budget_exceeded");
            fake.Requests.Should().BeEmpty();
        }
        finally { await settings.SaveAsync(before, CancellationToken.None); }
    }
}
