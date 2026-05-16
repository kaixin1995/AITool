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

/// <summary>
/// 系统设置页面集成测试，验证页面可访问并展示关键字段
/// </summary>
public sealed class SystemSettingsPageTests
{
    /// <summary>
    /// 验证系统设置页面会展示运行时配置字段。
    /// </summary>
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

    /// <summary>
    /// 验证关闭开发者功能后，布局中不会显示调用调试导航。
    /// </summary>
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

    /// <summary>
    /// 验证启用开发者功能后，布局中会显示调用调试导航。
    /// </summary>
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

/// <summary>
/// 用于构建 SystemSettingsWebApplicationFactory 对应的测试宿主，并准备隔离的测试数据。
/// </summary>
internal sealed class SystemSettingsWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// 保存当前测试使用的临时数据库路径。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-system-settings-{Guid.NewGuid():N}.db");
    /// <summary>
    /// 标记当前测试是否启用开发者功能。
    /// </summary>
    private readonly bool _developerFeaturesEnabled;

    /// <summary>
    /// 创建系统设置页面测试宿主，并记录开发者功能开关。
    /// </summary>
    public SystemSettingsWebApplicationFactory(bool developerFeaturesEnabled = false)
    {
        _developerFeaturesEnabled = developerFeaturesEnabled;
    }

    /// <summary>
    /// 配置系统设置页面测试所需的数据库。
    /// </summary>
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

    /// <summary>
    /// 创建客户端后初始化当前测试场景的数据。
    /// </summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        EnsureDatabaseAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 准备当前测试场景所需的数据。
    /// </summary>
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
