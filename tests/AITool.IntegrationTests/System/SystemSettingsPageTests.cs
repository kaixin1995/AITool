using System.Net;
using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AITool.IntegrationTests.System;

// 系统设置页面集成测试，验证页面可访问并展示关键字段
public sealed class SystemSettingsPageTests
{
    [Fact]
    public async Task Get_settings_page_contains_runtime_setting_fields()
    {
        await using var factory = new SystemSettingsWebApplicationFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/System/Settings");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("代理超时时间（秒）");
        html.Should().Contain("代理重试次数");
        html.Should().Contain("UsageLogs 保留天数");
        html.Should().Contain("启用开发者功能");
    }

    [Fact]
    public async Task Get_layout_hides_developer_invocation_navigation_when_feature_is_disabled()
    {
        await using var factory = new SystemSettingsWebApplicationFactory(false);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/System/Settings");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().NotContain("/Admin/Developer/Invocations");
    }

    [Fact]
    public async Task Get_layout_shows_developer_invocation_navigation_when_feature_is_enabled()
    {
        await using var factory = new SystemSettingsWebApplicationFactory(true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/Admin/System/Settings");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("/Admin/Developer/Invocations");
        html.Should().Contain("调用调试");
    }
}

internal sealed class SystemSettingsWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-system-settings-{Guid.NewGuid():N}.db");
    private readonly bool _developerFeaturesEnabled;

    public SystemSettingsWebApplicationFactory(bool developerFeaturesEnabled = false)
    {
        _developerFeaturesEnabled = developerFeaturesEnabled;
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
        EnsureDatabaseAsync().GetAwaiter().GetResult();
    }

    private async Task EnsureDatabaseAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            ProxyRequestTimeoutSeconds = 60,
            ProxyRetryCount = 1,
            DetectionRequestTimeoutSeconds = 60,
            DetectionRetryCount = 0,
            DetectionConcurrency = 1,
            CircuitBreakerFailureThreshold = 5,
            CircuitBreakerRecoveryMinutes = 2,
            UsageLogRetentionDays = 7,
            UsageLogAutoCleanupEnabled = true,
            DeveloperFeaturesEnabled = _developerFeaturesEnabled
        });
        await db.SaveChangesAsync();
    }
}
