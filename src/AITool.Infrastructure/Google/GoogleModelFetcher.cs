using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AITool.Application.Google;

namespace AITool.Infrastructure.Google;

/// <summary>
/// Google 上游模型清单拉取实现。Antigravity 端点与解析对齐 gcli2api
/// （src/api/antigravity.py fetch_available_models）。
/// </summary>
public sealed class GoogleModelFetcher : IGoogleModelFetcher
{
    private readonly HttpClient _httpClient;

    public GoogleModelFetcher(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(string Slug, string DisplayName)>> FetchAsync(string accountKind, string accessToken, CancellationToken ct)
    {
        return await FetchAntigravityModelsAsync(accessToken, ct);
    }

    private async Task<IReadOnlyList<(string Slug, string DisplayName)>> FetchAntigravityModelsAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{GoogleAccountKinds.GetBaseUrl(GoogleAccountKinds.Antigravity)}/v1internal:fetchAvailableModels")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("User-Agent", GoogleAccountKinds.AntigravityUserAgent);
        request.Headers.TryAddWithoutValidation("requestId", $"req-{Guid.NewGuid():N}");
        request.Headers.TryAddWithoutValidation("requestType", "agent");

        using var response = await _httpClient.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"fetchAvailableModels returned {(int)response.StatusCode}: {body}");
        }

        var models = new List<(string Slug, string DisplayName)>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("models", out var modelsElement) && modelsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in modelsElement.EnumerateObject())
            {
                models.Add((property.Name, property.Name));
            }

            // Antigravity 常用别名与变体补齐（对齐 gcli2api 与 ProxyProtocolBridge）：
            // 1. 存在 claude-sonnet-4-6 时补充其 thinking 变体
            if (modelsElement.TryGetProperty("claude-sonnet-4-6", out _) && !modelsElement.TryGetProperty("claude-sonnet-4-6-thinking", out _))
            {
                models.Add(("claude-sonnet-4-6-thinking", "claude-sonnet-4-6-thinking"));
            }

            // 2. 存在 gemini-3.7-flash-tiered 时补充常用的 high / medium / low / 基础别名
            if (modelsElement.TryGetProperty("gemini-3.7-flash-tiered", out _))
            {
                if (!modelsElement.TryGetProperty("gemini-3.7-flash-high", out _))
                    models.Add(("gemini-3.7-flash-high", "gemini-3.7-flash-high"));
                if (!modelsElement.TryGetProperty("gemini-3.7-flash-medium", out _))
                    models.Add(("gemini-3.7-flash-medium", "gemini-3.7-flash-medium"));
                if (!modelsElement.TryGetProperty("gemini-3.7-flash-low", out _))
                    models.Add(("gemini-3.7-flash-low", "gemini-3.7-flash-low"));
                if (!modelsElement.TryGetProperty("gemini-3.7-flash", out _))
                    models.Add(("gemini-3.7-flash", "gemini-3.7-flash"));
            }

            // 3. 存在 gemini-pro-agent 时补充 gemini-3.1-pro-high 别名
            if (modelsElement.TryGetProperty("gemini-pro-agent", out _) && !modelsElement.TryGetProperty("gemini-3.1-pro-high", out _))
            {
                models.Add(("gemini-3.1-pro-high", "gemini-3.1-pro-high"));
            }
        }

        return models;
    }
}
