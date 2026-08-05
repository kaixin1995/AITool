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

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ApiException(
                $"流式请求失败（HTTP {(int)response.StatusCode}）",
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
