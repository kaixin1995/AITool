using System.Net;
using AITool.Application.Sites;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.Services;

/// <summary>
/// 智谱额度供应商 HTTP 行为测试：端点路由、鉴权头形态（裸 key，无 Bearer）、
/// 401/403 → 密钥失效映射、缓存解析对称性。用桩 HttpMessageHandler 拦截，无真实网络。
/// </summary>
public sealed class ZhipuSiteQuotaProviderTests
{
    private const string SuccessBody = """
        {
          "success": true,
          "data": {
            "level": "GLM Coding Plan (Lite)",
            "limits": [
              { "type": "TOKENS_LIMIT", "unit": 3, "number": 5, "percentage": 12.5, "nextResetTime": 1000003600000 },
              { "type": "TOKENS_LIMIT", "unit": 6, "number": 7, "percentage": 47.0, "nextResetTime": 1000018000000 }
            ]
          }
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
                request.Headers.Authorization?.ToString());
            return Task.FromResult(responder(LastRequest));
        }
    }

    private static ZhipuSiteQuotaProvider CreateProvider(Func<CapturedRequest, HttpResponseMessage> responder, out StubHandler handler)
    {
        handler = new StubHandler(responder);
        return new ZhipuSiteQuotaProvider(
            new HttpClient(handler),
            NullLogger<ZhipuSiteQuotaProvider>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task Query_Cn_Host_Uses_Bigmodel_Endpoint_With_Raw_Key_Authorization()
    {
        var provider = CreateProvider(
            _ => Json(HttpStatusCode.OK, SuccessBody),
            out var handler);

        var result = await provider.QueryAsync("https://open.bigmodel.cn/api/coding/paas/v4", "my-secret-key", CancellationToken.None);

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Get);
        handler.LastRequest.Url.AbsoluteUri.Should().Be("https://open.bigmodel.cn/api/monitor/usage/quota/limit");
        // 智谱鉴权特殊：Authorization 直接携带 API key，不加 Bearer 前缀。
        handler.LastRequest.Authorization.Should().Be("my-secret-key");

        result.Success.Should().BeTrue();
        result.Level.Should().Be("GLM Coding Plan (Lite)");
        result.Windows.Should().HaveCount(2);
        result.Windows[0].Id.Should().Be("five_hour");
        result.Windows[0].UsedPercent.Should().Be(12.5);
        result.Windows[1].Id.Should().Be("weekly_limit");
        result.RawJson.Should().Be(SuccessBody);
    }

    [Fact]
    public async Task Query_Global_Host_Routes_To_Zai_Endpoint()
    {
        var provider = CreateProvider(
            _ => Json(HttpStatusCode.OK, SuccessBody),
            out var handler);

        await provider.QueryAsync("https://api.z.ai/api/v1", "k", CancellationToken.None);

        handler.LastRequest!.Url.Host.Should().Be("api.z.ai");
    }

    [Fact]
    public async Task Query_Unauthorized_Maps_To_Credential_Invalid()
    {
        var provider = CreateProvider(
            _ => Json(HttpStatusCode.Unauthorized, """{ "success": false, "msg": "invalid token" }"""),
            out _);

        var result = await provider.QueryAsync("https://open.bigmodel.cn/api", "bad-key", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.CredentialInvalid.Should().BeTrue();
        result.Error.Should().Contain("401");
    }

    [Fact]
    public async Task Query_Forbidden_Maps_To_Credential_Invalid()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.Forbidden), out _);

        var result = await provider.QueryAsync("https://open.bigmodel.cn/api", "revoked", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.CredentialInvalid.Should().BeTrue();
    }

    [Fact]
    public async Task Query_Server_Error_Surfaces_Status_And_Body()
    {
        var provider = CreateProvider(
            _ => Json(HttpStatusCode.InternalServerError, "boom"),
            out _);

        var result = await provider.QueryAsync("https://open.bigmodel.cn/api", "k", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.CredentialInvalid.Should().BeFalse();
        result.Error.Should().Contain("500").And.Contain("boom");
    }

    [Fact]
    public async Task Query_Business_Error_Is_Deterministic_Failure()
    {
        var provider = CreateProvider(
            _ => Json(HttpStatusCode.OK, """{ "success": false, "msg": "令牌无效" }"""),
            out _);

        var result = await provider.QueryAsync("https://open.bigmodel.cn/api", "k", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.CredentialInvalid.Should().BeFalse();
        result.Error.Should().Contain("令牌无效");
    }

    [Fact]
    public async Task Query_Team_Fallback_When_Personal_Has_No_Windows()
    {
        // 团队版 key 在个人端点拿不到窗口数据，应回退 ?type=2 团队端点。
        var urls = new List<string>();
        var provider = CreateProvider(request =>
        {
            urls.Add(request.Url.AbsoluteUri);
            return request.Url.Query.Contains("type=2")
                ? Json(HttpStatusCode.OK, SuccessBody)
                : Json(HttpStatusCode.OK, """{ "success": true, "data": { "level": "GLM Coding Plan", "limits": [] } }""");
        }, out _);

        var result = await provider.QueryAsync("https://open.bigmodel.cn/api/coding/paas/v4", "team-key", CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Windows.Should().HaveCount(2);
        urls.Should().Contain("https://open.bigmodel.cn/api/monitor/usage/quota/limit?type=2");
    }

    [Fact]
    public async Task Query_Team_Fallback_Tries_Bigmodel_Biz_Root_Second()
    {
        // bigmodel.cn 域：本站根失败后应再试业务根 bigmodel.cn（ZCode 客户端的问法）。
        var urls = new List<string>();
        var provider = CreateProvider(request =>
        {
            urls.Add(request.Url.AbsoluteUri);
            return request.Url.Host == "bigmodel.cn"
                ? Json(HttpStatusCode.OK, SuccessBody)
                : Json(HttpStatusCode.InternalServerError, "boom");
        }, out _);

        var result = await provider.QueryAsync("https://open.bigmodel.cn/api/coding/paas/v4", "team-key", CancellationToken.None);

        result.Success.Should().BeTrue();
        urls.Should().Equal(
            "https://open.bigmodel.cn/api/monitor/usage/quota/limit",
            "https://open.bigmodel.cn/api/monitor/usage/quota/limit?type=2",
            "https://bigmodel.cn/api/monitor/usage/quota/limit?type=2");
    }

    [Fact]
    public async Task Query_Key_Refusal_Envelope_Skips_Team_Fallback()
    {
        // HTTP 200 + 平台信封（code 1000–1005）= 密钥被拒：映射密钥失效，且不再尝试团队端点。
        var urls = new List<string>();
        var provider = CreateProvider(request =>
        {
            urls.Add(request.Url.AbsoluteUri);
            return Json(HttpStatusCode.OK, """{ "success": false, "code": 1001, "msg": "令牌无效" }""");
        }, out _);

        var result = await provider.QueryAsync("https://open.bigmodel.cn/api/coding/paas/v4", "bad-key", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.CredentialInvalid.Should().BeTrue();
        result.Error.Should().Contain("1001");
        urls.Should().ContainSingle("个人端点被拒后不应再打团队端点");
    }

    [Fact]
    public async Task Query_Envelope_Balance_Error_Is_Not_Credential_Invalid()
    {
        // code 1113（余额不足）是确定性业务错误，但不是密钥失效。
        var urls = new List<string>();
        var provider = CreateProvider(request =>
        {
            urls.Add(request.Url.AbsoluteUri);
            return Json(HttpStatusCode.OK, """{ "success": false, "code": 1113, "msg": "余额不足" }""");
        }, out _);

        var result = await provider.QueryAsync("https://open.bigmodel.cn/api/coding/paas/v4", "k", CancellationToken.None);

        result.Success.Should().BeFalse();
        result.CredentialInvalid.Should().BeFalse();
        // 非密钥拒绝的业务错误仍会尝试团队端点（两根域名各一次）。
        urls.Count.Should().Be(3);
    }

    [Fact]
    public async Task Query_Personal_Success_Does_Not_Try_Team()
    {
        var urls = new List<string>();
        var provider = CreateProvider(request =>
        {
            urls.Add(request.Url.AbsoluteUri);
            return Json(HttpStatusCode.OK, SuccessBody);
        }, out _);

        var result = await provider.QueryAsync("https://api.z.ai/api/v1", "k", CancellationToken.None);

        result.Success.Should().BeTrue();
        urls.Should().ContainSingle();
    }

    [Fact]
    public async Task ParseCached_Roundtrips_Query_Result()
    {
        var provider = CreateProvider(_ => Json(HttpStatusCode.OK, SuccessBody), out _);

        var cached = provider.ParseCached(SuccessBody);

        cached.Should().NotBeNull();
        cached!.Success.Should().BeTrue();
        cached.Level.Should().Be("GLM Coding Plan (Lite)");
        cached.Windows.Should().HaveCount(2);
        // 与实时查询同源解析，保证落库回读与首查展示一致。
        var live = await provider.QueryAsync("https://open.bigmodel.cn/api", "k", CancellationToken.None);
        live.Windows.Select(w => (w.Id, w.UsedPercent))
            .Should().Equal(cached.Windows.Select(w => (w.Id, w.UsedPercent)));
    }

    [Fact]
    public void ParseCached_Rejects_Unusable_Payloads()
    {
        var provider = CreateProvider(_ => Json(HttpStatusCode.OK, SuccessBody), out _);

        provider.ParseCached("").Should().BeNull();
        provider.ParseCached("not-json").Should().BeNull();
        // 业务错误报文不落库（查询失败走 LastQuotaError 列），无窗口可解析。
        provider.ParseCached("""{ "success": false, "msg": "x" }""").Should().BeNull();
    }
}
