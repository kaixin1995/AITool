using System.Text;
using System.Text.Json;
using AITool.Application.Sites;
using AITool.Infrastructure.Sites;

namespace AITool.Web.Services;

/// <summary>
/// 自定义余额供应商（ISiteQuotaProvider，ProviderKey="custom-balance"）。
/// <para>
/// 面向 new-api / sub2api / one-api 系中转面板：站点配置了 QuotaBalanceJson
///（url + method + expression + currency）即由本供应商接管额度查询——
/// 密钥以 Bearer 携带打到面板的余额接口，用表达式从响应中算出余额数值。
/// RawJson 落库为规范化快照（仅余额数值与币种），不保存面板原始响应
///（/api/user/self 等接口可能含邮箱等账号信息）。
/// </para>
/// </summary>
public sealed class CustomBalanceSiteQuotaProvider : ISiteQuotaProvider
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CustomBalanceSiteQuotaProvider> _logger;

    public CustomBalanceSiteQuotaProvider(HttpClient httpClient, ILogger<CustomBalanceSiteQuotaProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public string ProviderKey => "custom-balance";

    public string ProviderLabel => "自定义余额";

    /// <summary>自定义余额不按域名匹配：任何配置了余额查询的站点都接管。</summary>
    public bool MatchesBaseUrl(string baseUrl) => false;

    public bool MatchesSite(SiteQuotaSiteContext site) => site.Balance is not null;

    public Task<SiteQuotaQueryResult> QueryAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
        => throw new NotSupportedException("自定义余额查询需要站点上下文（QuotaBalanceJson）");

    public async Task<SiteQuotaQueryResult> QueryAsync(SiteQuotaSiteContext site, string apiKey, CancellationToken cancellationToken)
    {
        var config = site.Balance!;
        try
        {
            using var request = new HttpRequestMessage(
                config.Method == "POST" ? HttpMethod.Post : HttpMethod.Get,
                config.Url);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey.Trim());
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (request.Method == HttpMethod.Post)
            {
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }

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

            if (!CustomBalanceExpression.TryEvaluate(config.Expression, body, out var balance, out var error))
            {
                return new SiteQuotaQueryResult { Success = false, Error = error };
            }

            // 规范化快照落库：只保存余额与币种，不保存面板原始响应。
            var rawJson = JsonSerializer.Serialize(new { balance, currency = config.Currency });
            return new SiteQuotaQueryResult
            {
                Success = true,
                Level = "自定义余额",
                RawJson = rawJson,
                Balances = [new SiteQuotaBalanceInfo(config.Currency, Round(balance), null, null)],
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogInformation("Custom balance query failed: {Message}", ex.Message);
            return new SiteQuotaQueryResult { Success = false, Error = $"网络错误：{ex.Message}" };
        }
    }

    public SiteQuotaQueryResult? ParseCached(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("balance", out var balance)
                || balance.ValueKind != JsonValueKind.Number
                || !balance.TryGetDouble(out var value))
            {
                return null;
            }

            var currency = root.TryGetProperty("currency", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : "USD";
            return new SiteQuotaQueryResult
            {
                Success = true,
                Level = "自定义余额",
                RawJson = rawJson,
                Balances = [new SiteQuotaBalanceInfo(currency!, Round(value), null, null)],
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>余额展示保留 4 位小数，避免浮点尾噪。</summary>
    private static decimal Round(double value) => (decimal)Math.Round(value, 4, MidpointRounding.AwayFromZero);

    private static string Truncate(string text, int maxLength = 200)
        => string.IsNullOrEmpty(text) || text.Length <= maxLength ? text : text[..maxLength] + "…";
}
