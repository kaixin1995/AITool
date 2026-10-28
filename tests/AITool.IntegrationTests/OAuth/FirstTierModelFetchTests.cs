using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AITool.Application.OAuth;
using AITool.Infrastructure.OAuth;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.OAuth;

/// <summary>
/// 一线厂商（MiniMax CN/Global、Copilot、ZCode）远端模型目录拉取链路测试。
/// 全部走桩 HttpMessageHandler，无真实网络；断言真实端点 URL、鉴权形态、排序/过滤规则，
/// 以及「上游失败必须失败、绝不回退静态 DefaultModels 冒充动态目录」的行为。
/// </summary>
public sealed class FirstTierModelFetchTests
{
    private static readonly DeviceOAuthTokenSet Token = new()
    {
        AccessToken = "stub-access",
        RefreshToken = "stub-refresh",
    };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static string Envelope(object? data) => System.Text.Json.JsonSerializer.Serialize(new { code = 0, msg = "ok", data });

    // ---- MiniMax（CN + Global）-------------------------------------------------

    [Theory]
    [InlineData(false, "https://agent.minimax.cn/mavis/api/v1/models?region=cn&buildEnv=prod")]
    [InlineData(true, "https://agent.minimax.io/mavis/api/v1/models?region=en&buildEnv=prod")]
    public async Task MiniMax_uses_regional_mcode_endpoint_and_orders_by_model_order(bool global, string expectedUrl)
    {
        var handler = new StubHandler((_, _) => Json("""
            {"providers":[
              {"providerId":"other","config":{"models":{"wrong":{"name":"Wrong"}}}},
              {"providerId":"minimax","config":{
                "model_order":["m-b","gone","m-a","m-b"],
                "models":{
                  "m-a":{"name":"Model A","limit":{"context":1048576,"output":524288},
                        "modalities":{"input":["text","image"]},"thinking_config":{"mode":"switchable"}},
                  "m-b":{"name":"Model B","reasoning":false,"effort_options":["high"]},
                  "m-c":{"name":"Model C"}
                }}}
            ]}
            """));
        var vendor = new MiniMaxOAuthVendor(new HttpClient(handler), NullLogger<MiniMaxOAuthVendor>.Instance, global);

        vendor.SupportsModelFetch.Should().BeTrue();
        vendor.VendorKey.Should().Be(global ? "minimax-global" : "minimax-cn");

        var models = await vendor.FetchModelsAsync(Token, null, CancellationToken.None);

        // model_order 在前（缺失/重复的 ID 跳过），其余按目录出现顺序补后。
        models.Should().BeEquivalentTo(
        [
            new DeviceOAuthRemoteModel("m-b", "Model B"),
            new DeviceOAuthRemoteModel("m-a", "Model A"),
            new DeviceOAuthRemoteModel("m-c", "Model C"),
        ], options => options.WithStrictOrdering());
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Uri.Should().Be(expectedUrl);
        request.Headers["Authorization"].Should().Be("Bearer stub-access");
        request.Headers["Accept"].Should().Contain("application/json");
    }

    [Fact]
    public async Task MiniMax_retries_without_authorization_when_refused_then_succeeds()
    {
        var handler = new StubHandler(req => req.Headers.ContainsKey("Authorization")
            ? Json("{}", HttpStatusCode.Unauthorized)
            : Json("""{"providers":[{"providerId":"minimax","config":{"models":{"m-a":{"name":"A"}}}}]}"""));
        var vendor = new MiniMaxOAuthVendor(new HttpClient(handler), NullLogger<MiniMaxOAuthVendor>.Instance);

        var models = await vendor.FetchModelsAsync(Token, null, CancellationToken.None);

        models.Should().ContainSingle().Which.Slug.Should().Be("m-a");
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Headers.Should().ContainKey("Authorization");
        handler.Requests[1].Headers.Should().NotContainKey("Authorization");
    }

    [Theory]
    [InlineData(403)]
    [InlineData(500)]
    public async Task MiniMax_propagates_upstream_failures_instead_of_falling_back_to_static(int status)
    {
        var handler = new StubHandler((_, _) => Json("{}", (HttpStatusCode)status));
        var vendor = new MiniMaxOAuthVendor(new HttpClient(handler), NullLogger<MiniMaxOAuthVendor>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => vendor.FetchModelsAsync(Token, null, CancellationToken.None));
        ex.Message.Should().Contain(status.ToString());
    }

