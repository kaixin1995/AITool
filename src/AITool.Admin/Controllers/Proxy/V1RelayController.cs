using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Admin.Controllers.Proxy;

/// <summary>
/// 中继选项：维护后台是否开启 /v1 代理中继（默认关闭，关闭时端点 404）。
/// </summary>
public sealed class V1RelayOptions
{
    public bool Enabled { get; init; }
    public int HeartbeatSeconds { get; init; } = 15;
    public int MaxReconnects { get; init; } = 3;
}

/// <summary>
/// Admin /v1 中继端点：把客户端 /v1/* 请求反向代理到 Core 宿主。
/// <para>
/// 目的：客户端就近连本地 Admin，跨境链路集中在 Admin↔Core 之间；Admin↔Core 断线时
/// 用 X-Stream-Resume 断点续传（最多 <see cref="V1RelayOptions.MaxReconnects"/> 次），
/// 客户端侧连接全程保持，SSE 流无感。
/// - AccessKey 由 Admin 直接查库校验（与 Core 校验同口径，双保险）；
/// - 流式下行包心跳（静默超阈值注入 `: ping`）；
/// - 续传后的新流以首个 [DONE] 为界截断（v1 语义：重放段补齐缺失帧，新流只取尾部结束标记）。
/// </para>
/// </summary>
[ApiController]
[Route("v1")]
public sealed class V1RelayController : ControllerBase
{
    private const int MaxBufferedBodyBytes = 16 * 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly V1RelayOptions _options;
    private readonly ILogger<V1RelayController> _logger;

    public V1RelayController(
        IHttpClientFactory httpClientFactory,
        ProxyRequestMetadataCache metadataCache,
        V1RelayOptions options,
        ILogger<V1RelayController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _metadataCache = metadataCache;
        _options = options;
        _logger = logger;
    }

