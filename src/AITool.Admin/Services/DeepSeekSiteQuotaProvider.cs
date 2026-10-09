using AITool.Application.Sites;
using AITool.Infrastructure.DeepSeek;

namespace AITool.Admin.Services;

/// <summary>
/// DeepSeek 账户余额供应商（ISiteQuotaProvider，ProviderKey="deepseek"）。
/// <para>
/// 数据源为 GET https://api.deepseek.com/user/balance（官方文档化接口，Bearer 鉴权）。
/// DeepSeek 是按量计费，无套餐窗口，额度即余额（balance_infos：币种/总额/赠送/充值）。
/// 响应的 is_available（余额是否可用，如欠费停机）刻意不参与成功判定——本页只忠实
/// 展示金额，可用性由推理链路的实际报错呈现。
/// </para>
/// </summary>
public sealed class DeepSeekSiteQuotaProvider : ISiteQuotaProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<DeepSeekSiteQuotaProvider> _logger;

    public DeepSeekSiteQuotaProvider(HttpClient httpClient, ILogger<DeepSeekSiteQuotaProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string ProviderKey => "deepseek";

    public string ProviderLabel => "DeepSeek";

    public bool MatchesBaseUrl(string baseUrl) => DeepSeekBalanceParser.MatchesBaseUrl(baseUrl);

    public async Task<SiteQuotaQueryResult> QueryAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, DeepSeekBalanceParser.BalancePath);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());

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
            _logger.LogInformation("DeepSeek balance query failed: {Message}", ex.Message);
            return new SiteQuotaQueryResult { Success = false, Error = $"网络错误：{ex.Message}" };
        }
    }

    public SiteQuotaQueryResult? ParseCached(string rawJson)
    {
        var parsed = DeepSeekBalanceParser.Parse(rawJson);
        if (parsed.Error is not null || parsed.Balances.Count == 0)
        {
            return null;
        }

        return new SiteQuotaQueryResult
        {
            Success = true,
            RawJson = rawJson,
            Balances = parsed.Balances,
        };
    }

    /// <summary>解析成功响应体：结构不识别为确定性失败；balance_infos 空数组提示无数据。</summary>
    private static SiteQuotaQueryResult ParseBody(string body)
    {
        var parsed = DeepSeekBalanceParser.Parse(body);
        if (parsed.Error is not null)
        {
            return new SiteQuotaQueryResult { Success = false, Error = parsed.Error };
        }

        if (parsed.Balances.Count == 0)
        {
            return new SiteQuotaQueryResult { Success = false, Error = "响应中没有可用余额数据" };
        }

        return new SiteQuotaQueryResult
        {
            Success = true,
            RawJson = body,
            Balances = parsed.Balances,
        };
    }

    private static string Truncate(string text, int maxLength = 200)
        => string.IsNullOrEmpty(text) || text.Length <= maxLength ? text : text[..maxLength] + "…";
}
