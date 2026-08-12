using System.Net;
using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.Developer;

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
internal sealed class ProtocolDiagnosticsWebApplicationFactory : WebApplicationFactory<Program>
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