    [HttpGet("{**path}")]
    [HttpPost("{**path}")]
    [HttpPut("{**path}")]
    [HttpDelete("{**path}")]
    public async Task Relay(string? path, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // 1. AccessKey 校验（Admin 直查库，语义与 Core 一致）。
        var accessToken = ExtractAccessToken(Request);
        var accessKey = await _metadataCache.ValidateAccessKeyAsync(accessToken, cancellationToken);
        if (accessKey is null)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            await Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    message = "访问密钥无效或缺失，请在请求头中携带有效的 Authorization Bearer 令牌",
                    type = "authentication_error",
                    code = "invalid_access_key"
                }
            }, cancellationToken);
            return;
        }

        // 2. 请求体一次性读取（重连时复用；超出上限拒绝）。
        byte[] body;
        using (var ms = new MemoryStream())
        {
            await Request.Body.CopyToAsync(ms, cancellationToken);
            if (ms.Length > MaxBufferedBodyBytes)
            {
                Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            body = ms.ToArray();
        }

        // 路由模板已消费 "v1" 前缀，catch-all 捕获的是剩余路径，转发时须补回。
        var wirePath = string.IsNullOrWhiteSpace(path) ? "/v1" : $"/v1/{path}";
        var query = Request.QueryString.HasValue ? Request.QueryString.Value! : "";
        var client = _httpClientFactory.CreateClient("RelayCoreClient");
        var state = new RelayStreamState();
        var attempt = 0;
        HttpResponseMessage? current = null;

        try
        {
            while (true)
            {
                // 3. 发出（首次或重连）请求。
                using var response = await client.SendAsync(
                    BuildRequest(accessToken, wirePath, query, body, state.ResumeHeader),
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                current = response;

                if (!response.IsSuccessStatusCode)
                {
                    await WriteRelayFailureAsync(response, null, cancellationToken);
                    return;
                }

                var isStreaming = string.Equals(
                    response.Content.Headers.ContentType?.MediaType,
                    "text/event-stream",
                    StringComparison.OrdinalIgnoreCase);

                await CopyResponseHeadersAsync(response);

                // 首次连接确定恢复标识并挂心跳。
                if (attempt == 0)
                {
                    state.RequestId = response.Headers.TryGetValues("X-Stream-Request-Id", out var ids)
                        ? ids.FirstOrDefault()
                        : null;
                    if (_options.HeartbeatSeconds > 0)
                    {
                        Response.Body = new SseHeartbeatStream(Response.Body, _options.HeartbeatSeconds);
                    }
                }

                if (!isStreaming)
                {
                    // 非流式：单次透传，不做续传。
                    await response.Content.CopyToAsync(Response.Body, cancellationToken);
                    return;
                }

                var outcome = await PumpStreamAsync(response, state, cancellationToken);
                if (outcome != PumpOutcome.Broken)
                {
                    return;
                }

                // 断线：重连（带最后已下发帧序号）。
                attempt++;
                if (attempt > _options.MaxReconnects || state.DoneSeen || state.RequestId is null)
                {
                    await WriteTailAsync("中继上游流中断");
                    return;
                }

                _logger.LogWarning("中继断线，第 {Attempt} 次重连（resume={RequestId}:{Seq}）", attempt, state.RequestId, state.LastSequence);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // 首次/重连请求本身失败（网络不可达等）。
            if (current is null)
            {
                await WriteRelayFailureAsync(null, $"中继上游不可达: {ex.GetType().Name}", cancellationToken);
                return;
            }

            // 传输出错中断：尝试重连（若还有预算且未完成）。
            attempt++;
            if (attempt > _options.MaxReconnects || state.DoneSeen || state.RequestId is null)
            {
                await WriteTailAsync($"中继上游传输错误: {ex.GetType().Name}");
                return;
            }

            _logger.LogWarning("中继传输错误，第 {Attempt} 次重连（resume={RequestId}:{Seq}）", attempt, state.RequestId, state.LastSequence);
        }
        finally
        {
            current?.Dispose();
        }
    }

    private enum PumpOutcome
    {
        Completed,
        DoneTruncated,
        Broken,
    }

    private async Task<PumpOutcome> PumpStreamAsync(HttpResponseMessage response, RelayStreamState state, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[64 * 1024];
        byte[]? pending = null;

        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
            {
                return state.DoneSeen ? PumpOutcome.Completed : PumpOutcome.Broken;
            }

            if (read == 0)
            {
                return state.DoneSeen ? PumpOutcome.Completed : PumpOutcome.Broken;
            }

            var frames = SseFrameSplitter.Split(buffer.AsMemory(0, read), ref pending);
            foreach (var frame in frames)
            {
                state.LastSequence++;
                if (state.DoneSeen)
                {
                    // 已见 [DONE]：丢弃新流重复段。
                    continue;
                }

                var text = Encoding.UTF8.GetString(frame);
                if (text.Contains("data: [DONE]", StringComparison.Ordinal))
                {
                    state.DoneSeen = true;
                }

                await Response.Body.WriteAsync(frame, cancellationToken);
            }

            await Response.Body.FlushAsync(cancellationToken);
        }
    }

    private HttpRequestMessage BuildRequest(string accessToken, string wirePath, string query, byte[] body, string? resumeHeader)
    {
        var request = new HttpRequestMessage(new HttpMethod(Request.Method), wirePath + query);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (!string.IsNullOrEmpty(resumeHeader))
        {
            request.Headers.TryAddWithoutValidation("X-Stream-Resume", resumeHeader);
        }

        if (Request.Headers.TryGetValue("x-api-key", out var apiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey.ToString());
        }

        request.Content = new ByteArrayContent(body);
        if (Request.ContentType is { Length: > 0 } contentType)
        {
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        return request;
    }

    private async Task CopyResponseHeadersAsync(HttpResponseMessage response)
    {
        Response.StatusCode = (int)response.StatusCode;
        foreach (var (key, values) in response.Headers)
        {
            if (key.StartsWith("X-", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Access-Control-Allow-Origin", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var value in values)
                {
                    Response.Headers.Append(key, value);
                }
            }
        }

        if (response.Content.Headers.ContentType is { } contentType)
        {
            Response.ContentType = contentType.ToString();
        }
    }

    private async Task WriteRelayFailureAsync(HttpResponseMessage? response, string? message, CancellationToken cancellationToken)
    {
        if (response is not null)
        {
            Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } contentType)
            {
                Response.ContentType = contentType.ToString();
            }

            await response.Content.CopyToAsync(Response.Body, cancellationToken);
            return;
        }

        Response.StatusCode = StatusCodes.Status502BadGateway;
        await Response.WriteAsJsonAsync(new
        {
            error = new
            {
                message = message ?? "中继上游不可达",
                type = "api_error",
                code = "relay_upstream_unreachable"
            }
        }, cancellationToken);
    }

    private async Task WriteTailAsync(string message)
    {
        // 流已启动，无法改状态码：以 SSE 注释形式收尾并结束。
        await Response.Body.WriteAsync(Encoding.UTF8.GetBytes($": relay-error {message}\n\n"));
        await Response.Body.FlushAsync();
    }

    private static string? ExtractAccessToken(HttpRequest request)
    {
        if (request.Headers.TryGetValue("Authorization", out var auth) && auth.Count > 0)
        {
            var value = auth[0] ?? string.Empty;
            if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return value["Bearer ".Length..].Trim();
            }
        }

        return null;
    }

    private sealed class RelayStreamState
    {
        public string? RequestId { get; set; }
        public int LastSequence { get; set; }
        public bool DoneSeen { get; set; }
        public string? ResumeHeader => RequestId is null ? null : $"{RequestId}:{LastSequence}";
    }
}