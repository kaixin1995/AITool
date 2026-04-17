using System.Net.Http.Json;
using AITool.Application.SiteCatalog;
using AITool.Domain.Sites;

namespace AITool.Infrastructure.OpenAI;

// OpenAI 兼容站点的模型目录客户端实现
public sealed class OpenAiSiteCatalogClient : ISiteCatalogClient
{
    private readonly HttpClient _httpClient;

    public OpenAiSiteCatalogClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    // 通过 GET /v1/models 拉取站点支持的模型列表
    public async Task<IReadOnlyList<string>> GetModelsAsync(Site site, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{site.BaseUrl.TrimEnd('/')}/v1/models");
        request.Headers.Add("Authorization", $"Bearer {site.ApiKey}");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OpenAiModelsResponse>(cancellationToken);
        return result?.Data?.Select(m => m.Id).ToList() ?? [];
    }

    // OpenAI /v1/models 响应结构
    private sealed class OpenAiModelsResponse
    {
        public List<OpenAiModelItem>? Data { get; set; }
    }

    private sealed class OpenAiModelItem
    {
        public string Id { get; set; } = string.Empty;
    }
}
