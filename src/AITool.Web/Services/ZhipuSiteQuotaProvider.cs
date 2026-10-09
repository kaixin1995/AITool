using AITool.Application.Sites;
using AITool.Infrastructure.Zhipu;

namespace AITool.Web.Services;

/// <summary>
/// 智谱 GLM 编程套餐额度供应商（ISiteQuotaProvider，ProviderKey="zhipu"）。
/// <para>
/// 数据源为 GET {base}/api/monitor/usage/quota/limit（逆向自 bigmodel.cn 官网控制台监控
/// 接口；国内站 open.bigmodel.cn 与国际站 api.z.ai 共用同一后端）。请求头与推理侧不同：
/// Authorization 直接携带 API key，<b>不加 Bearer 前缀</b>。
/// 团队版（同路径 ?type=2 + bigmodel-organization/bigmodel-project 头）暂未支持。
/// </para>
/// </summary>
public sealed class ZhipuSiteQuotaProvider : ISiteQuotaProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ZhipuSiteQuotaProvider> _logger;

    public ZhipuSiteQuotaProvider(HttpClient httpClient, ILogger<ZhipuSiteQuotaProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string ProviderKey => "zhipu";

    public string ProviderLabel => "智谱 GLM";

    public bool MatchesBaseUrl(string baseUrl) => ZhipuQuotaParser.MatchesBaseUrl(baseUrl);

    public async Task<SiteQuotaQueryResult> QueryAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        var url = ZhipuQuotaParser.ResolveQuotaBase(baseUrl).TrimEnd('/') + ZhipuQuotaParser.QuotaPath;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // 智谱额度接口鉴权与推理侧不同：Authorization 直接携带 API key，不加 Bearer 前缀。
            request.Headers.TryAddWithoutValidation("Authorization", apiKey.Trim());
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
            {
                return new SiteQuotaQueryResult
                {
                    Success = false,
                    Error = $"认证失败（HTTP {(int)response.StatusCode}），请检查密钥",
                    CredentialInvalid = true,
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new SiteQuotaQueryResult
                {
                    Success = false,
                    Error = $"上游返回 {(int)response.StatusCode}：{Truncate(body)}",
                };
            }

            return ParseBody(body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Zhipu quota query failed: {Message}", ex.Message);
            return new SiteQuotaQueryResult { Success = false, Error = $"网络错误：{ex.Message}" };
        }
    }

    public SiteQuotaQueryResult? ParseCached(string rawJson)
    {
        var parsed = ZhipuQuotaParser.Parse(rawJson);
        if (parsed is null || parsed.Windows.Count == 0)
        {
            return null;
        }

        return new SiteQuotaQueryResult
        {
            Success = true,
            Level = parsed.Level,
            RawJson = rawJson,
            Windows = parsed.Windows,
        };
    }

    /// <summary>解析成功响应体：业务错误（success=false）与无窗口都算确定性失败。</summary>
    private static SiteQuotaQueryResult ParseBody(string body)
    {
        var parsed = ZhipuQuotaParser.Parse(body);
        if (parsed is null)
        {
            return new SiteQuotaQueryResult { Success = false, Error = "响应无法解析为额度数据" };
        }

        if (parsed.Error is not null)
        {
            return new SiteQuotaQueryResult { Success = false, Error = $"接口返回错误：{parsed.Error}" };
        }

        if (parsed.Windows.Count == 0)
        {
            return new SiteQuotaQueryResult { Success = false, Error = "响应中没有可用额度数据" };
        }

        return new SiteQuotaQueryResult
        {
            Success = true,
            Level = parsed.Level,
            RawJson = body,
            Windows = parsed.Windows,
        };
    }

    private static string Truncate(string text, int maxLength = 200)
        => string.IsNullOrEmpty(text) || text.Length <= maxLength ? text : text[..maxLength] + "…";
}
