using System.Net.Http.Json;
using AITool.Application.SiteCatalog;
using AITool.Application.Networking;
using AITool.Application.Proxy;
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
    private readonly IForwardNetworkPolicyResolver? _networkPolicyResolver;
    private readonly INetworkTransport? _networkTransport;

    public OpenAiSiteCatalogClient(HttpClient httpClient, SiteKeySelector siteKeySelector,
        IForwardNetworkPolicyResolver? networkPolicyResolver = null, INetworkTransport? networkTransport = null)
    {
        _httpClient = httpClient;
        _siteKeySelector = siteKeySelector;
        _networkPolicyResolver = networkPolicyResolver;
        _networkTransport = networkTransport;
    }

    /// <summary>
    /// 通过 GET /v1/models 拉取站点支持的模型列表
    /// </summary>
    public async Task<IReadOnlyList<string>> GetModelsAsync(Site site, CancellationToken cancellationToken)
    {
        var credential = await _siteKeySelector.GetActiveCredentialAsync(site.Id, cancellationToken);
        var policyRequest = new ProxyForwardRequest
        {
            TargetBaseUrl = site.BaseUrl,
            TargetEndpointPathMode = site.EndpointPathMode,
            TargetApiKey = string.IsNullOrEmpty(credential.ApiKey) ? site.ApiKey : credential.ApiKey,
            EgressProxyUrl = credential.EgressProxyUrl ?? site.EgressProxyUrl,
            AccountIdentity = credential.AccountIdentity
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_httpClient.Timeout != Timeout.InfiniteTimeSpan) timeout.CancelAfter(_httpClient.Timeout);
        var networkToken = timeout.Token;
        var client = _httpClient;
        INetworkTransportLease? acquired = null;
        if (_networkPolicyResolver is not null)
        {
            var policy = await _networkPolicyResolver.ResolveAsync(policyRequest, networkToken);
            if (!policy.UseDefaultClient)
            {
                acquired = await (_networkTransport ?? throw new InvalidOperationException("Network transport is unavailable."))
                    .AcquireAsync(policy.Configuration, policy.IsolationKey, "site-catalog", networkToken);
                client = acquired.Client;
            }
        }
        else if (policyRequest.AccountIdentity is not null || !string.IsNullOrWhiteSpace(policyRequest.EgressProxyUrl))
        {
            throw new InvalidOperationException("Network policy resolver is unavailable.");
        }
        await using var lease = acquired;
        using var request = new HttpRequestMessage(HttpMethod.Get, SiteEndpointPathResolver.BuildUrl(site.BaseUrl, site.EndpointPathMode, "models"));
        request.Headers.Add("Authorization", $"Bearer {policyRequest.TargetApiKey}");

        using var response = await client.SendAsync(request, networkToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OpenAiModelsResponse>(networkToken);
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
