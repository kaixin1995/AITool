using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AITool.Desktop.Services;

public sealed record SseEvent(string EventType, string Data);

public sealed class SseClient
{
    private readonly HttpClient _httpClient;
    private readonly TokenStore _tokenStore;
    private readonly ApiService _apiService;

    public SseClient(HttpClient httpClient, TokenStore tokenStore, ApiService apiService)
    {
        _httpClient = httpClient;
        _tokenStore = tokenStore;
        _apiService = apiService;
    }

    public async IAsyncEnumerable<SseEvent> StreamAsync(
        string path,
        object requestBody,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (response, error) = await SendSseRequestAsync(path, requestBody, cancellationToken);
        // 401 时刷新 token 重试一次。
        if (response is null && error is { StatusCode: 401 })
        {
            var refreshed = await _apiService.RefreshAccessTokenAsync(cancellationToken);
            if (refreshed)
            {
                (response, error) = await SendSseRequestAsync(path, requestBody, cancellationToken);
            }
        }

        if (response is null)
        {
            throw new ApiException(
                error?.Message ?? $"流式请求失败",
                string.Empty,
                error?.StatusCode ?? 0);
        }

        using (response)
        {
            // 校验 Content-Type 是否为 text/event-stream。
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new ApiException(
                    $"预期 SSE 响应，实际收到 {contentType ?? "未知"} 内容",
                    string.Empty,
                    (int)response.StatusCode);
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(responseStream);
            var block = new StringBuilder();
            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) break;

                if (line.Length == 0)
                {
                    var parsedEvent = ParseBlock(block.ToString());
                    block.Clear();
                    if (parsedEvent is not null)
                    {
                        yield return parsedEvent;
                    }

                    continue;
                }

                block.AppendLine(line);
            }

            var lastEvent = ParseBlock(block.ToString());
            if (lastEvent is not null)
            {
                yield return lastEvent;
            }
        }
    }

    /// <summary>
    /// 发起 SSE 请求，返回响应或错误信息。
    /// </summary>
    private async Task<(HttpResponseMessage? Response, ApiException? Error)> SendSseRequestAsync(
        string path, object requestBody, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _apiService.CreateRequestUri(path))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(requestBody, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        var accessToken = _tokenStore.Settings.AccessToken;
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        }

        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var statusCode = (int)response.StatusCode;
            response.Dispose();
            return (null, new ApiException($"流式请求失败（HTTP {statusCode}）", string.Empty, statusCode));
        }

        return (response, null);
    }

    private static SseEvent? ParseBlock(string block)
    {
        if (string.IsNullOrWhiteSpace(block)) return null;

        var eventType = "message";
        var data = new StringBuilder();
        foreach (var line in block.Split('\n'))
        {
            var normalizedLine = line.TrimEnd('\r');
            if (normalizedLine.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventType = normalizedLine[6..].Trim();
            }
            else if (normalizedLine.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(normalizedLine[5..].TrimStart());
            }
        }

        return data.Length == 0 ? null : new SseEvent(eventType, data.ToString());
    }
}
