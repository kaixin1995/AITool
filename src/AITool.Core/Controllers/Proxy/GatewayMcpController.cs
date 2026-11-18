using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Core.Controllers.Proxy;

/// <summary>Stateless streamable HTTP MCP router, with explicit per-key server allowlists.</summary>
[ApiController]
[Route("mcp/aitool")]
public sealed class GatewayMcpController(GatewayEnhancementSettings settings, ProxyRequestMetadataCache metadata,
    IHttpClientFactory clients) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Rpc([FromBody] JsonObject rpc, CancellationToken ct)
    {
        var auth = Request.Headers.Authorization.ToString();
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        operationTimeout.CancelAfter(TimeSpan.FromSeconds(90));
        var requestAborted = ct;
        ct = operationTimeout.Token;
        var key = await metadata.ValidateAccessKeyAsync(auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..] : "", ct);
        if (key is null) return Unauthorized();
        if (rpc["jsonrpc"]?.ToString() != "2.0") return BadRequest(new { message = "MCP 请求必须使用 JSON-RPC 2.0。" });
        var method = rpc["method"]?.ToString();
        if (method?.StartsWith("notifications/", StringComparison.Ordinal) == true) return Accepted();
        var id = rpc["id"]?.DeepClone();
        object Success(JsonNode value) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = value };
        object Error(int code, string message) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
        if (method == "initialize") return Ok(Success(new JsonObject { ["protocolVersion"] = "2025-03-26", ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject { ["name"] = "aitool", ["version"] = "1.0" } }));
        if (method == "ping") return Ok(Success(new JsonObject()));
        var servers = settings.Current.McpServers.Where(s => s.Enabled && s.AllowedKeyIds.Contains(key.Id)).ToList();
        var builtinAllowed = settings.Current.ToolAllowedKeyIds.Contains(key.Id);
        try
        {
            if (method == "tools/list")
            {
                var tools = new JsonArray();
                if (builtinAllowed && settings.Current.SearchEndpoint.Length > 0)
                    tools.Add(new JsonObject { ["name"] = "aitool_search", ["description"] = "Search the web using the administrator-configured search service.",
                        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("query") } });
                if (builtinAllowed && settings.Current.VisionRoute.Length > 0)
                    tools.Add(new JsonObject { ["name"] = "aitool_describe_image", ["description"] = "Describe an image using the administrator-configured vision route. This sends the image to that model and incurs model usage.",
                        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject {
                            ["imageUrl"] = new JsonObject { ["type"] = "string" }, ["query"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("imageUrl") } });
                foreach (var server in servers)
                {
                    var response = await Remote(server, "tools/list", new JsonObject(), ct);
                    if (response["tools"] is not JsonArray listed) continue;
                    foreach (var tool in listed.OfType<JsonObject>())
                    {
                        var copy = (JsonObject)tool.DeepClone();
                        copy["name"] = server.Name + "__" + tool["name"]?.ToString();
                        tools.Add(copy);
                    }
                }
                return Ok(Success(new JsonObject { ["tools"] = tools }));
            }
            if (method == "tools/call")
            {
                var name = rpc["params"]?["name"]?.ToString() ?? "";
                if (builtinAllowed && name == "aitool_describe_image" && settings.Current.VisionRoute.Length > 0)
                {
                    using var vision = new HttpRequestMessage(HttpMethod.Post, GatewayToolHttp.LocalEndpoint(HttpContext, "/v1/tools/describe-image"));
                    vision.Headers.Authorization = AuthenticationHeaderValue.Parse(auth);
                    vision.Content = new StringContent((rpc["params"]?["arguments"] ?? new JsonObject()).ToJsonString(), Encoding.UTF8, "application/json");
                    using var visionResponse = await clients.CreateClient("gateway-tools").SendAsync(vision, HttpCompletionOption.ResponseHeadersRead, ct);
                    var text = await GatewayToolHttp.ReadAsync(visionResponse.Content, 4_000_000, ct);
                    return Ok(Success(new JsonObject { ["isError"] = !visionResponse.IsSuccessStatusCode,
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) }));
                }
                if (builtinAllowed && name == "aitool_search" && settings.Current.SearchEndpoint.Length > 0)
                {
                    var query = rpc["params"]?["arguments"]?["query"]?.ToString() ?? "";
                    if (query.Length is < 1 or > 4000) return Ok(Error(-32602, "搜索内容长度无效。"));
                    var options = settings.Current;
                    using var search = new HttpRequestMessage(HttpMethod.Get, options.SearchEndpoint + (options.SearchEndpoint.Contains('?') ? "&" : "?") + "format=json&q=" + Uri.EscapeDataString(query));
                    if (options.SearchApiKey.Length > 0) search.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.SearchApiKey);
                    using var response = await clients.CreateClient("gateway-tools").SendAsync(search, HttpCompletionOption.ResponseHeadersRead, ct);
                    response.EnsureSuccessStatusCode();
                    var text = await GatewayToolHttp.ReadAsync(response.Content, 2_000_000, ct);
                    if (text.Length > 2_000_000) throw new InvalidOperationException("搜索结果过大。");
                    return Ok(Success(new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) }));
                }
                var server = servers.FirstOrDefault(s => name.StartsWith(s.Name + "__", StringComparison.Ordinal));
                if (server is null) return Ok(Error(-32602, "工具不存在或当前密钥无权访问。"));
                var parameters = (JsonObject)rpc["params"]!.DeepClone(); parameters["name"] = name[(server.Name.Length + 2)..];
                return Ok(Success(await Remote(server, method, parameters, ct)));
            }
            return Ok(Error(-32601, "Method not found"));
        }
        catch (HttpRequestException) { return Ok(Error(-32000, "MCP 上游调用失败。")); }
        catch (OperationCanceledException) when (!requestAborted.IsCancellationRequested) { return Ok(Error(-32000, "MCP 上游调用超时。")); }
        catch (System.Text.Json.JsonException) { return Ok(Error(-32000, "MCP 上游返回格式无效。")); }
        catch (InvalidOperationException ex) { return Ok(Error(-32000, ex.Message)); }
    }

    private async Task<JsonObject> Remote(GatewayMcpServer server, string method, JsonObject parameters, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        ct = timeout.Token;
        // Each operation initializes its own upstream session, avoiding cross-key state sharing.
        string? session = null;
        async Task<JsonObject> Send(string name, JsonObject args)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, server.Url);
            request.Headers.Accept.ParseAdd("application/json"); request.Headers.Accept.ParseAdd("text/event-stream");
            request.Headers.Add("MCP-Protocol-Version", "2025-03-26");
            if (session is not null) request.Headers.Add("Mcp-Session-Id", session);
            if (server.ApiKey.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.ApiKey);
            request.Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = name, ["params"] = args }.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await clients.CreateClient("gateway-tools").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var values)) session = values.FirstOrDefault();
            var body = await GatewayToolHttp.ReadAsync(response.Content, 4_000_000, ct);
            if (body.Length > 4_000_000) throw new InvalidOperationException("MCP 返回内容过大。");
            var payload = body.TrimStart().StartsWith('{') ? JsonNode.Parse(body) : body.Split('\n').Where(l => l.StartsWith("data:")).Select(l => { try { return JsonNode.Parse(l[5..]); } catch { return null; } }).LastOrDefault(n => n?["id"] is not null);
            if (payload?["error"] is not null) throw new InvalidOperationException("MCP 上游返回错误。");
            return payload?["result"]?.DeepClone() as JsonObject ?? throw new InvalidOperationException("MCP 返回格式无效。");
        }
        await Send("initialize", new JsonObject { ["protocolVersion"] = "2025-03-26", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "aitool", ["version"] = "1.0" } });
        using (var notification = new HttpRequestMessage(HttpMethod.Post, server.Url))
        {
            notification.Headers.Accept.ParseAdd("application/json"); notification.Headers.Accept.ParseAdd("text/event-stream");
            notification.Headers.Add("MCP-Protocol-Version", "2025-03-26");
            if (session is not null) notification.Headers.Add("Mcp-Session-Id", session);
            if (server.ApiKey.Length > 0) notification.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.ApiKey);
            notification.Content = new StringContent("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", Encoding.UTF8, "application/json");
            using var ack = await clients.CreateClient("gateway-tools").SendAsync(notification, ct);
            ack.EnsureSuccessStatusCode();
        }
        try
        {
            var result = await Send(method, parameters);
            if (method == "tools/list")
            {
                var all = result["tools"] as JsonArray ?? new JsonArray();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (result["nextCursor"]?.ToString() is { Length: > 0 } cursor)
                {
                    if (!seen.Add(cursor) || seen.Count > 32 || all.Count > 2000)
                        throw new InvalidOperationException("MCP 工具分页超过限制或重复。");
                    result = await Send("tools/list", new JsonObject { ["cursor"] = cursor });
                    if (result["tools"] is JsonArray page)
                        foreach (var item in page) all.Add(item?.DeepClone());
                }
                return new JsonObject { ["tools"] = all.DeepClone() };
            }
            return result;
        }
        finally
        {
            if (session is not null)
            {
                // A stateless gateway operation must not leave its upstream session alive.
                // Session deletion is optional in MCP; unsupported DELETE is harmless.
                using var release = new HttpRequestMessage(HttpMethod.Delete, server.Url);
                release.Headers.Add("Mcp-Session-Id", session);
                release.Headers.Add("MCP-Protocol-Version", "2025-03-26");
                if (server.ApiKey.Length > 0) release.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.ApiKey);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { using var response = await clients.CreateClient("gateway-tools").SendAsync(release, HttpCompletionOption.ResponseHeadersRead, cleanup.Token); }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
            }
        }
    }
}
