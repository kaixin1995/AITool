using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.ClientSimulator;

/// <summary>
/// 客户端模拟页测试，验证旧入口会重定向到开发者调用页面，且目标页会区分 OpenAI 与 Anthropic 的可用模型能力。
/// </summary>
public sealed class ClientSimulatorPageTests
{
    /// <summary>
    /// 验证页面会按协议展示模型能力，并设置对应的默认模型。
    /// </summary>
    [Fact]
    public async Task Get_page_renders_protocol_specific_model_capabilities_and_defaults()
    {
        await using var factory = new ClientSimulatorWebApplicationFactory();
        using var client = factory.CreateClient();

        // 旧入口重定向后应成功渲染开发者调用页面中的模拟器区域
        var response = await client.GetAsync("/Admin/ClientSimulator");
        var html = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        html.Should().Contain("\"ModelName\":\"gpt-only\"");
        html.Should().Contain("\"SupportsOpenAi\":true");
        html.Should().Contain("\"CanUseAnthropic\":true");
        html.Should().Contain("\"ModelName\":\"claude-only\"");
        html.Should().Contain("\"SupportsAnthropic\":true");
        html.Should().Contain("var defaultAnthropicModel = \"claude-only\"");
    }

    /// <summary>
    /// 验证缺少 Anthropic 路由时，页面会回退到可桥接的 OpenAI 模型作为默认值。
    /// </summary>
    [Fact]
    public async Task Get_page_falls_back_anthropic_default_to_openai_only_model_via_bridge()
    {
        await using var factory = new ClientSimulatorWebApplicationFactory(includeAnthropicRoute: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/ClientSimulator");
        var html = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        html.Should().Contain("\"ModelName\":\"gpt-only\"");
        html.Should().Contain("\"SupportsOpenAi\":true");
        html.Should().Contain("\"CanUseAnthropic\":true");
        html.Should().Contain("var defaultOpenAiModel = \"gpt-only\"");
        html.Should().Contain("var defaultAnthropicModel = \"gpt-only\"");
    }
}

/// <summary>
/// 用于构建 ClientSimulatorWebApplicationFactory 对应的测试宿主，并准备隔离的测试数据。
/// </summary>
internal sealed class ClientSimulatorWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// 保存当前测试使用的临时数据库路径。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-client-simulator-{Guid.NewGuid():N}.db");
    /// <summary>
    /// 标记当前测试是否需要写入 Anthropic 路由。
    /// </summary>
    private readonly bool _includeAnthropicRoute;

    /// <summary>
    /// 创建客户端模拟页面测试宿主，并记录当前是否包含 Anthropic 路由。
    /// </summary>
    public ClientSimulatorWebApplicationFactory(bool includeAnthropicRoute = true)
    {
        _includeAnthropicRoute = includeAnthropicRoute;
    }

    /// <summary>
    /// 配置客户端模拟页面测试所需的数据库。
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);
        });
    }

    /// <summary>
    /// 创建客户端后初始化当前测试场景的数据。
    /// </summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        SeedAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 准备当前测试场景所需的数据。
    /// </summary>
    private async Task SeedAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SqlSugarSetup.InitializeDatabase(db.Client);

        var openAiSiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var anthropicSiteId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        db.Sites.AddRange(
            new Site
            {
                Id = openAiSiteId,
                Name = "OpenAI Site",
                BaseUrl = "https://openai.example.com",
                ApiKey = "openai-key",
                ProtocolType = "OpenAI",
                SupportsOpenAi = true,
                SupportsAnthropic = false,
                IsEnabled = true
            },
            new Site
            {
                Id = anthropicSiteId,
                Name = "Anthropic Site",
                BaseUrl = "https://anthropic.example.com",
                ApiKey = "anthropic-key",
                ProtocolType = "Anthropic",
                SupportsOpenAi = false,
                SupportsAnthropic = true,
                IsEnabled = true
            });

        db.ProxyRouteRules.Add(new ProxyRouteRule
        {
            ExternalModelName = "gpt-only",
            UpstreamModelName = "gpt-5.5",
            SiteId = openAiSiteId,
            SiteModelName = "gpt-5.5-real",
            Priority = 0,
            ModelPriority = 0,
            InstancePriority = 0,
            IsEnabled = true
        });

        if (_includeAnthropicRoute)
        {
            db.ProxyRouteRules.Add(new ProxyRouteRule
            {
                ExternalModelName = "claude-only",
                UpstreamModelName = "claude-3-7-sonnet",
                SiteId = anthropicSiteId,
                SiteModelName = "claude-3-7-sonnet-real",
                Priority = 1,
                ModelPriority = 0,
                InstancePriority = 0,
                IsEnabled = true
            });
        }

        db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            ProxyRequestTimeoutSeconds = 30,
            ProxyRetryCount = 1,
            DetectionRequestTimeoutSeconds = 60,
            DetectionRetryCount = 0,
            DetectionConcurrency = 1,
            CircuitBreakerFailureThreshold = 5,
            CircuitBreakerRecoveryMinutes = 2,
            UsageLogRetentionDays = 7,
            UsageLogAutoCleanupEnabled = true,
            DeveloperFeaturesEnabled = true
        });

        await db.SaveChangesAsync();
    }
}
