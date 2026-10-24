using System.Net;
using System.Text;
using System.Text.Json;
using AITool.Infrastructure.OAuth;
using AITool.Infrastructure.Proxy;
using FluentAssertions;
using Xunit;

namespace AITool.IntegrationTests.OAuth;

/// <summary>
/// ZCode 深链路测试：登录轮询 → 业务登录 → 客户信息（默认机构/项目）→ 密钥铸造（复用/创建/取 secret）、
/// 额度 monitor 端点（个人空窗口→团队回退带组织头）、鉴权形态标记。
/// 桩 HttpMessageHandler 按 URL 分派，无真实网络。
/// </summary>
public sealed class ZCodeDeepChainTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<(string Url, string? Body)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            Requests.Add((request.RequestUri!.PathAndQuery, body));
            return Task.FromResult(responder(request, body));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string Envelope(object? data) => JsonSerializer.Serialize(new { code = 0, msg = "ok", data });

    /// <summary>客户信息桩：默认机构优先（另带一个排前面的普通团队 + 一个 type-2 项目应被跳过）。</summary>
    private static HttpResponseMessage CustomerInfoDefaultOrg() => Json(HttpStatusCode.OK, Envelope(new
    {
        organizations = new object[]
        {
            new { organizationId = "org-first", organizationName = "某个团队", projects = new[] { new { projectId = "p0", projectName = "P0", projectType = 2 } } },
            new { organizationId = "org-def", organizationName = "默认机构", projects = new[] { new { projectId = "p-def", projectName = "默认项目", projectType = 1 } } },
        },
    }));

    private static HttpResponseMessage CustomerInfoSingle() => Json(HttpStatusCode.OK, Envelope(new
    {
        organizations = new[] { new { organizationId = "o1", organizationName = "默认机构", projects = new[] { new { projectId = "p1", projectName = "默认项目" } } } },
    }));

    [Fact]
    public async Task Sign_in_mints_glm_api_key_through_default_org_and_project()
    {
        var handler = new StubHandler((req, _) => req.RequestUri!.PathAndQuery switch
        {
            var u when u.EndsWith("/api/v1/oauth/cli/init") => Json(HttpStatusCode.OK, """{"flow_id":"f1","authorize_url":"https://zcode.z.ai/auth","poll_interval_sec":2,"expires_at":0}"""),
            var u when u.Contains("/api/v1/oauth/cli/poll/") => Json(HttpStatusCode.OK,
                """{"status":"ready","zai":{"access_token":"zai-tok"},"user":{"id":"u-1","name":"张三"}}"""),
            var u when u.EndsWith("/api/auth/z/login") => Json(HttpStatusCode.OK, Envelope(new { access_token = "biz-tok" })),
            var u when u.EndsWith("/api/biz/customer/getCustomerInfo") => CustomerInfoDefaultOrg(),
            var u when u.EndsWith("/api_keys") && req.Method == HttpMethod.Get => Json(HttpStatusCode.OK, Envelope(Array.Empty<object>())),
            var u when u.EndsWith("/api_keys") && req.Method == HttpMethod.Post => Json(HttpStatusCode.OK, Envelope(new { apiKey = "key-id-1" })),
            var u when u.Contains("/api_keys/copy/key-id-1") => Json(HttpStatusCode.OK, Envelope(new { secretKey = "sec-1" })),
            _ => Json(HttpStatusCode.NotFound, "{}"),
        });
        var vendor = new ZCodeOAuthVendor(new HttpClient(handler));

        var start = await vendor.StartSignInAsync(CancellationToken.None);
        var poll = await vendor.PollSignInAsync(start.SessionJson, CancellationToken.None);

        poll.Tokens.Should().NotBeNull();
        poll.Tokens!.AccessToken.Should().Be("key-id-1.sec-1", "铸造出 id.secret 形态的标准 GLM API key");
        poll.Tokens.RefreshToken.Should().Be("zai-tok");
        poll.Tokens.ExpiresAt.Should().BeNull("铸造 key 长效不过期");
        poll.DisplayName.Should().Be("张三");
        var extra = JsonDocument.Parse(poll.ExtraJson!);
        extra.RootElement.GetProperty("org").GetString().Should().Be("org-def", "默认机构优先于排第一的团队");
        extra.RootElement.GetProperty("project").GetString().Should().Be("p-def");
        extra.RootElement.GetProperty("zaiToken").GetString().Should().Be("zai-tok");

        // 创建请求落在默认机构/默认项目的 keys 路径，密钥名 zcode-api-key
        var create = handler.Requests.Single(r => r.Url.EndsWith("/api_keys") && r.Url.Contains("/organization/org-def/") && r.Body != null);
        create.Body.Should().Contain("zcode-api-key");

        // 聊天站点：z.ai anthropic + Bearer 标记
        var chat = vendor.ChatSiteConfig!;
        chat.BaseUrl.Should().Be("https://api.z.ai/api/anthropic");
        chat.ExtraHeaders["X-AITool-Auth-Mode"].Should().Be("bearer");
    }

    [Fact]
    public async Task Existing_named_key_is_reused_without_creating()
    {
        var createCalled = false;
        var handler = new StubHandler((req, _) => req.RequestUri!.PathAndQuery switch
        {
            var u when u.EndsWith("/api/v1/oauth/cli/init") => Json(HttpStatusCode.OK, """{"flow_id":"f1","authorize_url":"https://zcode.z.ai/auth","poll_interval_sec":2,"expires_at":0}"""),
            var u when u.Contains("/api/v1/oauth/cli/poll/") => Json(HttpStatusCode.OK,
                """{"status":"ready","zai":{"access_token":"zai-tok"}}"""),
            var u when u.EndsWith("/api/auth/z/login") => Json(HttpStatusCode.OK, Envelope(new { access_token = "biz" })),
            var u when u.EndsWith("/api/biz/customer/getCustomerInfo") => CustomerInfoSingle(),
            var u when u.EndsWith("/api_keys") && req.Method == HttpMethod.Get => Json(HttpStatusCode.OK,
                Envelope(new[] { new { name = "zcode-api-key", apiKey = "existing-id" } })),
            var u when u.EndsWith("/api_keys") && req.Method == HttpMethod.Post => MarkCreate(ref createCalled),
            var u when u.Contains("/api_keys/copy/existing-id") => Json(HttpStatusCode.OK, Envelope(new { secretKey = "sec-2" })),
            _ => Json(HttpStatusCode.NotFound, "{}"),
        });
        var vendor = new ZCodeOAuthVendor(new HttpClient(handler));

        var start = await vendor.StartSignInAsync(CancellationToken.None);
        var poll = await vendor.PollSignInAsync(start.SessionJson, CancellationToken.None);

        poll.Tokens!.AccessToken.Should().Be("existing-id.sec-2");
        createCalled.Should().BeFalse("已有同名密钥时复用，不重复创建");
    }

    private static HttpResponseMessage MarkCreate(ref bool flag)
    {
        flag = true;
        return Json(HttpStatusCode.OK, Envelope(new { apiKey = "should-not-happen" }));
    }

    [Fact]
    public async Task Quota_uses_zhipu_monitor_with_team_fallback()
    {
        var personalQueried = false;
        var handler = new StubHandler((req, _) =>
        {
            var url = req.RequestUri!.PathAndQuery;
            if (url.Contains("/api/monitor/usage/quota/limit") && !url.Contains("type=2"))
            {
                personalQueried = true;
                return Json(HttpStatusCode.OK, """{"success":true,"data":{"level":"GLM Coding Plan","limits":[]}}""");
            }

            if (url.Contains("/api/monitor/usage/quota/limit?type=2"))
            {
                req.Headers.Should().Contain(h => h.Key == "Bigmodel-Organization" && h.Value.First() == "org-def",
                    "团队查询带组织头");
                return Json(HttpStatusCode.OK,
                    """{"success":true,"data":{"level":"GLM Coding Team","limits":[{"type":"TOKENS_LIMIT","unit":6,"percentage":42.0,"nextResetTime":1000018000000}]}}""");
            }

            return url switch
            {
                var u when u.EndsWith("/api/v1/oauth/cli/init") => Json(HttpStatusCode.OK, """{"flow_id":"f1","authorize_url":"https://zcode.z.ai/auth","poll_interval_sec":2,"expires_at":0}"""),
            var u when u.Contains("/api/v1/oauth/cli/poll/") => Json(HttpStatusCode.OK,
                    """{"status":"ready","zai":{"access_token":"zai-tok"}}"""),
                var u when u.EndsWith("/api/auth/z/login") => Json(HttpStatusCode.OK, Envelope(new { access_token = "biz" })),
                var u when u.EndsWith("/api/biz/customer/getCustomerInfo") => CustomerInfoDefaultOrg(),
                var u when u.EndsWith("/api_keys") && req.Method == HttpMethod.Get => Json(HttpStatusCode.OK, Envelope(Array.Empty<object>())),
                var u when u.EndsWith("/api_keys") && req.Method == HttpMethod.Post => Json(HttpStatusCode.OK, Envelope(new { apiKey = "k" })),
                var u when u.Contains("/api_keys/copy/") => Json(HttpStatusCode.OK, Envelope(new { secretKey = "s" })),
                _ => Json(HttpStatusCode.NotFound, "{}"),
            };
        });
        var vendor = new ZCodeOAuthVendor(new HttpClient(handler));

        var start = await vendor.StartSignInAsync(CancellationToken.None);
        var poll = await vendor.PollSignInAsync(start.SessionJson, CancellationToken.None);

        var quota = await vendor.QueryQuotaAsync(poll.Tokens!, poll.ExtraJson, CancellationToken.None);
        personalQueried.Should().BeTrue("个人版端点先于团队回退被查询");
        quota.Success.Should().BeTrue();
        quota.PlanType.Should().Be("GLM Coding Team");
        quota.Windows.Should().ContainSingle(w => w.UsedPercent == 42.0);
    }

    [Fact]
    public async Task Refresh_re_mints_key_with_zai_token()
    {
        var handler = new StubHandler((req, _) => req.RequestUri!.PathAndQuery switch
        {
            var u when u.EndsWith("/api/auth/z/login") => Json(HttpStatusCode.OK, Envelope(new { access_token = "biz" })),
            var u when u.EndsWith("/api/biz/customer/getCustomerInfo") => CustomerInfoSingle(),
            var u when u.EndsWith("/api_keys") && req.Method == HttpMethod.Get => Json(HttpStatusCode.OK,
                Envelope(new[] { new { name = "zcode-api-key", apiKey = "old" } })),
            var u when u.Contains("/api_keys/copy/old") => Json(HttpStatusCode.OK, Envelope(new { secretKey = "s1" })),
            _ => Json(HttpStatusCode.NotFound, "{}"),
        });
        var vendor = new ZCodeOAuthVendor(new HttpClient(handler));

        var extra = JsonSerializer.Serialize(new { zaiToken = "zai-tok", org = "o1", project = "p1" });
        var tokens = await vendor.RefreshTokenAsync("zai-tok", extra, CancellationToken.None);
        tokens.AccessToken.Should().Be("old.s1");
        tokens.RefreshToken.Should().Be("zai-tok");
        tokens.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMonths(11));
    }
}

/// <summary>Anthropic 鉴权形态标记（X-AITool-Auth-Mode）单测。</summary>
public sealed class AnthropicAuthModeTests
{
    [Fact]
    public void Default_mode_uses_x_api_key()
    {
        var headers = ProxyForwardService.ResolveAnthropicAuthHeaders("sk-1", new Dictionary<string, string>()).ToList();
        headers.Should().ContainSingle().Which.Should().Be(("x-api-key", "sk-1"));
    }

    [Fact]
    public void Bearer_marker_switches_to_authorization_bearer()
    {
        var headers = ProxyForwardService
            .ResolveAnthropicAuthHeaders("tok-1", new Dictionary<string, string> { [ProxyForwardService.AuthModeHeader] = "bearer" })
            .ToList();
        headers.Should().ContainSingle().Which.Should().Be(("Authorization", "Bearer tok-1"));
    }
}
