using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AITool.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Web.Controllers.Proxy;

public sealed class GatewayToolRequest
{
    public string Query { get; set; } = "";
    public string ImageUrl { get; set; } = "";
}

[ApiController]
[Route("v1/tools")]
public sealed class GatewayToolsController(GatewayEnhancementSettings settings, ProxyRequestMetadataCache metadata,
    IHttpClientFactory clients) : ControllerBase
{
    [HttpPost("search")]
    public async Task<IActionResult> Search(GatewayToolRequest input, CancellationToken ct)
    {
        try { return await SearchCore(input, ct); }
        catch (HttpRequestException) { return StatusCode(502, new { message = "搜索服务请求失败或响应超出大小限制。" }); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return StatusCode(504, new { message = "搜索服务超时。" }); }
    }

    private async Task<IActionResult> SearchCore(GatewayToolRequest input, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60)); ct = timeout.Token;
        var key = await Key(ct);
        if (key is null) return Unauthorized();
        if (!settings.Current.ToolAllowedKeyIds.Contains(key.Id)) return StatusCode(403, new { message = "当前密钥未获授权使用辅助工具。" });
        if (string.IsNullOrWhiteSpace(input.Query) || input.Query.Length > 4000) return BadRequest(new { message = "请输入 1–4000 字符的搜索内容。" });
        var options = settings.Current;
        if (options.SearchEndpoint.Length == 0) return StatusCode(503, new { message = "尚未配置搜索服务。" });
        var separator = options.SearchEndpoint.Contains('?') ? "&" : "?";
        using var request = new HttpRequestMessage(HttpMethod.Get, options.SearchEndpoint + separator + "format=json&q=" + Uri.EscapeDataString(input.Query));
        if (options.SearchApiKey.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.SearchApiKey);
        using var response = await clients.CreateClient("gateway-tools").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return StatusCode(502, new { message = "搜索服务请求失败。" });
        var body = await GatewayToolHttp.ReadAsync(response.Content, 2_000_000, ct);
        if (body.Length > 2_000_000) return StatusCode(502, new { message = "搜索结果过大。" });
        JsonNode? root;
        try { root = JsonNode.Parse(body); } catch { return StatusCode(502, new { message = "搜索服务未返回 JSON。" }); }
        if (root is not JsonObject || root["results"] is not JsonArray) return StatusCode(502, new { message = "搜索服务返回格式不兼容，请使用 SearXNG JSON 搜索接口。" });
        return Ok(new { results = (root?["results"] as JsonArray ?? []).OfType<JsonObject>().Take(10)
            .Select(r => new { title = r["title"]?.ToString(), url = r["url"]?.ToString(), content = r["content"]?.ToString() }) });
    }

    [HttpPost("describe-image")]
    public async Task<IActionResult> Describe(GatewayToolRequest input, CancellationToken ct)
    {
        try { return await DescribeCore(input, ct); }
        catch (HttpRequestException) { return StatusCode(502, new { message = "识图路由请求失败或响应超出大小限制。" }); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return StatusCode(504, new { message = "识图服务超时。" }); }
    }

    private async Task<IActionResult> DescribeCore(GatewayToolRequest input, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60)); ct = timeout.Token;
        var key = await Key(ct);
        if (key is null) return Unauthorized();
        if (!settings.Current.ToolAllowedKeyIds.Contains(key.Id)) return StatusCode(403, new { message = "当前密钥未获授权使用辅助工具。" });
        var options = settings.Current;
        if (string.IsNullOrWhiteSpace(options.VisionRoute)) return StatusCode(503, new { message = "尚未配置识图路由。" });
        if (input.ImageUrl is null || input.Query is null || input.Query.Length > 4000 || input.ImageUrl.Length > 12_000_000 || !(input.ImageUrl.StartsWith("data:image/", StringComparison.Ordinal)
            || Uri.TryCreate(input.ImageUrl, UriKind.Absolute, out var image) && image.Scheme == "https")) return BadRequest(new { message = "图片必须为 HTTPS 地址或图片 data URL。" });
        // Invoke the gateway's authenticated route, so key restrictions, accounting and egress policy apply.
        if (HttpContext.Connection.LocalPort <= 0) return StatusCode(503, new { message = "当前宿主不支持内部识图调用。" });
        using var request = new HttpRequestMessage(HttpMethod.Post, GatewayToolHttp.LocalEndpoint(HttpContext, "/v1/chat/completions"));
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(Request.Headers.Authorization.ToString());
        request.Headers.Add("X-Session-Id", "vision-" + Guid.NewGuid().ToString("N"));
        request.Content = new StringContent(new JsonObject { ["model"] = options.VisionRoute, ["stream"] = false,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = string.IsNullOrWhiteSpace(input.Query) ? "Describe this image accurately for a coding assistant." : input.Query },
                new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = input.ImageUrl } }) }) }.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await clients.CreateClient("gateway-tools").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return new ContentResult { Content = await GatewayToolHttp.ReadAsync(response.Content, 4_000_000, ct), ContentType = "application/json", StatusCode = (int)response.StatusCode };
    }

    private Task<CachedProxyAccessKey?> Key(CancellationToken ct)
    {
        var auth = Request.Headers.Authorization.ToString();
        return metadata.ValidateAccessKeyAsync(auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..] : "", ct);
    }
}
