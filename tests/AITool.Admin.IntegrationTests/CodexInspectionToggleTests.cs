using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Admin.IntegrationTests;

/// <summary>
/// Codex 巡检开关集成测试，验证关闭自动巡检后页签/页面/API 均不可用。
/// </summary>
public sealed class CodexInspectionToggleTests
{
    [Fact]
    public async Task Get_codex_page_hides_inspection_tab_when_inspection_is_disabled()
    {
        await using var factory = new CodexInspectionWebApplicationFactory(codexFeaturesEnabled: true, codexInspectionEnabled: false);
        using var client = await factory.CreateAuthenticatedClientAsync("/Admin/Codex");

        var response = await client.GetAsync("/Admin/Codex");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("账号额度");
        html.Should().NotContain("id=\"inspection-tab\"");
        html.Should().NotContain("/Admin/Codex/Inspection");
    }

    [Fact]
    public async Task Get_codex_page_shows_inspection_tab_when_inspection_is_enabled()
    {
        await using var factory = new CodexInspectionWebApplicationFactory(codexFeaturesEnabled: true, codexInspectionEnabled: true);
        using var client = await factory.CreateAuthenticatedClientAsync("/Admin/Codex");

        var response = await client.GetAsync("/Admin/Codex");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        html.Should().Contain("id=\"inspection-tab\"");
        html.Should().Contain("/Admin/Codex/Inspection");
    }

    [Fact]
    public async Task Get_inspection_page_returns_not_found_when_inspection_is_disabled()
    {
        await using var factory = new CodexInspectionWebApplicationFactory(codexFeaturesEnabled: true, codexInspectionEnabled: false);
        using var client = await factory.CreateAuthenticatedClientAsync("/Admin/Codex/Inspection");

        var response = await client.GetAsync("/Admin/Codex/Inspection");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("GET", "/api/admin/codex/inspection/status")]
    [InlineData("GET", "/api/admin/codex/inspection/last-run")]
    [InlineData("GET", "/api/admin/codex/inspection/logs")]
    [InlineData("POST", "/api/admin/codex/inspection/run?force=false")]
    public async Task Inspection_api_returns_not_found_when_inspection_is_disabled(string method, string url)
    {
        await using var factory = new CodexInspectionWebApplicationFactory(codexFeaturesEnabled: true, codexInspectionEnabled: false);
        using var client = await factory.CreateAuthenticatedClientAsync(url);

        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Inspection_status_api_returns_ok_when_inspection_is_enabled()
    {
        await using var factory = new CodexInspectionWebApplicationFactory(codexFeaturesEnabled: true, codexInspectionEnabled: true);
        using var client = await factory.CreateAuthenticatedClientAsync("/api/admin/codex/inspection/status");

        var response = await client.GetAsync("/api/admin/codex/inspection/status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

internal sealed class CodexInspectionWebApplicationFactory : WebApplicationFactory<AITool.Admin.AdminProgramMarker>
{
    private const string AdminPassword = "test-admin-password";
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-codex-inspection-{Guid.NewGuid():N}.db");
    private readonly bool _codexFeaturesEnabled;
    private readonly bool _codexInspectionEnabled;

    public CodexInspectionWebApplicationFactory(bool codexFeaturesEnabled, bool codexInspectionEnabled)
    {
        _codexFeaturesEnabled = codexFeaturesEnabled;
        _codexInspectionEnabled = codexInspectionEnabled;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdminAuth:PasswordHash"] = ComputeMd5(AdminPassword)
            });
        });
        builder.ConfigureServices(services =>
        {
            IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        EnsureDatabaseAsync().GetAwaiter().GetResult();
    }

    public async Task<HttpClient> CreateAuthenticatedClientAsync(string returnUrl)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var loginPage = await client.GetAsync($"/Login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        loginPage.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await loginPage.Content.ReadAsStringAsync();
        var marker = "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        start += marker.Length;
        var end = html.IndexOf('"', start);
        end.Should().BeGreaterThan(start);
        var token = html[start..end];

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Password"] = AdminPassword,
            ["__RequestVerificationToken"] = token,
            ["returnUrl"] = returnUrl
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/Login?handler=Login&returnUrl={Uri.EscapeDataString(returnUrl)}")
        {
            Content = form
        };
        var loginResponse = await client.SendAsync(request);
        loginResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

        return client;
    }

    private async Task EnsureDatabaseAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SqlSugarSetup.InitializeDatabase(db.Client);

        db.Client.Deleteable<SystemRuntimeSettings>().Where(x => x.Id == 1).ExecuteCommand();

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
            CodexFeaturesEnabled = _codexFeaturesEnabled,
            CodexInspectionEnabled = _codexInspectionEnabled,
            CodexInspectionIntervalMinutes = 30,
            CodexQuotaMaxCacheHours = 6,
            CodexAutoDisableThresholdPercent = 95
        });
        await db.SaveChangesAsync();
    }

    private static string ComputeMd5(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hashBytes = MD5.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
