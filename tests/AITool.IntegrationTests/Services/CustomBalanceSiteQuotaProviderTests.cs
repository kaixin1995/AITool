using System.Net;
using AITool.Application.Sites;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.Services;

/// <summary>
/// 自定义余额供应商 HTTP 行为测试：GET/POST、Bearer 鉴权、表达式提取、
/// 401/403 → 密钥失效映射、缓存快照对称性、匹配规则。桩 HttpMessageHandler 拦截，无真实网络。
/// </summary>
public sealed class CustomBalanceSiteQuotaProviderTests
{
    private sealed record CapturedRequest(HttpMethod Method, Uri Url, string? Authorization);

    private sealed class StubHandler(Func<CapturedRequest, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public CapturedRequest? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = new CapturedRequest(
                request.Method,
                request.RequestUri ?? new Uri("about:blank"),
                request.Headers.Authorization?.ToString());
            return Task.FromResult(responder(LastRequest));
        }
    }

    private static readonly SiteQuotaBalanceConfig NewApiConfig = new(
        "https://panel.example.com/api/user/self",
        "GET",
        "$.data.quota / 500000",
        "USD");

    private static CustomBalanceSiteQuotaProvider CreateProvider(
        Func<CapturedRequest, HttpResponseMessage> responder, out StubHandler handler)
    {
        handler = new StubHandler(responder);
        return new CustomBalanceSiteQuotaProvider(
            new HttpClient(handler),
            NullLogger<CustomBalanceSiteQuotaProvider>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    private static SiteQuotaSiteContext Context(SiteQuotaBalanceConfig? balance, string baseUrl = "https://panel.example.com/v1")
        => new(baseUrl, balance);

    [Fact]
    public async Task Query_Extracts_Balance_With_Bearer_Auth()
    {
        var provider = CreateProvider(_ => Json(HttpStatusCode.OK,
            """{ "success": true, "data": { "quota": 5000000 } }"""), out var handler);

        var result = await provider.QueryAsync(Context(NewApiConfig), "sk-panel-token", CancellationToken.None);

        handler.LastRequest!.Method.Should().Be(HttpMethod.Get);
        handler.LastRequest.Url.AbsoluteUri.Should().Be("https://panel.example.com/api/user/self");
        handler.LastRequest.Authorization.Should().Be("Bearer sk-panel-token");

        result.Success.Should().BeTrue();
        result.Level.Should().Be("自定义余额");
        result.Balances.Should().ContainSingle();
        result.Balances[0].Currency.Should().Be("USD");
        result.Balances[0].TotalBalance.Should().Be(10);
    }

    [Fact]
    public async Task Query_Post_Method_Sends_Json_Body()
    {
        var postConfig = NewApiConfig with { Method = "POST" };
        var provider = CreateProvider(_ => Json(HttpStatusCode.OK, """{ "data": { "quota": 250000 } }"""),
            out var handler);

        var result = await provider.QueryAsync(Context(postConfig), "k", CancellationToken.None);

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        result.Success.Should().BeTrue();
        result.Balances[0].TotalBalance.Should().Be(0.5m);
    }

    [Fact]
    public async Task Query_Unauthorized_Maps_To_Credential_Invalid()
    {
        var provider = CreateProvider(_ => Json(HttpStatusCode.Unauthorized, """{"message":"无效的令牌"}"""), out _);

        var result = await provider.QueryAsync(Context(NewApiConfig), "bad", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.CredentialInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task Query_Expression_Failure_Is_Deterministic_Error()
    {
        var provider = CreateProvider(_ => Json(HttpStatusCode.OK, """{ "data": { "other": 1 } }"""), out _);

        var result = await provider.QueryAsync(Context(NewApiConfig), "k", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.CredentialInvalid.Should().BeFalse();
        result.Error.Should().Contain("quota");
    }

    [Fact]
    public void RawJson_Snapshot_Contains_Only_Balance_And_Currency()
    {
        // /api/user/self 等接口可能含邮箱等账号信息，落库快照必须是规范化后的余额，不是原始响应。
        var provider = CreateProvider(_ => Json(HttpStatusCode.OK,
            """{ "data": { "quota": 5000000, "user": { "email": "secret@x.com" } } }"""), out _);

        var rawJson = provider.ParseCached("""{"balance":10,"currency":"USD"}""");

        rawJson.Should().NotBeNull();
        rawJson!.Balances.Should().ContainSingle();
        rawJson.Balances[0].TotalBalance.Should().Be(10);
        rawJson.Balances[0].Currency.Should().Be("USD");
    }

    [Fact]
    public void ParseCached_Roundtrips_Normalized_Snapshot()
    {
        var provider = CreateProvider(_ => Json(HttpStatusCode.OK, """{"data":{"quota":123456}}"""), out _);
        provider.ParseCached("""{"balance":0.2469,"currency":"CNY"}""").Should().NotBeNull();
        provider.ParseCached("""{"data":{"quota":123456}}""").Should().BeNull("原始面板响应不是快照格式");
        provider.ParseCached("not json").Should().BeNull();
    }

    [Fact]
    public void Matching_Follows_Config_Presence_Not_Host()
    {
        var provider = CreateProvider(_ => Json(HttpStatusCode.OK, "{}"), out _);

        provider.MatchesSite(Context(NewApiConfig)).Should().BeTrue();
        provider.MatchesSite(Context(null, "https://panel.example.com/v1")).Should().BeFalse("无配置不接管");
        provider.MatchesBaseUrl("https://anything.example.com").Should().BeFalse("不按域名匹配");
    }

    [Fact]
    public async Task Plain_Query_Without_Context_Is_Rejected()
    {
        var provider = CreateProvider(_ => Json(HttpStatusCode.OK, "{}"), out _);
        var act = () => provider.QueryAsync("https://panel.example.com/v1", "k", CancellationToken.None);
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public void BalanceConfig_Parsing_Normalizes_Method_And_Defaults()
    {
        SiteQuotaBalanceConfig.TryParse(null).Should().BeNull();
        SiteQuotaBalanceConfig.TryParse("").Should().BeNull();
        SiteQuotaBalanceConfig.TryParse("not json").Should().BeNull();
        SiteQuotaBalanceConfig.TryParse("""{"url":""}""").Should().BeNull();

        var config = SiteQuotaBalanceConfig.TryParse(
            """{"url":"https://x.com/api","method":"post","expression":"$.a"}""");
        config.Should().NotBeNull();
        config!.Method.Should().Be("POST");
        config.Currency.Should().Be("USD");
        config.Expression.Should().Be("$.a");

        var invalidMethod = SiteQuotaBalanceConfig.TryParse(
            """{"url":"https://x.com/api","method":"DELETE"}""");
        invalidMethod!.Method.Should().Be("GET", "非 GET/POST 归一为 GET");
    }
}
