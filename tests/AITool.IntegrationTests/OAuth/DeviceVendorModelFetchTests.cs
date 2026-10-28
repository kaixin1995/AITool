using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Infrastructure.OAuth;
using FluentAssertions;
using Xunit;

namespace AITool.IntegrationTests.OAuth;

/// <summary>
/// Kiro / Trae（CN+Global）/ Qoder 远端模型目录拉取测试：请求形态（URL/方法/头/签名链）、
/// 响应解析（默认模型置首、场景合并、function 列表过滤、host 选择）与错误面
/// （空目录 / HTTP 失败 / 缺账号身份）。全部走桩 HttpMessageHandler，无真实网络。
/// </summary>
public sealed class DeviceVendorModelFetchTests
{
    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Url,
        string? Body,
        IReadOnlyDictionary<string, string> Headers);

    private sealed class StubHandler(Func<CapturedRequest, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }

            if (request.Content?.Headers is { } contentHeaders)
            {
                foreach (var header in contentHeaders)
                {
                    headers[header.Key] = string.Join(",", header.Value);
                }
            }

            var captured = new CapturedRequest(request.Method, request.RequestUri!, body, headers);
            Requests.Add(captured);
            return Task.FromResult(responder(captured));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static AITool.Application.OAuth.DeviceOAuthTokenSet Tokens(string access = "at-1")
        => new() { AccessToken = access, RefreshToken = "rt-1", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };

    private static string Extra(object payload) => JsonSerializer.Serialize(payload);

    // ---- Kiro ------------------------------------------------------------------

    [Fact]
    public async Task Kiro_fetch_uses_extra_region_and_profile_and_orders_default_first()
    {
        const string arn = "arn:aws:codewhisperer:us-west-2:123456789012:profile/IDC";
        var handler = new StubHandler(req =>
        {
            req.Url.Host.Should().Be("management.us-west-2.kiro.dev");
            req.Method.Should().Be(HttpMethod.Get);
            req.Headers["Authorization"].Should().Be("Bearer at-1");
            return Json(HttpStatusCode.OK, """
                {"defaultModel":{"modelId":"auto"},
                 "models":[
                   {"modelId":"claude-opus-5.5","modelName":"Claude Opus 5.5","tokenLimits":{"maxInputTokens":1000000,"maxOutputTokens":128000},"supportedInputTypes":["TEXT","IMAGE"]},
                   {"modelId":"auto","modelName":"auto"},
                   {"modelId":"claude-sonnet-5.5"},
                   {"modelId":null}
                 ]}
                """);
        });
        var vendor = new KiroOAuthVendor(new HttpClient(handler));
        var extra = Extra(new { method = "social", region = "us-west-2", profileArn = arn });

        var models = await vendor.FetchModelsAsync(Tokens(), extra, CancellationToken.None);

        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Url.AbsolutePath.Should().Be("/List-Available-Models");
        // 查询串必须 origin 在前、profileArn 在后（URLSearchParams 排序语义）。
        request.Url.Query.Should().Be("?origin=KIRO_CLI&profileArn=" + Uri.EscapeDataString(arn));

        // defaultModel.modelId 排首；无 modelId 的条目丢弃；缺失 modelName 回退 modelId。
        models.Select(m => m.Slug).Should().Equal("auto", "claude-opus-5.5", "claude-sonnet-5.5");
        models[0].DisplayName.Should().Be("auto");
        models[1].DisplayName.Should().Be("Claude Opus 5.5");
        models[2].DisplayName.Should().Be("claude-sonnet-5.5");
        vendor.SupportsModelFetch.Should().BeTrue();
    }

    [Fact]
    public async Task Kiro_fetch_discovers_profile_arn_when_extra_has_none()
    {
        const string arn = "arn:aws:codewhisperer:us-east-1:222222222222:profile/IDC";
        var handler = new StubHandler(req => req.Url.AbsolutePath switch
        {
            "/List-Available-Profiles" => Json(HttpStatusCode.OK, $$"""{"profiles":[{"arn":"{{arn}}"}]}"""),
            "/List-Available-Models" => Json(HttpStatusCode.OK,
                """{"defaultModel":{"modelId":"claude-opus-5.5"},"models":[{"modelId":"claude-opus-5.5","modelName":"Claude Opus 5.5"}]}"""),
            _ => throw new InvalidOperationException("unexpected " + req.Url),
        });
        var vendor = new KiroOAuthVendor(new HttpClient(handler));
        var extra = Extra(new { method = "social" });

        var models = await vendor.FetchModelsAsync(Tokens(), extra, CancellationToken.None);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Url.AbsolutePath.Should().Be("/List-Available-Profiles");
        handler.Requests[0].Url.Host.Should().Be("management.us-east-1.kiro.dev", "region 缺失时按默认 us-east-1 探测");
        handler.Requests[1].Url.AbsolutePath.Should().Be("/List-Available-Models");
        handler.Requests[1].Url.Query.Should().Be("?origin=KIRO_CLI&profileArn=" + Uri.EscapeDataString(arn));
        models.Select(m => m.Slug).Should().Equal("claude-opus-5.5");
    }

    [Fact]
    public async Task Kiro_fetch_empty_catalog_and_http_failure_and_no_profile_are_errors()
    {
        var empty = new StubHandler(_ => Json(HttpStatusCode.OK, """{"defaultModel":{"modelId":"auto"},"models":[]}"""));
        var emptyVendor = new KiroOAuthVendor(new HttpClient(empty));
        var emptyAct = () => emptyVendor.FetchModelsAsync(
            Tokens(), Extra(new { region = "us-east-1", profileArn = "arn:p" }), CancellationToken.None);
        await emptyAct.Should().ThrowAsync<InvalidOperationException>().WithMessage("*未返回任何可用模型*");

        var failed = new StubHandler(_ => Json(HttpStatusCode.InternalServerError, """{"message":"boom"}"""));
        var failedVendor = new KiroOAuthVendor(new HttpClient(failed));
        var failedAct = () => failedVendor.FetchModelsAsync(
            Tokens(), Extra(new { region = "us-east-1", profileArn = "arn:p" }), CancellationToken.None);
        await failedAct.Should().ThrowAsync<InvalidOperationException>().WithMessage("*List-Available-Models*500*");

        var noProfile = new StubHandler(_ => Json(HttpStatusCode.OK, """{"profiles":[]}"""));
        var noProfileVendor = new KiroOAuthVendor(new HttpClient(noProfile));
        var noProfileAct = () => noProfileVendor.FetchModelsAsync(Tokens(), Extra(new { region = "us-east-1" }), CancellationToken.None);
        await noProfileAct.Should().ThrowAsync<InvalidOperationException>().WithMessage("*List-Available-Profiles*");
    }

    // ---- Trae ------------------------------------------------------------------

    private const string TraeAppId = "6eefa01c-1036-4c7e-9ca5-d891f63bfcd8";
    private static readonly string[] TraeFunctions =
    [
        "chat_v3", "solo_work_lite", "solo_agent", "solo_agent_lite", "solo_coder",
    ];

    [Fact]
    public async Task Trae_cn_batch_request_shape_headers_and_filters()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            {"function_configs":[
              {"function":"chat_v3","config_info_list":[
                {"config_name":"glm-5.2","usage":"chat_completion","display_config":{"display_name":"GLM-5.2"}},
                {"config_name":"summary","usage":"summary"},
                {"config_name":"custom_model_200k","usage":"chat_completion"},
                {"config_name":"kimi-k2","usage":"chat_completion","config_switch":false}
              ]},
              {"function":"solo_agent","config_info_list":[
                {"config_name":"deepseek-v4.1-flash","usage":"chat_completion","display_config":{"display_name":"DeepSeek-V4.1-Flash"}}
              ]},
              {"function":"builder","config_info_list":[{"config_name":"builder-only","usage":"chat_completion"}]}
            ]}
            """));
        var vendor = new TraeOAuthVendor(new HttpClient(handler));
        var extra = Extra(new { deviceId = "1234567890123456", machineId = "ab".Repeat(16), uid = "u-1" });

        var models = await vendor.FetchModelsAsync(Tokens("jwt-1"), extra, CancellationToken.None);

        // 批量端点成功时不再逐 function 兜底。
        var request = handler.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Post);
        request.Url.AbsoluteUri.Should().Be("https://trae-api-cn.mchost.guru/api/ide/v1/batch_get_detail_param");

        var body = JsonDocument.Parse(request.Body!).RootElement;
        body.GetProperty("functions").EnumerateArray().Select(f => f.GetString()).Should().Equal(TraeFunctions);
        body.GetProperty("agent_type").GetString().Should().BeEmpty();
        body.GetProperty("current_config_info").GetProperty("config_name").GetString().Should().BeEmpty();
        body.GetProperty("current_config_info").GetProperty("is_custom_model").GetBoolean().Should().BeFalse();
        body.GetProperty("mode_type").GetInt32().Should().Be(0);
        body.GetProperty("access_type").GetInt32().Should().Be(0);
        body.GetProperty("ab_force_vids").GetString().Should().BeEmpty();
        body.GetProperty("ab_autotest_advanced_mode").GetInt32().Should().Be(0);
        body.GetProperty("show_custom_model").GetBoolean().Should().BeTrue();

        request.Headers["Authorization"].Should().Be("Cloud-IDE-JWT jwt-1");
        request.Headers["X-Cloudide-Token"].Should().Be("jwt-1");
        request.Headers["x-ide-token"].Should().Be("jwt-1");
        request.Headers["x-uid"].Should().Be("u-1");
        request.Headers["x-app-id"].Should().Be(TraeAppId);
        request.Headers["x-device-id"].Should().Be("1234567890123456");
        request.Headers["x-machine-id"].Should().Be("ab".Repeat(16));
        request.Headers["x-ide-version"].Should().Be("0.1.69");
        request.Headers["x-ide-version-code"].Should().Be("20260917");
        request.Headers["x-app-version"].Should().Be("0.1.69");

        // IDE 助手 / custom_model 槽位 / 关断配置 / 其他 function 的列表不进目录。
        models.Select(m => m.Slug).Should().Equal("glm-5.2", "deepseek-v4.1-flash");
        models[0].DisplayName.Should().Be("GLM-5.2");
        models[1].DisplayName.Should().Be("DeepSeek-V4.1-Flash");
    }

    [Fact]
    public async Task Trae_falls_back_to_per_function_lists_when_batch_has_none()
    {
        var handler = new StubHandler(req =>
        {
            if (req.Url.AbsolutePath == "/api/ide/v1/batch_get_detail_param")
            {
                return Json(HttpStatusCode.OK, """{"code":0}""");
            }

            var function = JsonDocument.Parse(req.Body!).RootElement.GetProperty("function").GetString();
            return function == "solo_agent"
                ? Json(HttpStatusCode.OK, """{"config_info_list":[{"config_name":"deepseek-v4.1-flash","usage":"chat_completion"}]}""")
                : Json(HttpStatusCode.OK, """{"config_info_list":[]}""");
        });
        var vendor = new TraeOAuthVendor(new HttpClient(handler));
        var extra = Extra(new { deviceId = "1234567890123456", machineId = "ab".Repeat(16) });

        var models = await vendor.FetchModelsAsync(Tokens("jwt-1"), extra, CancellationToken.None);

        handler.Requests[0].Url.AbsolutePath.Should().Be("/api/ide/v1/batch_get_detail_param");
        handler.Requests.Skip(1).Select(r => r.Url.AbsolutePath)
            .Should().OnlyContain(p => p == "/api/ide/v1/get_detail_param");
        handler.Requests.Should().HaveCount(1 + TraeFunctions.Length, "批量无列表时逐 function 兜底一次");
        var fallback = handler.Requests[3];
        JsonDocument.Parse(fallback.Body!).RootElement.GetProperty("function").GetString().Should().Be("solo_agent");
        fallback.Url.Host.Should().Be("trae-api-cn.mchost.guru");
        models.Select(m => m.Slug).Should().Equal("deepseek-v4.1-flash");
    }

    [Fact]
    public async Task Trae_global_hides_invisible_models_but_cn_keeps_them()
    {
        const string listing = """
            {"function_configs":[{"function":"chat_v3","config_info_list":[
              {"config_name":"gpt-5.4","usage":"chat_completion","display_config":{"display_name":"GPT-5.4"}},
              {"config_name":"gemini-3.1-pro-paygo","usage":"chat_completion","display_config":{"display_name":"Gemini 3.1 Pro"},"is_invisible_to_user":true}
            ]}]}
            """;
        var globalHandler = new StubHandler(_ => Json(HttpStatusCode.OK, listing));
        var global = new TraeOAuthVendor(new HttpClient(globalHandler), global: true);
        var globalExtra = Extra(new { deviceId = "1234567890123456789", machineId = "ab".Repeat(16) });

        var globalModels = await global.FetchModelsAsync(Tokens("jwt-1"), globalExtra, CancellationToken.None);
        globalHandler.Requests.Should().ContainSingle();
        globalHandler.Requests[0].Url.Host.Should().Be("coresg-normal.trae.ai", "Global 默认走 SG 模型网关");
        // Global 隐藏 is_invisible_to_user 条目（Auto 通道 / pay-as-you-go 双子）。
        globalModels.Select(m => m.Slug).Should().Equal(["gpt-5.4"]);

        var cnHandler = new StubHandler(_ => Json(HttpStatusCode.OK, listing));
        var cn = new TraeOAuthVendor(new HttpClient(cnHandler));
        var cnExtra = Extra(new { deviceId = "1234567890123456", machineId = "ab".Repeat(16) });

        var cnModels = await cn.FetchModelsAsync(Tokens("jwt-1"), cnExtra, CancellationToken.None);
        cnHandler.Requests[0].Url.Host.Should().Be("trae-api-cn.mchost.guru");
        // CN 的列表整单呈现（隐藏标记只对 Global 生效）。
        cnModels.Select(m => m.Slug).Should().Equal(["gpt-5.4", "gemini-3.1-pro-paygo"]);
    }

    [Fact]
    public async Task Trae_callback_records_host_and_region_and_fetch_uses_recorded_host()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, """
            {"function_configs":[{"function":"chat_v3","config_info_list":[
              {"config_name":"gpt-5.4","usage":"chat_completion","display_config":{"display_name":"GPT-5.4"}}
            ]}]}
            """));
        var vendor = new TraeOAuthVendor(new HttpClient(handler), global: true);

        var start = await vendor.StartSignInAsync(CancellationToken.None);
        var completed = await vendor.CompleteWithCallbackAsync(
            start.SessionJson,
            "http://127.0.0.1:17895/authorize?userJwt=jwt-us&refreshToken=rt-us&uid=u-us&region=US-East&host=https://api-us-east.trae.ai",
            CancellationToken.None);

        completed.Tokens.Should().NotBeNull();
        var extra = JsonDocument.Parse(completed.ExtraJson!);
        extra.RootElement.GetProperty("modelHost").GetString().Should().Be("https://api-us-east.trae.ai");
        extra.RootElement.GetProperty("region").GetString().Should().Be("US-East");
        extra.RootElement.GetProperty("uid").GetString().Should().Be("u-us");

        await vendor.FetchModelsAsync(Tokens("jwt-us"), completed.ExtraJson, CancellationToken.None);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Url.AbsoluteUri.Should().StartWith("https://api-us-east.trae.ai/api/ide/v1/batch_get_detail_param");
    }

    [Fact]
    public async Task Trae_invalid_model_host_in_extra_falls_back_to_default()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK,
            """{"function_configs":[{"function":"chat_v3","config_info_list":[{"config_name":"glm-5.2","usage":"chat_completion"}]}]}"""));
        var vendor = new TraeOAuthVendor(new HttpClient(handler));
        var extra = Extra(new { deviceId = "1234567890123456", machineId = "ab".Repeat(16), modelHost = "https://evil.example.com" });

        await vendor.FetchModelsAsync(Tokens("jwt-1"), extra, CancellationToken.None);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Url.Host.Should().Be("trae-api-cn.mchost.guru", "非 Trae 域的 host 不采纳，回默认网关");
    }

    // ---- Qoder -----------------------------------------------------------------

    [Fact]
    public async Task Qoder_model_list_signs_cosy_headers_and_merges_scenes()
    {
        CapturedRequest? captured = null;
        var handler = new StubHandler(req =>
        {
            captured = req;
            return Json(HttpStatusCode.OK, """
                {"app":[
                  {"key":"fast","display_name":"Fast","enable":true},
                  {"key":"auto","enable":true}
                ],
                 "chat":[
                  {"key":"performance","display_name":"Performance","enable":true},
                  {"key":"fast","display_name":"Fast (chat)","enable":true},
                  {"key":"off","display_name":"Off","enable":false},
                  {"key":"","enable":true},
                  {"key":"default","enable":true}
                ]}
                """);
        });
        var vendor = new QoderOAuthVendor(new HttpClient(handler));
        var extra = Extra(new { deviceToken = "dt-1", machineId = "m-1", tokenSource = "job", uid = "u-1", name = "Ann", email = "a@x.com" });

        var models = await vendor.FetchModelsAsync(Tokens("jt-1"), extra, CancellationToken.None);

        captured.Should().NotBeNull();
        var request = captured!;
        request.Method.Should().Be(HttpMethod.Get);
        request.Url.AbsoluteUri.Should().Be("https://api3.qoder.sh/algo/api/v2/model/list?Encode=1");

        // COSY 头形态（对齐 qoder 插件 cosyHeaders）。
        request.Headers["Cosy-User"].Should().Be("u-1");
        request.Headers["Cosy-MachineId"].Should().Be("m-1");
        request.Headers["Cosy-MachineToken"].Should().Be("m-1");
        request.Headers["Cosy-ClientIp"].Should().Be("m-1");
        request.Headers["Cosy-ClientType"].Should().Be("10");
        request.Headers["Cosy-MachineType"].Should().Be("5");
        request.Headers["Cosy-MachineOS"].Should().Match("*_*");
        request.Headers["Cosy-Scene"].Should().Be("app");
        request.Headers["Cosy-Business-Product"].Should().Be("app");
        request.Headers["Cosy-Business-Type"].Should().Be("agent");
        request.Headers["Cosy-Data-Policy"].Should().Be("disagree");
        request.Headers["Cosy-Version"].Should().Be("1.1.69");
        request.Headers["Login-Version"].Should().Be("v2");
        request.Headers["Accept-Encoding"].Should().Be("identity");

        // 签名链：Authorization = Bearer COSY.{payload}.{sig}，
        // sig = md5(payload\nCosy-Key\nts\nwireBody\npath)，path 去掉 /algo 前缀。
        var authorization = request.Headers["Authorization"];
        authorization.Should().StartWith("Bearer COSY.");
        var segments = authorization["Bearer ".Length..].Split('.');
        segments.Should().HaveCount(3);
        segments[0].Should().Be("COSY");
        var payloadJson = Encoding.UTF8.GetString(Convert.FromBase64String(segments[1]));
        using var payload = JsonDocument.Parse(payloadJson);
        payload.RootElement.GetProperty("version").GetString().Should().Be("v1");
        payload.RootElement.GetProperty("cosyVersion").GetString().Should().Be("1.1.69");
        payload.RootElement.GetProperty("ideVersion").GetString().Should().BeEmpty();
        var info = payload.RootElement.GetProperty("info").GetString()!;
        var infoBytes = Convert.FromBase64String(info);
        infoBytes.Should().NotBeEmpty();
        (infoBytes.Length % 16).Should().Be(0, "AES-128-CBC 密文按 16 字节对齐");

        var wrappedKey = request.Headers["Cosy-Key"];
        Convert.FromBase64String(wrappedKey).Should().HaveCount(128, "RSA-1024 包装的 AES 密钥");
        var timestamp = request.Headers["Cosy-Date"];
        long.TryParse(timestamp, out _).Should().BeTrue();
        var expectedSig = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(
            $"{segments[1]}\n{wrappedKey}\n{timestamp}\n\n/api/v2/model/list"))).ToLowerInvariant();
        segments[2].Should().Be(expectedSig, "MD5 签名覆盖 payload、包装密钥、时间戳、空 wire body 与去前缀路径");

        // app 场景优先（app 的 Fast 压过 chat 的别名），enable/auto/default/空 过滤。
        models.Select(m => m.Slug).Should().Equal("performance", "fast");
        models[0].DisplayName.Should().Be("Performance");
        models[1].DisplayName.Should().Be("Fast");
        vendor.SupportsModelFetch.Should().BeTrue();
    }

    [Fact]
    public async Task Qoder_fetch_requires_uid_and_machine_id()
    {
        var vendor = new QoderOAuthVendor(new HttpClient(new StubHandler(_ => Json(HttpStatusCode.OK, "{}"))));

        var noUid = () => vendor.FetchModelsAsync(
            Tokens(), Extra(new { machineId = "m-1", deviceToken = "dt" }), CancellationToken.None);
        await noUid.Should().ThrowAsync<InvalidOperationException>().WithMessage("*uid*");

        var noMachine = () => vendor.FetchModelsAsync(
            Tokens(), Extra(new { uid = "u-1", deviceToken = "dt" }), CancellationToken.None);
        await noMachine.Should().ThrowAsync<InvalidOperationException>().WithMessage("*machineId*");
    }

    [Fact]
    public async Task Qoder_fetch_http_failure_and_empty_catalog_are_errors()
    {
        var failed = new StubHandler(_ => Json(HttpStatusCode.Unauthorized, """{"message":"denied"}"""));
        var failedVendor = new QoderOAuthVendor(new HttpClient(failed));
        var failedAct = () => failedVendor.FetchModelsAsync(
            Tokens(), Extra(new { uid = "u-1", machineId = "m-1" }), CancellationToken.None);
        await failedAct.Should().ThrowAsync<InvalidOperationException>().WithMessage("*401*");

        var empty = new StubHandler(_ => Json(HttpStatusCode.OK, """{"app":[{"key":"auto","enable":true}]}"""));
        var emptyVendor = new QoderOAuthVendor(new HttpClient(empty));
        var emptyAct = () => emptyVendor.FetchModelsAsync(
            Tokens(), Extra(new { uid = "u-1", machineId = "m-1" }), CancellationToken.None);
        await emptyAct.Should().ThrowAsync<InvalidOperationException>().WithMessage("*未返回任何可用模型*");
    }

    [Fact]
    public async Task Qoder_sign_in_records_identity_fields_for_cosy()
    {
        var handler = new StubHandler(req => req.Url.AbsolutePath switch
        {
            "/api/v1/deviceToken/poll" => Json(HttpStatusCode.OK,
                """{"token":"dt-1","refresh_token":"drt-1","user_id":"u-9","expires_at":4102444800000}"""),
            "/api/v1/me/jobToken" => Json(HttpStatusCode.OK,
                """{"token":"jt-1","refresh_token":"jrt-1","expired_at":4102444800000}"""),
            "/api/v1/userinfo" => Json(HttpStatusCode.OK, """{"id":"u-9","email":"a@x.com","name":"Ann"}"""),
            _ => throw new InvalidOperationException("unexpected " + req.Url),
        });
        var vendor = new QoderOAuthVendor(new HttpClient(handler));

        var start = await vendor.StartSignInAsync(CancellationToken.None);
        var poll = await vendor.PollSignInAsync(start.SessionJson, CancellationToken.None);

        poll.Tokens.Should().NotBeNull();
        poll.Tokens!.AccessToken.Should().Be("jt-1");
        poll.Email.Should().Be("a@x.com");
        poll.DisplayName.Should().Be("Ann");
        poll.ExternalUserId.Should().Be("u-9");
        var extra = JsonDocument.Parse(poll.ExtraJson!);
        extra.RootElement.GetProperty("uid").GetString().Should().Be("u-9");
        extra.RootElement.GetProperty("name").GetString().Should().Be("Ann");
        extra.RootElement.GetProperty("email").GetString().Should().Be("a@x.com");
        extra.RootElement.GetProperty("machineId").GetString().Should().NotBeEmpty();
        extra.RootElement.GetProperty("tokenSource").GetString().Should().Be("job");
        extra.RootElement.GetProperty("deviceToken").GetString().Should().Be("dt-1");
    }
}

file static class RepeatExtension
{
    public static string Repeat(this string value, int count) => string.Concat(Enumerable.Repeat(value, count));
}
