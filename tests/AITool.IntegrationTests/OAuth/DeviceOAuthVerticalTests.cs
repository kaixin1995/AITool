using AITool.Application.OAuth;
using AITool.Domain.Sites;
using AITool.Infrastructure.OAuth;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.OAuth;

/// <summary>
/// 通用轮询式 OAuth 垂直测试：额度解析器（MiniMax/Qoder）、供给工厂（隐藏 Site + 账号 + 模型映射）、
/// 注册表解析与特性开关矩阵。无真实网络（厂商 HTTP 逻辑以解析器/供给层为界）。
/// </summary>
public sealed class DeviceOAuthVerticalTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-device-oauth-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _serviceProvider;

    public DeviceOAuthVerticalTests()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddLogging();
        services.AddSqlSugar($"Data Source={_databasePath}");
        services.AddScoped<ProxyRequestMetadataCache>();
        _serviceProvider = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_serviceProvider.GetRequiredService<SqlSugar.ISqlSugarClient>());
    }

    // ---- MiniMax 额度解析 ----

    [Fact]
    public void MiniMax_plan_windows_general_is_blocking_and_groups_are_aside()
    {
        const string body = """
            { "model_remains": [
                { "model_name": "general", "current_interval_status": 1, "current_interval_remaining_percent": 80,
                  "start_time": 1700000000, "end_time": 1700018000,
                  "current_weekly_status": 1, "current_weekly_remaining_percent": 40,
                  "weekly_start_time": 1700000000, "weekly_end_time": 1700518400 },
                { "model_name": "MiniMax-M3", "current_interval_status": 2, "current_interval_remaining_percent": 0,
                  "current_weekly_status": 3 }
              ] }
            """;

        var windows = MiniMaxQuotaParser.ParsePlanWindows(body);

        windows.Should().HaveCount(3);
        windows[0].Id.Should().Be("minimax_interval");
        windows[0].UsedPercent.Should().Be(20);
        windows[0].ResetAtUtc.Should().NotBeNull();
        windows[1].Id.Should().Be("minimax_weekly");
        windows[1].UsedPercent.Should().Be(60);
        // 模型组窗口殿后（非阻塞展示），status=2 视为 100% 用尽。
        windows[2].Label.Should().Contain("MiniMax-M3");
        windows[2].UsedPercent.Should().Be(100);
    }

    [Fact]
    public void MiniMax_snapshot_roundtrip()
    {
        var snapshot = MiniMaxQuotaParser.SerializeSnapshot(
            "M Plan Pro · 余额 12.5 积分",
            [new AITool.Application.Accounts.AccountQuotaWindow { Id = "minimax_interval", Label = "套餐 区间窗口", UsedPercent = 30 }]);
        var parsed = new MiniMaxOAuthVendor(new HttpClient(), NullLogger<MiniMaxOAuthVendor>.Instance).ParseCachedQuota(snapshot);

        parsed.Should().NotBeNull();
        parsed!.Success.Should().BeTrue();
        parsed.PlanType.Should().Contain("M Plan Pro");
        parsed.Windows.Should().ContainSingle(w => w.UsedPercent == 30);
    }

    // ---- Qoder 额度解析 ----

    [Fact]
    public void Qoder_usage_envelope_parses_credit_windows()
    {
        const string body = """
            { "displayMode": "qoder", "qoderUsage": {
                "userType": "PRO",
                "userQuota": { "name": "Credits", "unit": "credits", "total": 100, "used": 25 },
                "addOnQuota": { "name": "Add-on", "unit": "credits", "cap": 50, "remaining": 10 }
              } }
            """;

        var result = QoderUsageParser.Parse(body);

        result.Should().NotBeNull();
        result!.Success.Should().BeTrue();
        result.PlanType.Should().Be("PRO");
        result.Windows.Should().HaveCount(2);
        result.Windows[0].UsedPercent.Should().Be(25);
        result.Windows[1].UsedPercent.Should().Be(80); // (50-10)/50
    }

    [Fact]
    public void Qoder_enterprise_mode_has_no_windows()
    {
        var result = QoderUsageParser.Parse("""{ "displayMode": "enterprise" }""");
        result!.Success.Should().BeTrue();
        result.PlanType.Should().Be("Enterprise");
        result.Windows.Should().BeEmpty();
    }

    // ---- 供给工厂（隐藏 Site + 账号 + 模型映射） ----

    [Fact]
    public async Task Provisioner_creates_hidden_site_account_and_model_mappings()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AITool.Infrastructure.Persistence.AppDbContext>();
        var provisioner = new DeviceOAuthAccountProvisioner(
            db,
            scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>(),
            new SiteCascadeDeleter(db),
            NullLogger<DeviceOAuthAccountProvisioner>.Instance);

        var vendor = new MiniMaxOAuthVendor(new HttpClient(), NullLogger<MiniMaxOAuthVendor>.Instance);
        var account = await provisioner.ProvisionAsync(
            vendor,
            displayName: "测试 MiniMax",
            email: "user@example.com",
            externalUserId: "u-1",
            tokens: new DeviceOAuthTokenSet { AccessToken = "at-1", RefreshToken = "rt-1", ExpiresAt = DateTimeOffset.UtcNow.AddHours(2) },
            extraJson: null,
            CancellationToken.None);

        account.Id.Should().NotBe(Guid.Empty);
        account.LinkedSiteId.Should().NotBe(Guid.Empty);

        var site = await db.Sites.InSingleAsync(account.LinkedSiteId);
        site.Should().NotBeNull();
        site!.ApiKey.Should().Be("at-1");
        site.ManagedSource.Should().Be(DeviceOAuthAccountProvisioner.ManagedSource);
        site.ProtocolType.Should().Be("Anthropic");
        site.SupportsAnthropic.Should().BeTrue();
        site.BaseUrl.Should().Contain("agent.minimax.cn");
        site.ExtraHeadersJson.Should().Contain("MiniMaxAgent");

        // 默认模型映射生成
        var mappings = await db.SiteModelMappings.Where(m => m.SiteId == site.Id).ToListAsync();
        mappings.Should().NotBeEmpty();
        mappings.Select(m => m.RemoteModelName).Should().Contain("MiniMax-M3");

        // 重复登录同 ExternalUserId 复用账号（不重复建站）
        var again = await provisioner.ProvisionAsync(
            vendor, "测试 MiniMax", "user@example.com", "u-1",
            new DeviceOAuthTokenSet { AccessToken = "at-2", RefreshToken = "rt-2", ExpiresAt = DateTimeOffset.UtcNow.AddHours(2) },
            null, CancellationToken.None);
        again.Id.Should().Be(account.Id);
        (await db.Sites.CountAsync(s => s.ManagedSource == DeviceOAuthAccountProvisioner.ManagedSource)).Should().Be(1);
        (await db.Sites.InSingleAsync(site.Id)).ApiKey.Should().Be("at-2");
    }

    [Fact]
    public async Task Provisioner_skips_site_for_chat_less_vendors()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AITool.Infrastructure.Persistence.AppDbContext>();
        var provisioner = new DeviceOAuthAccountProvisioner(
            db,
            scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>(),
            new SiteCascadeDeleter(db),
            NullLogger<DeviceOAuthAccountProvisioner>.Instance);

        var vendor = new QoderOAuthVendor(new HttpClient());
        var account = await provisioner.ProvisionAsync(
            vendor, "Qoder 账号", "q@example.com", "q-1",
            new DeviceOAuthTokenSet { AccessToken = "at", RefreshToken = "rt" }, null, CancellationToken.None);

        account.LinkedSiteId.Should().Be(Guid.Empty, "Qoder 无 ChatSiteConfig，只建账号不建站点");
        (await db.Sites.CountAsync(s => s.ManagedSource == DeviceOAuthAccountProvisioner.ManagedSource)).Should().Be(0);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        TryDelete(_databasePath);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
        try { File.Delete(path + "-wal"); } catch { }
        try { File.Delete(path + "-shm"); } catch { }
    }
}