    [Theory]
    [InlineData("""{"providers":[{"providerId":"minimax","config":{"models":{}}}]}""")]
    [InlineData("""{"providers":[{"providerId":"other","config":{"models":{"x":{}}}}]}""")]
    [InlineData("""{"providers":[]}""")]
    [InlineData("not-json")]
    public async Task MiniMax_rejects_empty_or_unparsable_catalogs_without_static_fallback(string body)
    {
        var handler = new StubHandler((_, _) => Json(body));
        var vendor = new MiniMaxOAuthVendor(new HttpClient(handler), NullLogger<MiniMaxOAuthVendor>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => vendor.FetchModelsAsync(Token, null, CancellationToken.None));
        ex.Message.Should().NotBeNullOrEmpty();
    }

    // ---- Copilot（已缓存会话令牌 + 持久化的 endpoints.api）-----------------------

    [Fact]
    public async Task Copilot_uses_cached_session_token_and_persisted_api_with_editor_headers_and_filters()
    {
        var handler = new StubHandler((_, _) => Json("""
            {"data":[
              {"id":"gpt-pick","name":"GPT Pick","model_picker_enabled":true,"capabilities":{"type":"chat"}},
              {"id":"claude-terms","name":"Claude Terms","model_picker_category":"versatile",
               "policy":{"state":"disabled","terms":"Enable access"},"capabilities":{"type":"chat"}},
              {"id":"claude-no-terms","name":"No Terms","model_picker_category":"powerful",
               "policy":{"state":"disabled"},"capabilities":{"type":"chat"}},
              {"id":"hidden-snapshot","name":"Hidden","policy":{"state":"enabled"},"capabilities":{"type":"chat"}},
              {"id":"claude-enabled-by-picker","name":"Picker Only","model_picker_enabled":true,"capabilities":{"type":"chat"}},
              {"id":"embed-model","name":"Embed","model_picker_enabled":true,"capabilities":{"type":"embedding"}},
              {"id":"exec-agent-x","name":"Internal","model_picker_enabled":true,"capabilities":{"type":"chat"}},
              {"id":"copilot-search-y","name":"Internal 2","model_picker_enabled":true,"capabilities":{"type":"chat"}},
              {"id":"model-secondary","name":"Internal 3","model_picker_enabled":true,"capabilities":{"type":"chat"}},
              {"id":"kimi-exp","name":"Experimental","vendor":"Experimental","model_picker_enabled":true,"capabilities":{"type":"chat"}}
            ]}
            """));
        var vendor = new CopilotOAuthVendor(new HttpClient(handler));

        var models = await vendor.FetchModelsAsync(Token, """{"chatBaseUrl":"https://copilot-api.acme.ghe.com/"}""", CancellationToken.None);

        // 只保留：chat 能力、非 internal、非 Experimental、picker/category 可见、
        // policy enabled / 无 policy 且 picker / 有 terms 待同意；无 terms 的 disabled 与旧快照剔除。
        models.Select(m => m.Slug).Should().BeEquivalentTo(
            ["gpt-pick", "claude-terms", "claude-enabled-by-picker"], options => options.WithStrictOrdering());
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Uri.Should().Be("https://copilot-api.acme.ghe.com/models");
        request.Headers["Authorization"].Should().Be("Bearer stub-access", "账号 AccessToken 即后台刷新持有的 Copilot 会话令牌");
        request.Headers["Editor-Version"].Should().Be("vscode/1.140.0");
        request.Headers["Editor-Plugin-Version"].Should().Be("copilot-chat/0.68.0");
        request.Headers["Copilot-Integration-Id"].Should().Be("vscode-chat");
        request.Headers["User-Agent"].Should().Be("GitHubCopilotChat/0.68.0");
        request.Headers["X-GitHub-Api-Version"].Should().Be("2026-01-09");
    }

    [Fact]
    public async Task Copilot_falls_back_to_public_api_when_persisted_base_is_missing_or_untrusted()
    {
        var handler = new StubHandler((_, _) => Json("""{"data":[{"id":"gpt-4.1","model_picker_enabled":true,"capabilities":{"type":"chat"}}]}"""));
        var vendor = new CopilotOAuthVendor(new HttpClient(handler));

        await vendor.FetchModelsAsync(Token, """{"chatBaseUrl":"https://evil.example.com"}""", CancellationToken.None);

        handler.Requests.Should().ContainSingle().Which.Uri.Should().Be("https://api.githubcopilot.com/models");
    }

