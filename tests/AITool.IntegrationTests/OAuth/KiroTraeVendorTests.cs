using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Infrastructure.OAuth;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.OAuth;

/// <summary>
/// Kiro / Trae 厂商测试：双段登录流转（社交直换 / Builder ID 动态注册续跳 / 第二段换 token）、
/// 设备密钥 proof 的签名可验证性、refresh 轮换保留。全部走桩 HttpMessageHandler，无真实网络。
/// </summary>
public sealed class KiroTraeVendorTests
{
    private sealed record CapturedRequest(HttpMethod Method, Uri Url, string? Body, string? Form);

    private sealed class StubHandler(Func<CapturedRequest, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            var form = request.Content?.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded" ? body : null;
            var captured = new CapturedRequest(request.Method, request.RequestUri!, body, form);
            Requests.Add(captured);
            return Task.FromResult(responder(captured));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    // ---- Kiro ----

    [Fact]
    public async Task Kiro_social_callback_exchanges_token()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{"accessToken":"k-at","refreshToken":"k-rt","expiresIn":3600,"profileArn":"arn:profile"}"""));
        var vendor = new KiroOAuthVendor(new HttpClient(handler));

        var start = await vendor.StartSignInAsync(CancellationToken.None);
        var session = JsonDocument.Parse(start.SessionJson);
        var state = session.RootElement.GetProperty("State").GetString()!;

        var result = await vendor.CompleteWithCallbackAsync(
            start.SessionJson,
            $"http://127.0.0.1:17893/oauth/callback?code=c-1&state={Uri.EscapeDataString(state)}&login_option=google",
            CancellationToken.None);

        result.Tokens.Should().NotBeNull();
        result.Tokens!.AccessToken.Should().Be("k-at");
        result.Tokens.RefreshToken.Should().Be("k-rt");
        var extra = JsonDocument.Parse(result.ExtraJson!);
        extra.RootElement.GetProperty("method").GetString().Should().Be("social");
        extra.RootElement.GetProperty("profileArn").GetString().Should().Be("arn:profile");

        // 换 token 请求形态：表单 + code_verifier + redirect_uri 带 login_option
        var exchange = handler.Requests.Single(r => r.Url.AbsoluteUri.Contains("/oauth/token"));
        exchange.Form.Should().Contain("code=c-1").And.Contain("login_option%3Dgoogle");
    }

    [Fact]
    public async Task Kiro_builderid_registers_aws_client_then_exchanges_second_leg()
    {
        var handler = new StubHandler(req => req.Url.AbsolutePath.EndsWith("/client/register")
            ? Json(HttpStatusCode.OK, """{"clientId":"aws-cid","clientSecret":"aws-sec"}""")
            : Json(HttpStatusCode.OK, """{"accessToken":"aws-at","refreshToken":"aws-rt","expiresIn":3600}"""));
        var vendor = new KiroOAuthVendor(new HttpClient(handler));

        var start = await vendor.StartSignInAsync(CancellationToken.None);
        var session = JsonDocument.Parse(start.SessionJson);
        var state = session.RootElement.GetProperty("State").GetString()!;

        // 第一段：builderid 回调 → 动态注册 + 续跳 AWS authorize
        var first = await vendor.CompleteWithCallbackAsync(
            start.SessionJson,
            $"http://127.0.0.1:17893/oauth/callback?code=c1&state={Uri.EscapeDataString(state)}&login_option=builderid&issuer_url=https://issuer&region_id=x&idc_region=us-west-2",
            CancellationToken.None);

        first.IsPending.Should().BeTrue();
        first.NextAuthorizeUrl.Should().NotBeNull().And.Contain("oidc.us-west-2.amazonaws.com/authorize");
        first.UpdatedSessionJson.Should().NotBeNull();

        // 注册请求形态：issuerUrl + redirectUris 指向本机回调
        var register = handler.Requests.Single(r => r.Url.AbsolutePath.EndsWith("/client/register"));
        register.Body.Should().Contain("issuerUrl").And.Contain("Kiro IDE");

        // 第二段：AWS 回调（aws.state 匹配）→ camelCase JSON 换 token
        var second = await vendor.CompleteWithCallbackAsync(
            first.UpdatedSessionJson!,
            $"http://127.0.0.1:17893/oauth/callback?code=c2&state={ExtractJsonValue(first.UpdatedSessionJson!, "AwsState")}",
            CancellationToken.None);

        second.Tokens.Should().NotBeNull();
        second.Tokens!.AccessToken.Should().Be("aws-at");
        var extra = JsonDocument.Parse(second.ExtraJson!);
        extra.RootElement.GetProperty("method").GetString().Should().Be("idc");
        extra.RootElement.GetProperty("region").GetString().Should().Be("us-west-2");
        extra.RootElement.GetProperty("clientId").GetString().Should().Be("aws-cid");

        var tokenExchange = handler.Requests.Single(r => r.Url.AbsoluteUri.Contains("oidc.us-west-2.amazonaws.com/token"));
        tokenExchange.Body.Should().Contain("\"codeVerifier\"").And.Contain("\"clientId\":\"aws-cid\"");
    }

    [Fact]
    public async Task Kiro_social_refresh_5xx_is_retryable_not_refused()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.ServiceUnavailable, "{}"));
        var vendor = new KiroOAuthVendor(new HttpClient(handler));
        var extra = System.Text.Json.JsonSerializer.Serialize(new { method = "social", region = "us-east-1" });

        // 5xx 必须是可重试异常；若误判为「需重登」，账号会被 RequiresReauth 砖掉。
        var act = () => vendor.RefreshTokenAsync("rt-1", extra, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        await act.Should().NotThrowAsync<AITool.Application.OAuth.DeviceOAuthRefreshRefusedException>();
    }

    [Fact]
    public async Task Kiro_idc_refresh_sends_client_secret()
    {
        CapturedRequest? captured = null;
        var handler = new StubHandler(req =>
        {
            if (req.Url.AbsoluteUri.Contains("oidc.us-west-2.amazonaws.com/token"))
            {
                captured = req;
                return Json(HttpStatusCode.OK, """{"accessToken":"a2","refreshToken":"r2","expiresIn":3600}""");
            }

            return Json(HttpStatusCode.OK, "{}");
        });
        var vendor = new KiroOAuthVendor(new HttpClient(handler));
        var extra = System.Text.Json.JsonSerializer.Serialize(new { method = "idc", region = "us-west-2", clientId = "cid", clientSecret = "csec" });

        var tokens = await vendor.RefreshTokenAsync("r1", extra, CancellationToken.None);
        tokens.AccessToken.Should().Be("a2");
        captured.Should().NotBeNull();
        captured!.Form.Should().Contain("client_id=cid").And.Contain("client_secret=csec").And.Contain("refresh_token=r1");
    }

    [Fact]
    public void Kiro_usage_parser_reads_breakdown_and_trial()
    {
        const string body = """
            { "usageBreakdownList": [
                { "currentUsageWithPrecision": 3, "usageLimitWithPrecision": 10 },
                { "currentUsageWithPrecision": 0, "usageLimitWithPrecision": 0,
                  "freeTrialInfo": { "freeTrialStatus": "ACTIVE", "usageLimitWithPrecision": 5, "currentUsageWithPrecision": 1, "freeTrialExpiry": 2000000000 } }
              ] }
            """;

        var result = KiroUsageParser.Parse(body)!;
        result.Success.Should().BeTrue();
        result.Windows.Should().HaveCount(2);
        result.Windows[0].UsedPercent.Should().Be(30);
        result.Windows[1].Label.Should().Contain("免费试用");
        result.Windows[1].UsedPercent.Should().Be(20);
        result.Windows[1].ResetAtUtc.Should().NotBeNull();
    }

    // ---- Trae ----

    [Fact]
    public async Task Trae_sign_in_creates_device_key_and_callback_completes()
    {
        var vendor = new TraeOAuthVendor(new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK, "{}"))));

        var start = await vendor.StartSignInAsync(CancellationToken.None);
        start.AuthorizeUrl.Should().Contain("authorization?").And.Contain("auth_callback_url=");
        var session = JsonDocument.Parse(start.SessionJson);
        session.RootElement.GetProperty("DeviceId").GetString()!.Should().HaveLength(16);
        session.RootElement.GetProperty("DeviceId").GetString()!.AllDigits().Should().BeTrue();
        session.RootElement.GetProperty("PublicKeyPem").GetString()!.Should().Contain("BEGIN PUBLIC KEY");

        var result = await vendor.CompleteWithCallbackAsync(
            start.SessionJson,
            "http://127.0.0.1:17895/authorize?userJwt=jwt-1&refreshToken=rt-1&uid=u-9",
            CancellationToken.None);

        result.Tokens.Should().NotBeNull();
        result.Tokens!.AccessToken.Should().Be("jwt-1");
        result.Tokens.RefreshToken.Should().Be("rt-1");
        result.ExternalUserId.Should().Be("u-9");
        var extra = JsonDocument.Parse(result.ExtraJson!);
        extra.RootElement.GetProperty("devicePrivateKey").GetString().Should().Contain("PRIVATE KEY");
    }

    [Fact]
    public async Task Trae_global_uses_19_digit_device_and_global_host()
    {
        var vendor = new TraeOAuthVendor(new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK, "{}"))), global: true);
        vendor.VendorKey.Should().Be("trae-global");

        var start = await vendor.StartSignInAsync(CancellationToken.None);
        var session = JsonDocument.Parse(start.SessionJson);
        session.RootElement.GetProperty("DeviceId").GetString()!.Should().HaveLength(19);
        session.RootElement.GetProperty("DeviceId").GetString()!.AllDigits().Should().BeTrue();
        start.AuthorizeUrl.Should().StartWith("https://www.trae.ai/");
    }

    /// <summary>
    /// 设备绑定续期的核心正确性：请求里的 DeviceProof.Signature 必须能被请求里的
    /// DeviceInfo.DevicePublicKey 按 "POST\n{path}\n{ClientID}\n{RefreshToken}\n{ts}\n{nonce}" 验签。
    /// </summary>
    [Fact]
    public async Task Trae_refresh_proof_signature_verifies_with_embedded_public_key()
    {
        JsonElement? capturedBody = null;
        var handler = new StubHandler(req =>
        {
            capturedBody = JsonDocument.Parse(req.Body ?? "{}").RootElement.Clone();
            return Json(HttpStatusCode.OK,
                """{"Result":{"Token":"jwt-2","RefreshToken":"rt-2","TokenExpireAt":2000000000,"UserInfo":"{}"}}""");
        });
        var vendor = new TraeOAuthVendor(new HttpClient(handler));

        var start = await vendor.StartSignInAsync(CancellationToken.None);
        var completed = await vendor.CompleteWithCallbackAsync(
            start.SessionJson,
            "http://127.0.0.1:17895/authorize?userJwt=jwt-1&refreshToken=rt-1",
            CancellationToken.None);

        var refreshed = await vendor.RefreshTokenAsync("rt-1", completed.ExtraJson, CancellationToken.None);
        refreshed.AccessToken.Should().Be("jwt-2");
        refreshed.RefreshToken.Should().Be("rt-2", "Trae 每次续期轮换 refresh_token");

        var body = capturedBody!.Value;
        var proof = body.GetProperty("DeviceProof");
        var info = body.GetProperty("DeviceInfo");
        var text = string.Join("\n",
            "POST",
            "/trae/api/v3/oauth/ExchangeToken",
            body.GetProperty("ClientID").GetString()!,
            "rt-1",
            proof.GetProperty("Timestamp").GetInt64().ToString(),
            proof.GetProperty("Nonce").GetString()!);

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(info.GetProperty("DevicePublicKey").GetString()!);
        ecdsa.VerifyData(
            Encoding.UTF8.GetBytes(text),
            Convert.FromBase64String(proof.GetProperty("Signature").GetString()!),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence).Should().BeTrue("proof 必须能被随请求携带的设备公钥验签（DER 格式）");
    }

    [Fact]
    public async Task Trae_refresh_without_device_key_is_refused()
    {
        var vendor = new TraeOAuthVendor(new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK, "{}"))));
        var act = () => vendor.RefreshTokenAsync("rt-x", CancellationToken.None);
        await act.Should().ThrowAsync<AITool.Application.OAuth.DeviceOAuthRefreshRefusedException>();
    }

    private static string ExtractJsonValue(string json, string property)
        => JsonDocument.Parse(json).RootElement.GetProperty(property).GetString()!;
}

file static class DigitExtensions
{
    public static bool AllDigits(this string value) => value.All(char.IsDigit);
}
