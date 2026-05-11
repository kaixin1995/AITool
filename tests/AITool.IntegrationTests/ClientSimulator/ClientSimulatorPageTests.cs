using AITool.Domain.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.ClientSimulator;

// 客户端模拟页测试，验证页面会区分 OpenAI 与 Anthropic 的可用模型能力。
public sealed class ClientSimulatorPageTests
{
    [Fact]
    public async Task Get_page_renders_protocol_specific_model_capabilities_and_defaults()
    {
        await using var factory = new ClientSimulatorWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/ClientSimulator");
        var html = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        html.Should().Contain("gpt-only (1 条路由, OpenAI)");
        html.Should().Contain("claude-only (1 条路由, Anthropic)");
        html.Should().Contain("\"ModelName\":\"claude-only\"");
        html.Should().Contain("\"SupportsAnthropic\":true");
        html.Should().Contain("var defaultAnthropicModel = \"claude-only\"");
    }

    [Fact]
    public async Task Get_page_does_not_fallback_anthropic_default_to_openai_only_model()
    {
        await using var factory = new ClientSimulatorWebApplicationFactory(includeAnthropicRoute: false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/ClientSimulator");
        var html = await response.Content.ReadAsStringAsync();

        response.EnsureSuccessStatusCode();
        html.Should().Contain("var defaultOpenAiModel = \"gpt-only\"");
        html.Should().Contain("var defaultAnthropicModel = \"\"");
    }
}

internal sealed class ClientSimulatorWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-client-simulator-{Guid.NewGuid():N}.db");
    private readonly bool _includeAnthropicRoute;

    public ClientSimulatorWebApplicationFactory(bool includeAnthropicRoute = true)
    {
        _includeAnthropicRoute = includeAnthropicRoute;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_databasePath}"));
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        SeedAsync().GetAwaiter().GetResult();
    }

    private async Task SeedAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

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

        await db.SaveChangesAsync();
    }
}
