using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AITool.Application.Google;

namespace AITool.Infrastructure.Google;

/// <summary>
/// Google 上游模型清单拉取实现。Antigravity 端点与解析对齐 gcli2api
/// （src/api/antigravity.py fetch_available_models）；GeminiCli 无动态清单接口，返回静态清单。
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
        if (string.Equals(GoogleAccountKinds.Normalize(accountKind), GoogleAccountKinds.Antigravity, StringComparison.OrdinalIgnoreCase))
        {
            return await FetchAntigravityModelsAsync(accessToken, ct);
        }

        // GeminiCli：与供给器共用静态清单。
        return GoogleAccountKinds.GeminiCliModels
            .Select(n => (n, n))
            .ToList();
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

            // gcli2api 同款补齐：存在 claude-sonnet-4-6 时补充其 thinking 变体。
            if (modelsElement.TryGetProperty("claude-sonnet-4-6", out _) && !modelsElement.TryGetProperty("claude-sonnet-4-6-thinking", out _))
            {
                models.Add(("claude-sonnet-4-6-thinking", "claude-sonnet-4-6-thinking"));
            }
        }

        return models;
    }
}
