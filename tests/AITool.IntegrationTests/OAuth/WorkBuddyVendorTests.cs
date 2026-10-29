using System.Net;
using System.Text;
using System.Text.Json;
using AITool.Infrastructure.OAuth;
using FluentAssertions;
using Xunit;

namespace AITool.IntegrationTests.OAuth;

/// <summary>
/// WorkBuddy 厂商测试：纯轮询登录（state→token→account）、刷新头形态（X-Refresh-Token + 旧 access 鉴权）、
/// 个人资源包（Cycle/Capacity 钳位 + UTC+8 到期）、企业周期（limit-credit）、快照 vendor 标记防串、
/// 拒绝分类（4xx=重登 / 5xx=可重试）。桩 HttpMessageHandler，无真实网络。
/// </summary>
public sealed class WorkBuddyVendorTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<(string Url, string? Body, HttpRequestMessage Request)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            Requests.Add((request.RequestUri!.PathAndQuery, body, request));
            return Task.FromResult(responder(request, body));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private const string StateBody = """{"code":0,"msg":"ok","data":{"state":"st-1","authUrl":"https://www.codebuddy.cn/login?state=st-1"}}""";

    [Fact]
    public async Task Sign_in_polls_token_then_account_with_fingerprint_headers()
    {
        var handler = new StubHandler((req, _) => req.RequestUri!.PathAndQuery switch
        {
            var u when u.Contains("/v2/plugin/auth/state") => Json(HttpStatusCode.OK, StateBody),
            var u when u.Contains("/v2/plugin/auth/token") => Json(HttpStatusCode.OK,
                """{"code":0,"data":{"accessToken":"at-1","refreshToken":"rt-1","expiresIn":7200,"domain":"www.codebuddy.cn"}}"""),
            var u when u.Contains("/v2/plugin/login/account") => Json(HttpStatusCode.OK,
                """{"code":0,"data":{"uid":"u-77","nickname":"测试号","enterpriseId":""}}"""),
            _ => Json(HttpStatusCode.NotFound, "{}"),
        });
        var vendor = new WorkBuddyOAuthVendor(new HttpClient(handler));

        var start = await vendor.StartSignInAsync(CancellationToken.None);
        start.AuthorizeUrl.Should().Contain("st-1");
        // 发码请求形态：platform=CLI + 风控闸门头。
        var stateReq = handler.Requests.Single(r => r.Url.Contains("/auth/state"));
        stateReq.Url.Should().Contain("platform=CLI");
        stateReq.Request.Headers.Should().Contain(h => h.Key == "X-CodeBuddy-Request" && h.Value.First() == "1");

        var pending = await vendor.PollSignInAsync(start.SessionJson, CancellationToken.None);
        pending.IsPending.Should().BeFalse();

        pending.Tokens.Should().NotBeNull();
        pending.Tokens!.AccessToken.Should().Be("at-1");
        pending.Tokens.RefreshToken.Should().Be("rt-1");
        pending.DisplayName.Should().Be("测试号");
        pending.ExternalUserId.Should().Be("u-77");

        // ExtraJson：指纹头（可复算校验）+ chatHeaders 结构 + realm。
        var extra = JsonDocument.Parse(pending.ExtraJson!);
        extra.RootElement.GetProperty("chatHeaders").GetProperty("X-Machine-ID").GetString()
            .Should().Be(Derive("machine", "u-77"), "指纹 = sha256(wb2a:machine:uid)[:36]");
        extra.RootElement.GetProperty("chatHeaders").GetProperty("X-User-Id").GetString().Should().Be("u-77");
        extra.RootElement.GetProperty("realm").GetString().Should().Be("workbuddy-cn");

        // 聊天站点：OpenAI 兼容 + 官方静态头（每账号头由供给器合并，不在 ChatSiteConfig）。
        var chat = vendor.ChatSiteConfig!;
        chat.BaseUrl.Should().Be("https://copilot.tencent.com/v2");
        chat.SupportsOpenAi.Should().BeTrue();
        chat.ExtraHeaders["X-Agent-Type"].Should().Be("main");
        // 上游只接受流式（11101）：标记头驱动代理层强制 stream:true + SSE 聚合。
        chat.ExtraHeaders["X-AITool-Force-Stream"].Should().Be("true");
    }

    [Fact]
    public async Task Poll_returns_pending_when_not_authorized_yet()
    {
        var handler = new StubHandler((req, _) => req.RequestUri!.PathAndQuery switch
        {
            var u when u.Contains("/v2/plugin/auth/state") => Json(HttpStatusCode.OK, StateBody),
            var u when u.Contains("/v2/plugin/auth/token") => Json(HttpStatusCode.OK,
                """{"code":11217,"msg":"waiting"}"""),
            _ => Json(HttpStatusCode.NotFound, "{}"),
        });
        var vendor = new WorkBuddyOAuthVendor(new HttpClient(handler));

        var start = await vendor.StartSignInAsync(CancellationToken.None);
        var poll = await vendor.PollSignInAsync(start.SessionJson, CancellationToken.None);
        poll.IsPending.Should().BeTrue("业务码非 0 / 无 accessToken 均为未授权");
    }

    [Fact]
    public async Task Refresh_sends_x_refresh_token_with_old_access_authorization()
    {
        CapturedAssert? captured = null;
        var handler = new StubHandler((req, body) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("/token/refresh"))
            {
                captured = new CapturedAssert(
                    req.Headers.TryGetValues("X-Refresh-Token", out var rt) ? rt.First() : null,
                    req.Headers.Authorization?.ToString(),
                    req.Headers.TryGetValues("X-Auth-Refresh-Source", out var src) ? src.First() : null);
            }

            return Json(HttpStatusCode.OK,
                """{"code":0,"data":{"accessToken":"at-2","refreshToken":"rt-2","expiresIn":3600}}""");
        });
        var vendor = new WorkBuddyOAuthVendor(new HttpClient(handler));

        var tokens = await vendor.RefreshTokenAsync(
            new AITool.Application.OAuth.DeviceOAuthTokenSet { AccessToken = "old-at", RefreshToken = "rt-1" },
            JsonSerializer.Serialize(new { uid = "u-77" }),
            CancellationToken.None);

        tokens.AccessToken.Should().Be("at-2");
        tokens.RefreshToken.Should().Be("rt-2");
        captured.Should().NotBeNull();
        captured!.Value.RefreshTokenHeader.Should().Be("rt-1");
        captured.Value.Authorization.Should().Be("Bearer old-at", "刷新要求 Authorization 仍带旧 access");
        captured.Value.RefreshSource.Should().Be("plugin");
    }

    [Fact]
    public async Task Refresh_4xx_is_refused_but_5xx_is_retryable()
    {
        var refused = new WorkBuddyOAuthVendor(new HttpClient(new StubHandler((req, _) =>
            Json(HttpStatusCode.BadRequest, "{}"))));
        var refuseAct = () => refused.RefreshTokenAsync(
            new AITool.Application.OAuth.DeviceOAuthTokenSet { AccessToken = "a", RefreshToken = "r" }, null, CancellationToken.None);
        await refuseAct.Should().ThrowAsync<AITool.Application.OAuth.DeviceOAuthRefreshRefusedException>();

        var flaky = new WorkBuddyOAuthVendor(new HttpClient(new StubHandler((req, _) =>
            Json(HttpStatusCode.BadGateway, "{}"))));
        var retryAct = () => flaky.RefreshTokenAsync(
            new AITool.Application.OAuth.DeviceOAuthTokenSet { AccessToken = "a", RefreshToken = "r" }, null, CancellationToken.None);
        await retryAct.Should().ThrowAsync<InvalidOperationException>();
        await retryAct.Should().NotThrowAsync<AITool.Application.OAuth.DeviceOAuthRefreshRefusedException>();
    }

    [Fact]
    public async Task Personal_quota_clamps_cycle_and_parses_utc8_expiry()
    {
        var handler = new StubHandler((req, _) => req.RequestUri!.PathAndQuery.Contains("get-user-resource")
            ? Json(HttpStatusCode.OK, """
                {"code":0,"data":{"Accounts":[
                    {"CycleCapacitySize":100,"CycleCapacityRemain":150,"CycleCapacityUsed":30,"CycleEndTime":"2026-11-01 12:00:00"},
                    {"CapacityRemain":25,"EndTime":"2027-01-01 00:00:00"}
                ]}}
                """)
            : Json(HttpStatusCode.NotFound, "{}"));
        var vendor = new WorkBuddyOAuthVendor(new HttpClient(handler));

        var quota = await vendor.QueryQuotaAsync(
            new AITool.Application.OAuth.DeviceOAuthTokenSet { AccessToken = "at" },
            JsonSerializer.Serialize(new { uid = "u-1" }), CancellationToken.None);

        quota.Success.Should().BeTrue();
        // 套餐1：Cycle 钳位（remain 150>size 100 → 100；used 30 < 100-100=0? 修正：usedFromRemain=0，
        // cycleUsed=30 > 0 → remain = 100-30 = 70）。套餐2：无 Cycle → Capacity 25。合计 95。
        quota.PlanType.Should().Contain("95");
        quota.Windows.Should().HaveCount(2);
        quota.Windows[0].UsedPercent.Should().BeNull("积分制无百分比，不参与自动禁用");
        quota.Windows[0].ResetAtUtc.Should().NotBeNull();
        // UTC+8 墙钟 2026-11-01 12:00 = UTC 04:00。
        quota.Windows[0].ResetAtUtc!.Value.UtcDateTime.Should().Be(new DateTime(2026, 11, 1, 4, 0, 0));
        // 快照带 vendor 标记（落库可回读、不与他厂串）。
        quota.RawJson.Should().Contain("\"vendor\":\"workbuddy-cn\"");
        vendor.ParseCachedQuota(quota.RawJson).Should().NotBeNull();
        vendor.ParseCachedQuota("""{"planType":"别的厂商"}""").Should().BeNull("严格 vendor 标记，防跨厂商误吞");
    }

    [Fact]
    public async Task Enterprise_quota_computes_limit_minus_credit()
    {
        var handler = new StubHandler((req, _) => req.RequestUri!.PathAndQuery.Contains("get-enterprise-user-usage")
            ? Json(HttpStatusCode.OK, """{"code":0,"data":{"credit":30,"limitNum":100}}""")
            : Json(HttpStatusCode.NotFound, "{}"));
        var vendor = new WorkBuddyOAuthVendor(new HttpClient(handler));

        var quota = await vendor.QueryQuotaAsync(
            new AITool.Application.OAuth.DeviceOAuthTokenSet { AccessToken = "at" },
            JsonSerializer.Serialize(new { uid = "u-1", enterpriseId = "ent-9" }), CancellationToken.None);

        quota.Success.Should().BeTrue();
        quota.PlanType.Should().Contain("70", "企业 remain = limit - credit = 100 - 30");
        quota.Windows.Should().ContainSingle(w => w.UsedPercent == 30);
    }

    [Fact]
    public async Task Global_vendor_uses_workbuddy_ai_bases_and_en_language()
    {
        var handler = new StubHandler((req, _) => Json(HttpStatusCode.OK, StateBody));
        var vendor = new WorkBuddyOAuthVendor(new HttpClient(handler), global: true);
        vendor.VendorKey.Should().Be("workbuddy-global");
        vendor.ChatSiteConfig!.BaseUrl.Should().Be("https://www.workbuddy.ai/v2");

        await vendor.StartSignInAsync(CancellationToken.None);
        var stateReq = handler.Requests.Single(r => r.Url.Contains("/auth/state"));
        stateReq.Request.Headers.Should().Contain(h => h.Key == "Accept-Language" && h.Value.First() == "en-US");
        stateReq.Request.Headers.Should().Contain(h => h.Key == "Origin" && h.Value.First() == "https://www.workbuddy.ai");
    }

    private static string Derive(string purpose, string uid)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"wb2a:{purpose}:{uid}"))).ToLowerInvariant()[..36];

    private readonly record struct CapturedAssert(string? RefreshTokenHeader, string? Authorization, string? RefreshSource);
    [Fact]
    public async Task Refresh_maps_business_code_12153_to_refused()
    {
        var handler = new StubHandler((req, _) => Json(HttpStatusCode.OK,
            """{"code":12153,"msg":"session dead"}"""));
        var vendor = new WorkBuddyOAuthVendor(new HttpClient(handler));

        var act = () => vendor.RefreshTokenAsync(
            new AITool.Application.OAuth.DeviceOAuthTokenSet { AccessToken = "a", RefreshToken = "r" }, null, CancellationToken.None);
        await act.Should().ThrowAsync<AITool.Application.OAuth.DeviceOAuthRefreshRefusedException>("12153=会话死亡，须重登而非无限退避");
    }

    [Fact]
    public async Task Refresh_accepts_bare_token_object_without_envelope()
    {
        var handler = new StubHandler((req, _) => Json(HttpStatusCode.OK,
            """{"accessToken":"bare-at","refreshToken":"bare-rt"}"""));
        var vendor = new WorkBuddyOAuthVendor(new HttpClient(handler));

        var tokens = await vendor.RefreshTokenAsync(
            new AITool.Application.OAuth.DeviceOAuthTokenSet { AccessToken = "a", RefreshToken = "r", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) },
            null, CancellationToken.None);
        tokens.AccessToken.Should().Be("bare-at", "裸 token 对象形态也要认");
        tokens.RefreshToken.Should().Be("bare-rt");
    }

    [Fact]
    public async Task Refresh_sends_device_fingerprint_headers_when_uid_known()
    {
        CapturedAssert? captured = null;
        var handler = new StubHandler((req, _) =>
        {
            if (req.RequestUri!.PathAndQuery.Contains("/token/refresh"))
            {
                captured = new CapturedAssert(null, null, null);
            }

            return Json(HttpStatusCode.OK, """{"code":0,"data":{"accessToken":"at-2","expiresIn":3600}}""");
        });
        var vendor = new WorkBuddyOAuthVendor(new HttpClient(handler));

        await vendor.RefreshTokenAsync(
            new AITool.Application.OAuth.DeviceOAuthTokenSet { AccessToken = "a", RefreshToken = "r" },
            JsonSerializer.Serialize(new { uid = "u-77" }),
            CancellationToken.None);

        var refreshReq = handler.Requests.Single(r => r.Url.Contains("/token/refresh")).Request;
        refreshReq.Headers.Should().Contain(h => h.Key == "X-Machine-ID" && h.Value.First() == Derive("machine", "u-77"),
            "uid 已知的刷新必须带指纹头（风控关联信号）");
        refreshReq.Headers.Should().Contain(h => h.Key == "X-Session-ID" && h.Value.First() == Derive("session", "u-77"));
    }

    [Fact]
    public void Personal_quota_descends_nested_envelope_shapes()
    {
        // 腾讯云风格 data.Response.Accounts 深层形态。
        const string body = """{"code":0,"data":{"Response":{"Accounts":[{"CapacityRemain":40}]}}}""";
        var parsed = WorkBuddyQuotaParser.ParsePersonal(body);
        parsed.Should().NotBeNull();
        parsed!.PlanType.Should().Contain("40");
    }

}