    [Theory]
    [InlineData(401, "拒绝")]
    [InlineData(403, "拒绝")]
    [InlineData(503, "503")]
    public async Task Copilot_distinguishes_credential_refusals_from_transient_failures(int status, string expected)
    {
        var handler = new StubHandler((_, _) => Json("{}", (HttpStatusCode)status));
        var vendor = new CopilotOAuthVendor(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => vendor.FetchModelsAsync(Token, null, CancellationToken.None));
        ex.Message.Should().Contain(expected);
    }

    [Fact]
    public async Task Copilot_rejects_empty_catalog_without_static_fallback()
    {
        var handler = new StubHandler((_, _) => Json("""{"data":[]}"""));
        var vendor = new CopilotOAuthVendor(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => vendor.FetchModelsAsync(Token, null, CancellationToken.None));
    }

    // ---- ZCode（configs → provider 配置 → 订阅判定套餐 → gift 追加）--------------

    private const string ProviderConfigJson = """
        {"revision":7,"config":{
          "providerConfigRules":{"providerRules":[
            {"providerId":"account:zai-individual-coding-plan","builtinModelIds":["GLM-5.3","GLM-5.3-Flash"]},
            {"providerId":"account:zai-start-plan","builtinModelIds":["GLM-5.2"]},
            {"providerId":"account:bigmodel-individual-coding-plan","builtinModelIds":["GLM-Other-Plan"]}
          ]},
          "modelConfigRules":{"builtinProviderModelRules":[
            {"providerId":"account:zai-individual-coding-plan","modelId":"GLM-5.3-Flash","config":{"enabled":false}},
            {"providerId":"account:zai-individual-coding-plan","modelId":"GLM-5-Turbo","config":{"enabled":true}},
            {"providerId":"account:zai-individual-coding-plan","modelId":"GLM-5.3"}
          ]}}}
        """;

    private static string GiftBalanceJson => Envelope(new
    {
        server_time = 1_750_000_000L,
        plans = new[] { new { plan_id = "zai-start-plan", user_plan_id = "up1", name = "Start Plan", status = "active", ends_at = 1_750_864_000L } },
        balances = new object[]
        {
            new { plan_id = "zai-start-plan", user_plan_id = "up1", capabilities = new[] { "model:GLM-5.2", "model:GLM-9.9" }, remaining_units = 5 },
            new { plan_id = "zai-start-plan", user_plan_id = "up1", capabilities = new[] { "model:GLM-5-Turbo" }, remaining_units = 0 },
        },
    });

    private static ZCodeStubHandler ZCodeHandler(string? subscriptionData = null, string? balanceData = null)
        => new((req, _) => req.Uri.AbsoluteUri switch
        {
            var u when u.StartsWith("https://zcode.z.ai/api/v1/client/configs") => Json(Envelope(new
            {
                configs = new { builtin_provider_config_json = "https://cdn.z.ai/static/zcode-builtin.json" },
            })),
            "https://cdn.z.ai/static/zcode-builtin.json" => Json(ProviderConfigJson),
            "https://api.z.ai/api/biz/subscription/list" => subscriptionData is null
                ? Json(Envelope(Array.Empty<object>()))
                : Json(Envelope(new[] { new { productName = "GLM Coding Pro", status = "VALID" } })),
            "https://zcode.z.ai/api/v1/zcode-plan/billing/balance" => balanceData is null
                ? Json(Envelope(new { plans = Array.Empty<object>(), balances = Array.Empty<object>() }))
                : Json(balanceData),
            _ => Json("{}", HttpStatusCode.NotFound),
        });

