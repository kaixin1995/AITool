using System.Net.Http.Json;
using AITool.Application.SiteCatalog;
using AITool.Domain.Sites;

namespace AITool.Infrastructure.OpenAI;

/// <summary>
/// OpenAI 兼容站点的模型目录客户端实现
/// </summary>
public sealed class OpenAiSiteCatalogClient : ISiteCatalogClient
{
    /// <summary>
    /// 字段 _httpClient。
    /// </summary>
    private readonly HttpClient _httpClient;

    /// <summary>
    /// 初始化 OpenAiSiteCatalogClient。
    /// </summary>
    public OpenAiSiteCatalogClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// 通过 GET /v1/models 拉取站点支持的模型列表
    /// </summary>
    public async Task<IReadOnlyList<string>> GetModelsAsync(Site site, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{site.BaseUrl.TrimEnd('/')}/v1/models");
        request.Headers.Add("Authorization", $"Bearer {site.ApiKey}");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OpenAiModelsResponse>(cancellationToken);
        return result?.Data?.Select(m => m.Id).ToList() ?? [];
    }

    /// <summary>
    /// OpenAI /v1/models 响应结构
    /// </summary>
    private sealed class OpenAiModelsResponse
    {
        /// <summary>
        /// 属性 Data。
        /// </summary>
        public List<OpenAiModelItem>? Data { get; set; }
    }

    /// <summary>
    /// 类 OpenAiModelItem。
    /// </summary>
    private sealed class OpenAiModelItem
    {
        /// <summary>
        /// 属性 Id。
        /// </summary>
        public string Id { get; set; } = string.Empty;
    }
}
