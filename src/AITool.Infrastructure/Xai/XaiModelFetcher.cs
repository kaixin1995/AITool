using System.Net.Http.Headers;
using System.Text.Json;
using AITool.Application.Xai;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Xai;

/// <summary>
/// xAI 上游模型目录：GET https://api.x.ai/v1/models（OpenAI 格式 data[]，Bearer 鉴权），
/// 以内置默认清单为基准归并上游新增；xAI 无别名体系，公开名 == 上游 ID。
/// </summary>
public sealed class XaiModelFetcher : IXaiModelFetcher
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<XaiModelFetcher> _logger;

    public XaiModelFetcher(HttpClient httpClient, ILogger<XaiModelFetcher> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(string Slug, string DisplayName)>> FetchAsync(string accessToken, CancellationToken ct)
    {
        var catalog = new List<(string Slug, string DisplayName)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slug, displayName) in XaiConstants.DefaultModels)
        {
            if (seen.Add(slug))
            {
                catalog.Add((slug, displayName));
            }
        }

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return catalog;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{XaiConstants.ApiBaseUrl}/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());

            using var response = await _httpClient.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                        {
                            catalog.Add((id, id));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch xAI models from upstream /v1/models, fallback to default catalog");
        }

        return catalog;
    }
}
