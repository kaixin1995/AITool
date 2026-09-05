using System.Net;
using System.Text;
using System.Text.Json;
using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.IntegrationTests.Developer;

/// <summary>
/// 调试工具各功能页分开关的集成测试：关闭某个分开关后对应 API 应隐藏（404），
/// init 与登录态接口应正确报告各页可用性；全开关默认开启时行为与旧版一致。
/// </summary>
public sealed class DeveloperTabSwitchesApiTests
{
    private const string InitEndpoint = "/api/admin/developer/invocations/init";
    private const string ListEndpoint = "/api/admin/developer/invocations/list";
    private const string ProtocolDiagEndpoint = "/api/admin/developer/invocations/protocol-diagnostics";
    private const string DumpConfigEndpoint = "/api/admin/developer/invocations/diagnostic-config";
    private const string SqlMigrationsEndpoint = "/api/admin/sql-migrations";
    private const string ProxyProfilesEndpoint = "/api/admin/developer/proxy-profiles";
    private const string AuthStatusEndpoint = "/api/auth/status";

    [Fact]
    public async Task All_tabs_enabled_by_default_and_init_reports_them()
    {
        await using var factory = new DeveloperTabSwitchesWebApplicationFactory(
            developerFeaturesEnabled: true, trace: null, dumps: null, protocolDiag: null, sqlMigrations: null);
        using var client = factory.CreateClient();

        using var initResponse = await client.GetAsync(InitEndpoint);
        initResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await initResponse.Content.ReadAsStringAsync());
        var tabs = document.RootElement.GetProperty("data").GetProperty("tabs");
        tabs.GetProperty("invocations").GetBoolean().Should().BeTrue();
        tabs.GetProperty("diagnosticDumps").GetBoolean().Should().BeTrue();
        tabs.GetProperty("simulator").GetBoolean().Should().BeTrue();
        tabs.GetProperty("protocolDiagnostics").GetBoolean().Should().BeTrue();
        tabs.GetProperty("sqlMigrations").GetBoolean().Should().BeTrue();
        // 网络代理池默认关闭（用户不使用出口代理时全链路直连）。
        tabs.GetProperty("proxyProfiles").GetBoolean().Should().BeFalse();

