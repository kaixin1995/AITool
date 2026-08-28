using System.Net.Http.Headers;
using System.Text.Json;
using AITool.Application.Kimi;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Kimi;

/// <summary>
/// Kimi 上游模型目录。
/// 以 CLIProxyAPI 注册表（KimiConstants.DefaultModels，对外公开名）为基准目录，
/// 并尝试拉取上游 /v1/models 归并上游新增模型：已知 ID 反查为公开名，未知 ID 原样保留。
/// </summary>
public sealed class KimiModelFetcher : IKimiModelFetcher
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<KimiModelFetcher> _logger;

    public KimiModelFetcher(HttpClient httpClient, ILogger<KimiModelFetcher> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(string Slug, string DisplayName)>> FetchAsync(string accessToken, string? deviceId, CancellationToken ct)
    {
        // 基准目录：与 CLIProxyAPI 对外展示一致的完整清单（即使上游 /v1/models 缺项也保留）。
        var catalog = new List<(string Slug, string DisplayName)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slug, displayName) in KimiConstants.DefaultModels)
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
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{KimiConstants.ApiBaseUrl}/v1/models");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
            request.Headers.TryAddWithoutValidation("User-Agent", KimiConstants.ClientUserAgent);
            request.Headers.TryAddWithoutValidation("X-Msh-Platform", "kimi_cli");
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                request.Headers.TryAddWithoutValidation("X-Msh-Device-Id", deviceId.Trim());
            }

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
                        // 上游返回的是规范 ID（如 k2.5、kimi-for-coding），反查为对外公开名后归并进目录。
                        var upstream = KimiModelNormalizer.NormalizeUpstreamModel(id);
                        if (string.IsNullOrWhiteSpace(upstream))
                        {
                            continue;
                        }
                        var publicName = KimiModelNormalizer.PublicModelNameFromUpstream(upstream);
                        if (seen.Add(publicName))
                        {
                            catalog.Add((publicName, publicName));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch Kimi models from upstream /v1/models, fallback to default catalog");
        }

        return catalog;
    }
}