    [Fact]
    public async Task ZCode_fetches_remote_config_filters_models_by_the_accounts_own_plan_and_appends_live_gift_trials()
    {
        var handler = ZCodeHandler(subscriptionData: "valid");
        handler.Configure(req => req.Uri.AbsoluteUri switch
        {
            "https://cdn.z.ai/static/zcode-builtin.json" => Json(ProviderConfigJson),
            "https://api.z.ai/api/biz/subscription/list" => Json(Envelope(new[] { new { productName = "GLM Coding Pro", status = "VALID" } })),
            "https://zcode.z.ai/api/v1/zcode-plan/billing/balance" => Json(GiftBalanceJson()),
            _ => Json("{}", HttpStatusCode.NotFound),
        });
        var vendor = new ZCodeOAuthVendor(new HttpClient(handler));
        var extra = """{"zaiToken":"zai-jwt","org":"o1","project":"p1"}""";

        var models = await vendor.FetchModelsAsync(Token, extra, CancellationToken.None);

        // 只取该账号自己套餐（zai-individual）的 ID：start-plan / bigmodel 的模型不混入；
        // GLM-5.3-Flash 被 enabled=false 剔除；GLM-5-Turbo 由 builtinProviderModelRules 追加。
        // 有余额的 gift（GLM-5.2/GLM-9.9）以 -Trial 别名追加；花完的桶（GLM-5-Turbo）不追加。
        models.Should().BeEquivalentTo(
        [
            new DeviceOAuthRemoteModel("GLM-5.3", "GLM-5.3"),
            new DeviceOAuthRemoteModel("GLM-5-Turbo", "GLM-5-Turbo"),
            new DeviceOAuthRemoteModel("GLM-5.2-Trial", "GLM-5.2-Trial"),
            new DeviceOAuthRemoteModel("GLM-9.9-Trial", "GLM-9.9-Trial"),
        ], options => options.WithStrictOrdering());

        var configs = handler.Requests.Single(r => r.Uri.AbsoluteUri.StartsWith("https://zcode.z.ai/api/v1/client/configs"));
        configs.Uri.Query.Should().Be("?app_version=3.16.3&platform=" + Regex.Match(configs.Uri.Query, @"platform=([\w-]+)").Groups[1].Value);
        configs.Uri.Query.Should().MatchRegex(@"^<app_version=3\.16\.3&platform=(win32|darwin|linux)-(x64|arm64)>$".Replace('<', "(").Replace('>', ")")[1..^1].Length > 0 ? configs.Uri.Query : configs.Uri.Query);
        configs.Uri.Query.Should().MatchRegex(@"app_version=3\.16\.3&platform=(win32|darwin|linux)-(x64|arm64)");
        configs.Headers["X-Device-Mid"].Should().NotBeNullOrEmpty();

        var configDownload = handler.Requests.Single(r => r.Uri.AbsoluteUri == "https://cdn.z.ai/static/zcode-builtin.json");
        configDownload.Headers["User-Agent"].Should().Be("ZCode/3.16.3");

        var subscription = handler.Requests.Single(r => r.Uri.AbsoluteUri == "https://api.z.ai/api/biz/subscription/list");
        subscription.Headers["Authorization"].Should().Be("stub-access", "订阅端点以裸铸造 key 鉴权");

        var balance = handler.Requests.Single(r => r.Uri.AbsoluteUri.Contains("/api/v1/zcode-plan/billing/balance"));
        balance.Headers["Authorization"].Should().Be("Bearer zai-jwt", "gift 余额以登录时的 zai token 鉴权");
    }

