using AITool.Application.Sites;
using AITool.Infrastructure.Zhipu;

namespace AITool.Web.Services;

/// <summary>
/// 智谱 GLM 编程套餐额度供应商（ISiteQuotaProvider，ProviderKey="zhipu"）。
/// <para>
/// 数据源为 GET {base}/api/monitor/usage/quota/limit（逆向自 bigmodel.cn 官网控制台监控
/// 接口；国内站 open.bigmodel.cn 与国际站 api.z.ai 共用同一后端）。请求头与推理侧不同：
/// Authorization 直接携带 API key，<b>不加 Bearer 前缀</b>。
/// 个人版无窗口或失败（且非密钥被拒）时，回退团队版端点：同路径 <c>?type=2</c>，
/// 先打本站根域名，再打 bigmodel.cn 业务根（对齐 ZCode 客户端行为；组织/项目头暂不携带，
/// 上游可凭 key 推断所属团队）。团队命中时套餐展示为「GLM Coding Team」。
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
        var quotaBase = ZhipuQuotaParser.ResolveQuotaBase(baseUrl).TrimEnd('/');
        var personal = await SendQuotaRequestAsync(quotaBase + ZhipuQuotaParser.QuotaPath, apiKey, cancellationToken);

        // 密钥被拒（HTTP 401/403 或 200 信封 code 1000–1005）时团队端点同样会被拒，不重试；
        // 其余失败或个人版无窗口（团队版 key 在个人端点拿不到数据）时尝试团队端点。
        if (!personal.CredentialInvalid && !(personal.Success && personal.Windows.Count > 0))
        {
            foreach (var root in ResolveTeamRoots(baseUrl))
            {
                var teamUrl = root.TrimEnd('/') + ZhipuQuotaParser.QuotaPath + "?type=2";
                var team = await SendQuotaRequestAsync(teamUrl, apiKey, cancellationToken);
                if (team.Success && team.Windows.Count > 0)
                {
                    return team with { Level = string.IsNullOrWhiteSpace(team.Level) ? TeamPlanLabel : team.Level };
                }
            }
        }

        return personal;
    }

    /// <summary>团队版套餐展示名（团队端点命中且响应未携带 level 时使用）。</summary>
    private const string TeamPlanLabel = "GLM Coding Team";

    /// <summary>
    /// 团队端点尝试的主机顺序：本站根域名在前；bigmodel.cn 域再补业务根 bigmodel.cn
    ///（ZCode 客户端的问法），z.ai 域个人根即业务根，无需第二个。
    /// </summary>
    private static IReadOnlyList<string> ResolveTeamRoots(string baseUrl)
    {
        var own = ZhipuQuotaParser.ResolveQuotaBase(baseUrl);
        if (Uri.TryCreate(own, UriKind.Absolute, out var uri)
            && uri.Host.EndsWith(".bigmodel.cn", StringComparison.Ordinal))
        {
            return [own, "https://bigmodel.cn"];
        }

        return [own];
    }

    private async Task<SiteQuotaQueryResult> SendQuotaRequestAsync(string url, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // 智谱额度接口鉴权与推理侧不同：Authorization 直接携带 API key，不加 Bearer 前缀。
            request.Headers.TryAddWithoutValidation("Authorization", apiKey.Trim());
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
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
            // 智谱会以 HTTP 200 + 平台信封（success:false + code）回绝请求；其中 1000–1005
            // 是密钥本身被拒（不存在/缺失/过期/需二次验证），映射为密钥失效状态。
            return new SiteQuotaQueryResult
            {
                Success = false,
                Error = parsed.IsKeyRefusal
                    ? $"密钥被智谱拒绝（code {parsed.ErrorCode}）：{parsed.Error}"
                    : $"接口返回错误{(parsed.ErrorCode is { } code ? $"（code {code}）" : "")}：{parsed.Error}",
                CredentialInvalid = parsed.IsKeyRefusal,
            };
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
