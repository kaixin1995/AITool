using AITool.Infrastructure.Proxy;
using System.Net;
using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using AITool.Admin.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.Admin.IntegrationTests.Developer;

/// <summary>
/// 离线协议诊断接口集成测试，确认诊断只调用内存桥接逻辑，不进入真实代理链路。
/// </summary>
public sealed class ProtocolDiagnosticsApiTests
{
    private const string Endpoint = "/api/admin/developer/invocations/protocol-diagnostics";

    /// <summary>
    /// 开关开启时，合法请求诊断应返回转换结果，并且不产生转发、追踪或业务数据库写入。
    /// </summary>
    [Fact]
    public async Task Valid_request_diagnostic_runs_without_forwarding_or_business_writes()
    {
        await using var factory = new ProtocolDiagnosticsWebApplicationFactory(developerFeaturesEnabled: true);
        using var client = factory.CreateClient();
        var before = factory.ReadSnapshot();

        using var content = JsonContent(new
        {
            direction = "request",
            sourceProtocol = "OpenAI",
            targetProtocol = "Responses",
            streaming = false,
            modelName = "deepseek-v4-flash",
            payload = "{\"model\":\"deepseek-v4-flash\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}"
        });

        var response = await client.PostAsync(Endpoint, content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("data").GetProperty("convertedPayload").GetString()
            .Should().Contain("\"store\":false");

        factory.ForwardService.ForwardAsyncCalls.Should().Be(0);
        factory.ForwardService.ForwardStreamingAsyncCalls.Should().Be(0);
        factory.ReadSnapshot().Should().BeEquivalentTo(before);
    }

    /// <summary>
    /// 开发者功能关闭时，诊断接口应隐藏且不能通过请求触发设置记录创建。
    /// </summary>
    [Fact]
    public async Task Diagnostic_returns_not_found_when_developer_features_are_disabled()
    {
        await using var factory = new ProtocolDiagnosticsWebApplicationFactory(developerFeaturesEnabled: false);
        using var client = factory.CreateClient();
        var before = factory.ReadSnapshot();

        using var content = JsonContent(CreateValidRequest());
        var response = await client.PostAsync(Endpoint, content);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.ReadSnapshot().Should().BeEquivalentTo(before);
        factory.ForwardService.ForwardAsyncCalls.Should().Be(0);
        factory.ForwardService.ForwardStreamingAsyncCalls.Should().Be(0);
    }

    /// <summary>
    /// 系统设置记录不存在时，接口应按关闭处理，而不是通过 GetOrCreateAsync 创建记录。
    /// </summary>
    [Fact]
    public async Task Missing_runtime_settings_returns_not_found_without_creating_settings()
    {
        await using var factory = new ProtocolDiagnosticsWebApplicationFactory(developerFeaturesEnabled: true);
        using var client = factory.CreateClient();
        factory.DeleteRuntimeSettings();
        factory.ReadSnapshot().RuntimeSettingsCount.Should().Be(0);

        using var content = JsonContent(CreateValidRequest());
        var response = await client.PostAsync(Endpoint, content);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        factory.ReadSnapshot().RuntimeSettingsCount.Should().Be(0);
        factory.ForwardService.ForwardAsyncCalls.Should().Be(0);
        factory.ForwardService.ForwardStreamingAsyncCalls.Should().Be(0);
    }

    /// <summary>
    /// 非法协议名称应返回受控的 400，而不是进入未处理异常路径。
    /// </summary>
    [Fact]
    public async Task Invalid_protocol_returns_controlled_bad_request()
    {
        await using var factory = new ProtocolDiagnosticsWebApplicationFactory(developerFeaturesEnabled: true);
        using var client = factory.CreateClient();

        var request = CreateValidRequest();
        request["targetProtocol"] = "Unknown";
        using var content = JsonContent(request);

        var response = await client.PostAsync(Endpoint, content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("invalid_protocol");
        factory.ForwardService.ForwardAsyncCalls.Should().Be(0);
        factory.ForwardService.ForwardStreamingAsyncCalls.Should().Be(0);
    }

    /// <summary>
    /// 流式输入不能把完整 SSE block 误传给只接受原始 JSON chunk 的转换器。
    /// </summary>
    [Fact]
    public async Task Invalid_stream_framing_returns_controlled_bad_request()
    {
        await using var factory = new ProtocolDiagnosticsWebApplicationFactory(developerFeaturesEnabled: true);
        using var client = factory.CreateClient();

        var request = CreateValidRequest();
        request["streaming"] = true;
        request["payload"] = "data: {\"id\":\"chatcmpl-test\",\"model\":\"deepseek-v4-flash\",\"choices\":[]}";
        using var content = JsonContent(request);

        var response = await client.PostAsync(Endpoint, content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("invalid_stream_payload");
        factory.ForwardService.ForwardAsyncCalls.Should().Be(0);
        factory.ForwardService.ForwardStreamingAsyncCalls.Should().Be(0);
    }

    /// <summary>
    /// 模型名不再受限：诊断台应支持任意模型名自由测试协议转换。
    /// </summary>
    [Fact]
    public async Task Diagnostic_accepts_any_model_name()
    {
        await using var factory = new ProtocolDiagnosticsWebApplicationFactory(developerFeaturesEnabled: true);
        using var client = factory.CreateClient();

        var request = CreateValidRequest();
        request["modelName"] = "gpt-4.1-custom";
        request["payload"] = "{\"model\":\"gpt-4.1-custom\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";
        using var content = JsonContent(request);

        var response = await client.PostAsync(Endpoint, content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        factory.ForwardService.ForwardAsyncCalls.Should().Be(0);
    }

    /// <summary>
    /// 诊断结果应附带转换路径、输入摘要和字段映射表，便于定位转换关系。
    /// </summary>
    [Fact]
    public async Task Diagnostic_result_includes_path_summary_and_field_mappings()
    {
        await using var factory = new ProtocolDiagnosticsWebApplicationFactory(developerFeaturesEnabled: true);
        using var client = factory.CreateClient();

        using var content = JsonContent(CreateValidRequest());
        var response = await client.PostAsync(Endpoint, content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("conversionPath").GetString().Should().Be("PrepareRequestBody");
        data.GetProperty("inputSummary").GetProperty("模型").GetString().Should().Be("deepseek-v4-flash");
        data.GetProperty("fieldMappings").GetArrayLength().Should().BeGreaterThan(0);
        data.GetProperty("fieldMappings")[0].GetProperty("source").GetString().Should().NotBeNullOrWhiteSpace();
        data.GetProperty("conversionFailed").GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// 跨协议组合应返回 bridge 模式链路：5 个环节 + 双向转换函数名；流式时附带事件映射。
    /// </summary>
    [Fact]
    public async Task Diagnostic_chain_shows_bridge_mode_stages_and_stream_event_mappings()
    {
        await using var factory = new ProtocolDiagnosticsWebApplicationFactory(developerFeaturesEnabled: true);
        using var client = factory.CreateClient();

        var request = CreateValidRequest();
        request["sourceProtocol"] = "OpenAI";
        request["targetProtocol"] = "Anthropic";
        request["streaming"] = true;
        request["payload"] = "{\"choices\":[{\"delta\":{\"content\":\"hi\"},\"index\":0}]}";
        using var content = JsonContent(request);

        var response = await client.PostAsync(Endpoint, content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        var chain = data.GetProperty("chain");
        chain.GetProperty("mode").GetString().Should().Be("bridge");
        chain.GetProperty("stages").GetArrayLength().Should().Be(5);
        chain.GetProperty("stages")[0].GetProperty("kind").GetString().Should().Be("client-request");
        chain.GetProperty("stages")[1].GetProperty("function").GetString()
            .Should().Contain("BuildAnthropicRequestFromOpenAi");
        chain.GetProperty("stages")[3].GetProperty("function").GetString()
            .Should().Contain("BuildOpenAiStreamingResponseFromAnthropic");
        var eventMappings = chain.GetProperty("eventMappings");
        eventMappings.GetArrayLength().Should().BeGreaterThan(0);
        eventMappings[0].GetProperty("sourceEvent").GetString().Should().Be("message_start");
        factory.ForwardService.ForwardAsyncCalls.Should().Be(0);
    }

    /// <summary>
    /// 同协议组合应返回 direct 模式链路，转换环节标注透传且无事件映射。
    /// </summary>
    [Fact]
    public async Task Diagnostic_chain_shows_direct_mode_for_same_protocol()
    {
        await using var factory = new ProtocolDiagnosticsWebApplicationFactory(developerFeaturesEnabled: true);
        using var client = factory.CreateClient();

        var request = CreateValidRequest();
        request["sourceProtocol"] = "OpenAI";
        request["targetProtocol"] = "OpenAI";
        using var content = JsonContent(request);

        var response = await client.PostAsync(Endpoint, content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var chain = document.RootElement.GetProperty("data").GetProperty("chain");
        chain.GetProperty("mode").GetString().Should().Be("direct");
        chain.GetProperty("stages")[1].GetProperty("isBridge").GetBoolean().Should().BeFalse();
        chain.GetProperty("stages")[1].GetProperty("note").GetString().Should().Contain("透传");
        chain.GetProperty("eventMappings").GetArrayLength().Should().Be(0);
    }

    /// <summary>
    /// 响应方向 上游 Anthropic 流式 → 客户端 OpenAI：接受完整 SSE 帧并整体转换为 OpenAI 事件流。
    /// </summary>
    [Fact]
    public async Task Diagnostic_stream_response_anthropic_to_openai_accepts_full_sse()
    {
        await using var factory = new ProtocolDiagnosticsWebApplicationFactory(developerFeaturesEnabled: true);
        using var client = factory.CreateClient();

        var request = CreateValidRequest();
        request["direction"] = "response";
        request["sourceProtocol"] = "Anthropic";
        request["targetProtocol"] = "OpenAI";
        request["streaming"] = true;
        request["payload"] =
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n" +
            "event: message_stop\n" +
            "data: {\"type\":\"message_stop\"}\n\n";
        using var content = JsonContent(request);

        var response = await client.PostAsync(Endpoint, content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("conversionFailed").GetBoolean().Should().BeFalse();
        var converted = data.GetProperty("convertedPayload").GetString();
        converted.Should().Contain("data: ");
        converted.Should().Contain("content");
        factory.ForwardService.ForwardAsyncCalls.Should().Be(0);
    }

    /// <summary>
    /// 响应方向 上游 Responses 流式 → 客户端 Anthropic：两级转换 Responses→Chat→Anthropic。
    /// </summary>
    [Fact]
    public async Task Diagnostic_stream_response_responses_to_anthropic_two_stage_conversion()
    {
        await using var factory = new ProtocolDiagnosticsWebApplicationFactory(developerFeaturesEnabled: true);
        using var client = factory.CreateClient();

        var request = CreateValidRequest();
        request["direction"] = "response";
        request["sourceProtocol"] = "Responses";
        request["targetProtocol"] = "Anthropic";
        request["streaming"] = true;
        request["payload"] =
            "event: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\"item_id\":\"i1\",\"output_index\":0,\"delta\":\"hi\"}\n\n" +
            "event: response.completed\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"r1\",\"output\":[],\"status\":\"completed\"}}\n\n";
        using var content = JsonContent(request);

        var response = await client.PostAsync(Endpoint, content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("conversionFailed").GetBoolean().Should().BeFalse();
        data.GetProperty("convertedPayload").GetString().Should().Contain("event: message_start");
        factory.ForwardService.ForwardAsyncCalls.Should().Be(0);
    }

    /// <summary>
    /// 同协议流式：事件原样透传，不解析内容。
    /// </summary>
    [Fact]
    public async Task Diagnostic_stream_same_protocol_passthrough()
    {
        await using var factory = new ProtocolDiagnosticsWebApplicationFactory(developerFeaturesEnabled: true);
        using var client = factory.CreateClient();

        var request = CreateValidRequest();
        request["sourceProtocol"] = "OpenAI";
        request["targetProtocol"] = "OpenAI";
        request["streaming"] = true;
        request["payload"] = "data: {\"id\":\"x\",\"choices\":[{\"delta\":{\"content\":\"hi\"},\"index\":0}]}\n\ndata: [DONE]\n\n";
        using var content = JsonContent(request);

        var response = await client.PostAsync(Endpoint, content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("conversionFailed").GetBoolean().Should().BeFalse();
        data.GetProperty("convertedPayload").GetString().Should().Contain("data: ");
        data.GetProperty("chain").GetProperty("mode").GetString().Should().Be("direct");
        factory.ForwardService.ForwardAsyncCalls.Should().Be(0);
    }

    /// <summary>
    /// 流式输入是单个事件片段，不应触发整体字段缺失误报（如单事件无 usage/content/messages）。
    /// </summary>
    [Fact]
    public async Task Diagnostic_streaming_skips_whole_body_field_checks()
    {
        await using var factory = new ProtocolDiagnosticsWebApplicationFactory(developerFeaturesEnabled: true);
        using var client = factory.CreateClient();

        var request = CreateValidRequest();
        request["sourceProtocol"] = "OpenAI";
        request["targetProtocol"] = "Anthropic";
        request["streaming"] = true;
        request["payload"] = "{\"choices\":[{\"delta\":{\"content\":\"hi\"},\"index\":0}]}";
        using var content = JsonContent(request);

        var response = await client.PostAsync(Endpoint, content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        // 单事件无 messages/usage 是正常的，不应出现在缺失提醒里。
        data.GetProperty("missingFields").GetArrayLength().Should().Be(0);
    }

    /// <summary>
    /// 试运行规则：请求方向转换完成后应用兼容规则（strip），并标记 rulesApplied。
    /// </summary>
    [Fact]
    public async Task Diagnostic_applies_trial_rules_after_conversion()
    {
        await using var factory = new ProtocolDiagnosticsWebApplicationFactory(developerFeaturesEnabled: true);
        using var client = factory.CreateClient();

        var request = CreateValidRequest();
        request["payload"] = "{\"model\":\"deepseek-v4-flash\",\"metadata\":{\"tag\":\"x\"},\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";
        request["rules"] = new[]
        {
            new { op = "strip", target = "metadata", scope = "bridge" },
            new { op = "strip", target = "nope", scope = "passthrough" }
        };
        using var content = JsonContent(request);

        var response = await client.PostAsync(Endpoint, content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("rulesApplied").GetBoolean().Should().BeTrue();
        var converted = data.GetProperty("convertedPayload").GetString();
        // strip(metadata) 已生效（跨协议兼容路径，scope=bridge 规则生效）；
        // 转换本身正常（messages → input），passthrough 规则未误伤转换结果。
        converted.Should().NotContain("metadata");
        converted.Should().Contain("\"input\"");
        factory.ForwardService.ForwardAsyncCalls.Should().Be(0);
    }

    /// <summary>
    /// 兼容规则是请求体规则：响应方向即使传了 rules 也不应应用。
    /// </summary>
    [Fact]
    public async Task Diagnostic_trial_rules_ignored_for_response_direction()
    {
        await using var factory = new ProtocolDiagnosticsWebApplicationFactory(developerFeaturesEnabled: true);
        using var client = factory.CreateClient();

        var request = CreateValidRequest();
        request["direction"] = "response";
        request["sourceProtocol"] = "Responses";
        request["targetProtocol"] = "OpenAI";
        request["streaming"] = false;
        request["payload"] = "{\"id\":\"resp-1\",\"output\":[{\"type\":\"message\",\"id\":\"m1\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"hi\",\"annotations\":[]}]}],\"status\":\"completed\"}";
        request["rules"] = new[]
        {
            new { op = "strip", target = "id", scope = "all" }
        };
        using var content = JsonContent(request);

        var response = await client.PostAsync(Endpoint, content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("rulesApplied").GetBoolean().Should().BeFalse();
        // 若规则被误应用，id 会被 strip 掉；保留说明响应方向确实忽略规则。
        data.GetProperty("convertedPayload").GetString().Should().Contain("resp-1");
        factory.ForwardService.ForwardAsyncCalls.Should().Be(0);
    }

    /// <summary>
    /// 试运行规则按透传/兼容路径筛选 scope：同协议透传时仅 passthrough/all 规则生效。
    /// </summary>
    [Fact]
    public async Task Diagnostic_trial_rules_respect_passthrough_scope()
    {
        await using var factory = new ProtocolDiagnosticsWebApplicationFactory(developerFeaturesEnabled: true);
        using var client = factory.CreateClient();

        var request = CreateValidRequest();
        request["sourceProtocol"] = "OpenAI";
        request["targetProtocol"] = "OpenAI";
        request["payload"] = "{\"model\":\"deepseek-v4-flash\",\"metadata\":{\"tag\":\"x\"},\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";
        request["rules"] = new[]
        {
            new { op = "strip", target = "metadata", scope = "passthrough" },
            new { op = "strip", target = "messages", scope = "bridge" }
        };
        using var content = JsonContent(request);

        var response = await client.PostAsync(Endpoint, content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        var converted = data.GetProperty("convertedPayload").GetString();
        converted.Should().NotContain("metadata");
        converted.Should().Contain("\"messages\"");
        factory.ForwardService.ForwardAsyncCalls.Should().Be(0);
    }

    /// <summary>
    /// 转换结果为空时应在 200 响应中返回 conversionFailed + failureReason，而不是笼统 400。
    /// </summary>
    [Fact]
    public async Task Diagnostic_empty_conversion_returns_failure_reason_in_ok_response()
    {
        await using var factory = new ProtocolDiagnosticsWebApplicationFactory(developerFeaturesEnabled: true);
        using var client = factory.CreateClient();

        var request = CreateValidRequest();
        request["direction"] = "response";
        request["sourceProtocol"] = "Responses";
        request["targetProtocol"] = "OpenAI";
        request["streaming"] = false;
        request["modelName"] = "deepseek-v4-flash";
        request["payload"] = "{\"id\":\"resp-empty\",\"output\":[],\"status\":\"completed\"}";
        using var content = JsonContent(request);

        var response = await client.PostAsync(Endpoint, content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var data = document.RootElement.GetProperty("data");
        data.GetProperty("conversionFailed").GetBoolean().Should().BeTrue();
        data.GetProperty("failureReason").GetString().Should().NotBeNullOrWhiteSpace();
        data.GetProperty("missingFields").GetArrayLength().Should().BeGreaterThan(0);
        factory.ForwardService.ForwardAsyncCalls.Should().Be(0);
        factory.ForwardService.ForwardStreamingAsyncCalls.Should().Be(0);
    }

    /// <summary>
    /// 首版未公开逐事件状态桥接的方向应明确返回不支持，而不是尝试真实调用或伪造转换结果。
    /// </summary>
    [Fact]
    public async Task Unsupported_stream_direction_returns_controlled_bad_request()
    {
        await using var factory = new ProtocolDiagnosticsWebApplicationFactory(developerFeaturesEnabled: true);
        using var client = factory.CreateClient();

        var request = CreateValidRequest();
        request["streaming"] = true;
        request["sourceProtocol"] = "Anthropic";
        request["targetProtocol"] = "OpenAI";
        request["payload"] = "event: content_block_delta\ndata: {\"type\":\"content_block_delta\"}\n\n";
        using var content = JsonContent(request);

        var response = await client.PostAsync(Endpoint, content);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("unsupported_stream_direction");
        factory.ForwardService.ForwardAsyncCalls.Should().Be(0);
        factory.ForwardService.ForwardStreamingAsyncCalls.Should().Be(0);
    }

    private static Dictionary<string, object> CreateValidRequest()
    {
        return new Dictionary<string, object>
        {
            ["direction"] = "request",
            ["sourceProtocol"] = "OpenAI",
            ["targetProtocol"] = "Responses",
            ["streaming"] = false,
            ["modelName"] = "deepseek-v4-flash",
            ["payload"] = "{\"model\":\"deepseek-v4-flash\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}"
        };
    }

    private static StringContent JsonContent(object value)
        => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}

/// <summary>
/// 为协议诊断测试提供隔离数据库和禁止真实转发的测试宿主。
/// </summary>
internal sealed class ProtocolDiagnosticsWebApplicationFactory : WebApplicationFactory<AITool.Admin.AdminProgramMarker>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-protocol-diagnostics-{Guid.NewGuid():N}.db");
    private readonly bool _developerFeaturesEnabled;
    private readonly ProtocolDiagnosticsFakeProxyForwardService _forwardService = new();

    public ProtocolDiagnosticsWebApplicationFactory(bool developerFeaturesEnabled)
    {
        _developerFeaturesEnabled = developerFeaturesEnabled;
    }

    public ProtocolDiagnosticsFakeProxyForwardService ForwardService => _forwardService;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Server:Port"] = "0"
            });
        });
        builder.ConfigureServices(services =>
        {
            IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);
            services.RemoveAll<IProxyForwardService>();
            services.AddSingleton<IProxyForwardService>(_forwardService);
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        Seed();
    }

    public ProtocolDiagnosticsDbSnapshot ReadSnapshot()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var traceStore = scope.ServiceProvider.GetRequiredService<DeveloperInvocationTraceStore>();
        return new ProtocolDiagnosticsDbSnapshot(
            db.SystemRuntimeSettings.Count(),
            db.Sites.Count(),
            db.SiteKeys.Count(),
            db.ProxyRouteEntries.Count(),
            db.ProxyRouteRules.Count(),
            db.ModelLibraryItems.Count(),
            db.SiteModelMappings.Count(),
            db.ProxyUsageLogs.Count(),
            traceStore.List().Count);
    }

    public void DeleteRuntimeSettings()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Client.Deleteable<SystemRuntimeSettings>().Where(x => x.Id == 1).ExecuteCommand();
    }

    private void Seed()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SqlSugarSetup.InitializeDatabase(db.Client);
        db.Client.Deleteable<SystemRuntimeSettings>().Where(x => x.Id == 1).ExecuteCommand();
        db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            DeveloperFeaturesEnabled = _developerFeaturesEnabled
        });
    }
}

/// <summary>
/// 记录所有转发调用；诊断接口若误入真实转发路径，测试会通过计数暴露问题。
/// </summary>
internal sealed class ProtocolDiagnosticsFakeProxyForwardService : IProxyForwardService
{
    public int ForwardAsyncCalls { get; private set; }
    public int ForwardStreamingAsyncCalls { get; private set; }

    public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
    {
        ForwardAsyncCalls++;
        throw new InvalidOperationException("协议诊断不应调用 ForwardAsync");
    }

    public Task<ProxyForwardResult> ForwardStreamingAsync(
        ProxyForwardRequest request,
        Func<string, CancellationToken, Task> onSseDataAsync,
        CancellationToken cancellationToken = default)
    {
        ForwardStreamingAsyncCalls++;
        throw new InvalidOperationException("协议诊断不应调用 ForwardStreamingAsync");
    }
}

internal sealed record ProtocolDiagnosticsDbSnapshot(
    int RuntimeSettingsCount,
    int SitesCount,
    int SiteKeysCount,
    int RouteEntriesCount,
    int RouteRulesCount,
    int ModelLibraryItemsCount,
    int SiteModelMappingsCount,
    int UsageLogsCount,
    int TraceCount);
