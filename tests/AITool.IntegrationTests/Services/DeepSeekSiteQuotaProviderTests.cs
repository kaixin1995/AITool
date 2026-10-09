using System.Net;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.Services;

/// <summary>
/// DeepSeek 余额供应商 HTTP 行为测试：官方端点、Bearer 鉴权、401/403 → 密钥失效映射、
/// 缓存解析对称性。用桩 HttpMessageHandler 拦截，无真实网络。
/// </summary>
public sealed class DeepSeekSiteQuotaProviderTests
{
    private const string SuccessBody = """
        {
          "is_available": true,
          "balance_infos": [
            { "currency": "CNY", "total_balance": "88.60", "granted_balance": "8.60", "topped_up_balance": "80.00" }
          ]
        }
        """;

    private sealed record CapturedRequest(HttpMethod Method, Uri Url, string? Authorization);

    private sealed class StubHandler(Func<CapturedRequest, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public CapturedRequest? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = new CapturedRequest(
                request.Method,
                request.RequestUri ?? new Uri("about:blank"),
                request.Headers.Authorization is { } auth
                    ? $"{auth.Scheme} {auth.Parameter}"
                    : null);
            return Task.FromResult(responder(LastRequest));
        }
    }

    private static DeepSeekSiteQuotaProvider CreateProvider(Func<CapturedRequest, HttpResponseMessage> responder, out StubHandler handler)
    {
        handler = new StubHandler(responder);
        return new DeepSeekSiteQuotaProvider(
            new HttpClient(handler),
            NullLogger<DeepSeekSiteQuotaProvider>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task Query_Uses_Official_Balance_Endpoint_With_Bearer_Auth()
    {
        var provider = CreateProvider(_ => Json(HttpStatusCode.OK, SuccessBody), out var handler);

        // base_url 传什么都打官方端点（DeepSeek 只有一家，不按 base 分流）。
        var result = await provider.QueryAsync("https://api.deepseek.com", "sk-my-key", CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Get);
        handler.LastRequest.Url.AbsoluteUri.Should().Be("https://api.deepseek.com/user/balance");
        // DeepSeek 是标准 Bearer 鉴权（与智谱的裸 key 不同）。
        handler.LastRequest.Authorization.Should().Be("Bearer sk-my-key");

        result.Success.Should().BeTrue();
        result.Windows.Should().BeEmpty();
        result.Balances.Should().HaveCount(1);
        result.Balances[0].Currency.Should().Be("CNY");
        result.Balances[0].TotalBalance.Should().Be(88.60m);
        result.RawJson.Should().Be(SuccessBody);
    }

    [Fact]
    public async Task Query_Unauthorized_Maps_To_Credential_Invalid()
    {
        var provider = CreateProvider(
            _ => Json(HttpStatusCode.Unauthorized, """{ "error": { "message": "Invalid API key" } }"""),
            out _);

        var result = await provider.QueryAsync("https://api.deepseek.com", "bad", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.CredentialInvalid.Should().BeTrue();
        result.Error.Should().Contain("401");
    }

    [Fact]
    public async Task Query_Server_Error_Surfaces_Status()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests), out _);

        var result = await provider.QueryAsync("https://api.deepseek.com", "k", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.CredentialInvalid.Should().BeFalse();
        result.Error.Should().Contain("429");
    }

    [Fact]
    public async Task Query_Unrecognized_Body_Is_Deterministic_Failure()
    {
        var provider = CreateProvider(_ => Json(HttpStatusCode.OK, """{ "unexpected": true }"""), out _);

        var result = await provider.QueryAsync("https://api.deepseek.com", "k", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("余额");
    }

    [Fact]
    public void ParseCached_Roundtrips_Query_Result()
    {
        var provider = CreateProvider(_ => Json(HttpStatusCode.OK, SuccessBody), out _);

        var cached = provider.ParseCached(SuccessBody);

        cached.Should().NotBeNull();
        cached!.Success.Should().BeTrue();
        cached.Balances.Should().HaveCount(1);
        cached.Balances[0].GrantedBalance.Should().Be(8.60m);
        // 与实时查询同源解析，保证落库回读与首查展示一致。
        var live = provider.QueryAsync("https://api.deepseek.com", "k", CancellationToken.None).GetAwaiter().GetResult();
        live.Balances.Should().Equal(cached.Balances);
    }

    [Fact]
    public void ParseCached_Rejects_Unusable_Payloads()
    {
        var provider = CreateProvider(_ => Json(HttpStatusCode.OK, SuccessBody), out _);

        provider.ParseCached("").Should().BeNull();
        provider.ParseCached("not-json").Should().BeNull();
        // 空数组=无数据（不落库），与错误报文一致走 null。
        provider.ParseCached("""{ "balance_infos": [] }""").Should().BeNull();
    }
}
