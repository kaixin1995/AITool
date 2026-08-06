using System.Net.Http.Json;
using AITool.Application.SiteCatalog;
using AITool.Application.Sites;
using AITool.Domain.Sites;
using AITool.Infrastructure.Sites;

namespace AITool.Infrastructure.OpenAI;

/// <summary>
/// OpenAI 兼容站点的模型目录客户端实现
/// </summary>
public sealed class OpenAiSiteCatalogClient : ISiteCatalogClient
{
    /// <summary>
    /// 用于请求站点 /v1/models 接口的 HTTP 客户端
    /// </summary>
    private readonly HttpClient _httpClient;
    /// <summary>
    /// 站点密钥选择器，取站点活动密钥（多 Key 站点用优先级最高的启用项）。
    /// </summary>
    private readonly SiteKeySelector _siteKeySelector;

    /// <summary>
    /// 注入 HTTP 客户端和站点密钥选择器
    /// </summary>
    public OpenAiSiteCatalogClient(HttpClient httpClient, SiteKeySelector siteKeySelector)
    {
        _httpClient = httpClient;
        _siteKeySelector = siteKeySelector;
    }

    /// <summary>
    /// 通过 GET /v1/models 拉取站点支持的模型列表
    /// </summary>
    public async Task<IReadOnlyList<string>> GetModelsAsync(Site site, CancellationToken cancellationToken)
    {
        // 取站点活动密钥：多 Key 站点用优先级最高的启用项，没有 SiteKey 时回退 site.ApiKey（兼容 Codex/未迁移）。
        var apiKey = await _siteKeySelector.GetActiveKeyAsync(site.Id, cancellationToken);
        if (string.IsNullOrEmpty(apiKey))
        {
            apiKey = site.ApiKey;
        }

        var request = new HttpRequestMessage(HttpMethod.Get, SiteEndpointPathResolver.BuildUrl(site.BaseUrl, site.EndpointPathMode, "models"));
        request.Headers.Add("Authorization", $"Bearer {apiKey}");

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
        /// /v1/models 返回的模型列表
        /// </summary>
        public List<OpenAiModelItem>? Data { get; set; }
    }

    /// <summary>
    /// OpenAI /v1/models 响应中的单个模型条目
    /// </summary>
    private sealed class OpenAiModelItem
    {
        /// <summary>
        /// 模型标识符
        /// </summary>
        public string Id { get; set; } = string.Empty;
    }
}