    [Fact]
    public async Task ZCode_uses_start_plan_catalog_and_plain_gift_names_without_own_subscription()
    {
        var handler = ZCodeHandler(subscriptionData: null, balanceData: GiftBalanceJson());
        var vendor = new ZCodeOAuthVendor(new HttpClient(handler));

        var models = await vendor.FetchModelsAsync(Token, """{"zaiToken":"zai-jwt"}""", CancellationToken.None);

        // 无 VALID 订阅 → 走 Start Plan 目录（account:zai-start-plan 的 GLM-5.2），
        // gift 的模型按余额原名单独给出，再补 -Trial 别名。
        models.Select(m => m.Slug).Should().BeEquivalentTo(
            ["GLM-5.2", "GLM-9.9", "GLM-5.2-Trial", "GLM-9.9-Trial"], options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task ZCode_still_returns_plan_catalog_when_gift_balance_is_unreadable()
    {
        var handler = ZCodeHandler(subscriptionData: "valid");
        handler.Configure(req => req.Uri.AbsoluteUri switch
        {
            "https://api.z.ai/api/biz/subscription/list" => Json(Envelope(new[] { new { status = "VALID" } })),
            "https://zcode.z.ai/api/v1/zcode-plan/billing/balance" => Json("{}", HttpStatusCode.InternalServerError),
            _ => Json(ProviderConfigJson),
        });
        var vendor = new ZCodeOAuthVendor(new HttpClient(handler));

        var models = await vendor.FetchModelsAsync(Token, """{"zaiToken":"zai-jwt"}""", CancellationToken.None);

        // gift 读不到只是没有体验模型（参考行为），套餐目录照常返回。
        models.Select(m => m.Slug).Should().BeEquivalentTo(["GLM-5.3", "GLM-5-Turbo"]);
    }

    [Fact]
    public async Task ZCode_fails_explicitly_when_the_plan_cannot_be_identified()
    {
        var handler = ZCodeHandler();
        handler.Configure(_ => Json("{}", HttpStatusCode.InternalServerError));
        handler.Routes["https://api.z.ai/api/biz/subscription/list"] = _ => Json("{}", HttpStatusCode.InternalServerError);
        var vendor = new ZCodeOAuthVendor(new HttpClient(handler));

        // 订阅端点读不出 → 无法辨识套餐 → 明确失败，绝不静态兜底。
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            vendor.FetchModelsAsync(Token, """{"zaiToken":"zai-jwt"}""", CancellationToken.None));
        ex.Message.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ZCode_reports_credential_refusal_from_the_subscription_endpoint()
    {
        var handler = ZCodeHandler();
        handler.Routes["https://api.z.ai/api/biz/subscription/list"] = _ => Json("{}", HttpStatusCode.Forbidden);
        var vendor = new ZCodeOAuthVendor(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<DeviceOAuthRefreshRefusedException>(() =>
            vendor.FetchModelsAsync(Token, """{"zaiToken":"zai-jwt"}""", CancellationToken.None));
        ex.Message.Should().Contain("403");
    }

    [Fact]
    public async Task ZCode_fails_when_configs_name_no_provider_config_url()
    {
        var handler = ZCodeHandler();
        handler.Routes["https://zcode.z.ai/api/v1/client/configs?app_version=3.16.3&platform="] = _ =>
            Json(Envelope(new { configs = new { builtin_provider_config_json = "http://insecure.example.com/config.json" } }));
        var vendor = new ZCodeOAuthVendor(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            vendor.FetchModelsAsync(Token, """{"zaiToken":"zai-jwt"}""", CancellationToken.None));
        handler.Requests.Should().NotContain(r => r.Uri.AbsoluteUri.Contains("insecure.example.com"));
    }

    [Fact]
    public async Task ZCode_fails_when_the_plan_has_no_models_and_no_gift()
    {
        var handler = ZCodeHandler(subscriptionData: null, balanceData: null);
        handler.Routes["https://cdn.z.ai/static/zcode-builtin.json"] = _ => Json("""{"revision":1,"config":{}}""");
        var vendor = new ZCodeOAuthVendor(new HttpClient(handler));

        // Start Plan 目录为空且无 gift 余额 → 明确失败，不回退静态清单。
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            vendor.FetchModelsAsync(Token, """{"zaiToken":"zai-jwt"}""", CancellationToken.None));
    }

    [Fact]
    public async Task ZCode_fails_when_the_provider_config_download_breaks()
    {
        var handler = ZCodeHandler();
        handler.Routes["https://cdn.z.ai/static/zcode-builtin.json"] = _ => Json("{}", HttpStatusCode.BadGateway);
        var vendor = new ZCodeOAuthVendor(new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            vendor.FetchModelsAsync(Token, """{"zaiToken":"zai-jwt"}""", CancellationToken.None));
        ex.Message.Should().Contain("502");
    }
}

/// <summary>按 URI 精确匹配分派的桩（含请求记录，供断言端点/头）。</summary>
public sealed class ZCodeStubHandler
{
    private readonly Func<RecordedRequest, HttpResponseMessage> _fallback;
    private readonly List<RecordedRequest> _requests = [];

    public Dictionary<string, Func<RecordedRequest, HttpResponseMessage>> Routes { get; } = new();
    public IReadOnlyList<RecordedRequest> Requests => _requests;

    public ZCodeStubHandler(Func<RecordedRequest, HttpResponseMessage> fallback) => _fallback = fallback;

    public void Configure(Func<RecordedRequest, HttpResponseMessage> responder) => Routes[string.Empty] = responder;

    public HttpClient CreateClient() => new(new Handler(this));

    private HttpResponseMessage Respond(RecordedRequest request)
        => Routes.TryGetValue(request.Uri.AbsoluteUri, out var route) ? route(request)
           : Routes.TryGetValue(string.Empty, out var catchAll) ? catchAll(request)
           : _fallback(request);

    private sealed class Handler(ZCodeStubHandler owner) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var recorded = new RecordedRequest(
                request.RequestUri!,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase));
            owner._requests.Add(recorded);
            return Task.FromResult(owner.Respond(recorded));
        }
    }

    public sealed record RecordedRequest(Uri Uri, IReadOnlyDictionary<string, string> Headers);
}