        using var listResponse = await client.GetAsync(ListEndpoint);
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // SQL 迁移页正向断言：分开关全开时接口可见。
        using var sqlResponse = await client.GetAsync(SqlMigrationsEndpoint);
        sqlResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Trace_tab_disabled_hides_invocations_apis_and_reports_in_auth_status()
    {
        await using var factory = new DeveloperTabSwitchesWebApplicationFactory(
            developerFeaturesEnabled: true, trace: false, dumps: null, protocolDiag: null, sqlMigrations: null);
        using var client = factory.CreateClient();

        using var listResponse = await client.GetAsync(ListEndpoint);
        listResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var initResponse = await client.GetAsync(InitEndpoint);
        using var document = JsonDocument.Parse(await initResponse.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("data").GetProperty("tabs").GetProperty("invocations").GetBoolean()
            .Should().BeFalse();

        // 登录态接口（前端 Tab 隐藏的数据源）也应反映分开关状态。
        using var statusResponse = await client.GetAsync(AuthStatusEndpoint);
        statusResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
        status.RootElement.GetProperty("features").GetProperty("developerEnabled").GetBoolean().Should().BeTrue();
        status.RootElement.GetProperty("features").GetProperty("developerTabs").GetProperty("invocations").GetBoolean()
            .Should().BeFalse();
    }

    [Fact]
    public async Task Diagnostics_tab_disabled_hides_dump_apis()
    {
        await using var factory = new DeveloperTabSwitchesWebApplicationFactory(
            developerFeaturesEnabled: true, trace: null, dumps: false, protocolDiag: null, sqlMigrations: null);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(DumpConfigEndpoint);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Protocol_diagnostics_tab_disabled_hides_diag_api()
    {
        await using var factory = new DeveloperTabSwitchesWebApplicationFactory(
            developerFeaturesEnabled: true, trace: null, dumps: null, protocolDiag: false, sqlMigrations: null);
        using var client = factory.CreateClient();

        using var content = new StringContent(
            """{"direction":"request","sourceProtocol":"OpenAI","targetProtocol":"Responses","streaming":false,"modelName":"m","payload":"{}"}""",
            Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(ProtocolDiagEndpoint, content);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sql_migrations_tab_disabled_hides_scripts_api()
    {
        await using var factory = new DeveloperTabSwitchesWebApplicationFactory(
            developerFeaturesEnabled: true, trace: null, dumps: null, protocolDiag: null, sqlMigrations: false);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(SqlMigrationsEndpoint);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Proxy_profiles_disabled_by_default_hides_api_and_reports_false()
    {
        await using var factory = new DeveloperTabSwitchesWebApplicationFactory(
            developerFeaturesEnabled: true, trace: null, dumps: null, protocolDiag: null, sqlMigrations: null);
        using var client = factory.CreateClient();

        using var listResponse = await client.GetAsync(ProxyProfilesEndpoint);
        listResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);

        using var statusResponse = await client.GetAsync(AuthStatusEndpoint);
        using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
        status.RootElement.GetProperty("features").GetProperty("developerTabs").GetProperty("proxyProfiles").GetBoolean()
            .Should().BeFalse();

        using var initResponse = await client.GetAsync(InitEndpoint);
        using var document = JsonDocument.Parse(await initResponse.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("data").GetProperty("tabs").GetProperty("proxyProfiles").GetBoolean()
            .Should().BeFalse();
    }

    [Fact]
    public async Task Proxy_profiles_enabled_serves_api()
    {
        await using var factory = new DeveloperTabSwitchesWebApplicationFactory(
            developerFeaturesEnabled: true, trace: null, dumps: null, protocolDiag: null, sqlMigrations: null, proxyProfiles: true);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(ProxyProfilesEndpoint);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// 旧客户端（如桌面端模型未含新字段）部分回写设置时，未提交的分页开关应保持现值，
    /// 而不是被属性默认值重置（回归防护：曾在桌面端保存一次就会把已关开关洗回默认开）。
    /// </summary>
    [Fact]
    public async Task Partial_settings_update_preserves_unsent_tab_switches()
    {
        await using var factory = new DeveloperTabSwitchesWebApplicationFactory(
            developerFeaturesEnabled: true, trace: false, dumps: null, protocolDiag: null, sqlMigrations: null, proxyProfiles: true);
        using var client = factory.CreateClient();

        using var content = new StringContent(JsonSerializer.Serialize(new
        {
            proxyRequestTimeoutSeconds = 60,
            proxyRetryCount = 1,
            detectionConcurrency = 1,
            circuitBreakerFailureThreshold = 5,
            circuitBreakerRecoveryMinutes = 2,
            usageLogRetentionDays = 7,
            developerFeaturesEnabled = true,
            concurrencyQueueTimeoutSeconds = 120,
            oauthFeaturesEnabled = true
        }), Encoding.UTF8, "application/json");
        using var putResponse = await client.PutAsync("/api/admin/system/settings", content);
        putResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var statusResponse = await client.GetAsync(AuthStatusEndpoint);
        using var status = JsonDocument.Parse(await statusResponse.Content.ReadAsStringAsync());
        var tabs = status.RootElement.GetProperty("features").GetProperty("developerTabs");
        tabs.GetProperty("invocations").GetBoolean().Should().BeFalse("未提交的开关字段应保持现值");
        tabs.GetProperty("proxyProfiles").GetBoolean().Should().BeTrue("未提交的开关字段应保持现值");
    }
}

/// <summary>
/// 按参数播种开发者分开关的测试宿主（null 表示保持实体默认值 true）。
/// </summary>
internal sealed class DeveloperTabSwitchesWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-dev-tabs-{Guid.NewGuid():N}.db");
    private readonly bool _developerFeaturesEnabled;
    private readonly bool? _trace;
    private readonly bool? _dumps;
    private readonly bool? _protocolDiag;
    private readonly bool? _sqlMigrations;
    private readonly bool? _proxyProfiles;

    public DeveloperTabSwitchesWebApplicationFactory(
        bool developerFeaturesEnabled,
        bool? trace,
        bool? dumps,
        bool? protocolDiag,
        bool? sqlMigrations,
        bool? proxyProfiles = null)
    {
        _developerFeaturesEnabled = developerFeaturesEnabled;
        _trace = trace;
        _dumps = dumps;
        _protocolDiag = protocolDiag;
        _sqlMigrations = sqlMigrations;
        _proxyProfiles = proxyProfiles;
    }

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
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        Seed();
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
            DeveloperFeaturesEnabled = _developerFeaturesEnabled,
            DeveloperTraceEnabled = _trace ?? true,
            DeveloperFailureDumpEnabled = _dumps ?? true,
            DeveloperSimulatorEnabled = true,
            DeveloperProtocolDiagnosticsEnabled = _protocolDiag ?? true,
            DeveloperSqlMigrationsEnabled = _sqlMigrations ?? true,
            DeveloperProxyProfilesEnabled = _proxyProfiles ?? false
        });
    }
}
