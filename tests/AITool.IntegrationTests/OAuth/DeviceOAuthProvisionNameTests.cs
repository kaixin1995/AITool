using AITool.Application.OAuth;
using AITool.Infrastructure.OAuth;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.OAuth;

/// <summary>
/// 设备 OAuth 账号命名回归：用户自定义名（登录时命名 / 事后改名）绝不被重登的
/// 上游昵称/邮箱冲掉；非自定义默认名跟随上游刷新（对齐 CodexAccountProvisioner
/// 的 IsDisplayNameCustom 语义），账号名与隐藏 Site 名始终同步。
/// </summary>
public sealed class DeviceOAuthProvisionNameTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-device-oauth-name-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _serviceProvider;

    public DeviceOAuthProvisionNameTests()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddLogging();
        services.AddSqlSugar($"Data Source={_databasePath}");
        services.AddScoped<ProxyRequestMetadataCache>();
        _serviceProvider = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_serviceProvider.GetRequiredService<SqlSugar.ISqlSugarClient>());
    }

    [Fact]
    public async Task Re_login_preserves_custom_name_set_by_rename()
    {
        var (provisioner, db) = CreateProvisioner();
        var vendor = CreateVendor();

        // 首登：上游昵称作默认名（非自定义）。
        var account = await provisioner.ProvisionAsync(vendor, "上游昵称", null, "u-name-1",
            Tokens("at-1"), null, CancellationToken.None);
        account.DisplayName.Should().Be("上游昵称");

        // 用户改名 → 自定义名。
        await provisioner.UpdateAsync(account.Id, "我的主力号", null, CancellationToken.None);

        // 重登（token 失效重新授权）：上游昵称已变，自定义名不得被冲掉。
        var relogin = await provisioner.ProvisionAsync(vendor, "上游新昵称", null, "u-name-1",
            Tokens("at-2"), null, CancellationToken.None);
        relogin.Id.Should().Be(account.Id);

        var row = await db.DeviceOAuthAccounts.InSingleAsync(account.Id);
        row!.DisplayName.Should().Be("我的主力号", "重登不得把用户改名冲回上游昵称");
        (await db.Sites.InSingleAsync(account.LinkedSiteId))!.Name.Should().Be("我的主力号", "站点名与账号名同步保持");
        row.AccessToken.Should().Be("at-2", "token 照常更新");
    }

    [Fact]
    public async Task Re_login_refreshes_default_name_to_latest_upstream_value()
    {
        var (provisioner, db) = CreateProvisioner();
        var vendor = CreateVendor();

        var account = await provisioner.ProvisionAsync(vendor, "昵称v1", null, "u-name-2",
            Tokens("at-1"), null, CancellationToken.None);
        account.DisplayName.Should().Be("昵称v1");

        // 非自定义默认名：重登跟随上游昵称刷新（上游改名要在面板可见）。
        await provisioner.ProvisionAsync(vendor, "昵称v2", null, "u-name-2",
            Tokens("at-2"), null, CancellationToken.None);

        var row = await db.DeviceOAuthAccounts.InSingleAsync(account.Id);
        row!.DisplayName.Should().Be("昵称v2", "默认名跟随上游最新昵称");
        (await db.Sites.InSingleAsync(account.LinkedSiteId))!.Name.Should().Be("昵称v2");
    }

    [Fact]
    public async Task User_supplied_name_at_sign_in_is_custom_and_survives_relogin()
    {
        var (provisioner, db) = CreateProvisioner();
        var vendor = CreateVendor();

        // 登录时用户明确命名（请求体 DisplayName）→ 视为自定义名。
        var account = await provisioner.ProvisionAsync(vendor, "测试号A", null, "u-name-3",
            Tokens("at-1"), null, CancellationToken.None, isUserSuppliedName: true);
        account.DisplayName.Should().Be("测试号A");

        // 重登（未再命名，控制器传上游昵称）：自定义名保持。
        await provisioner.ProvisionAsync(vendor, "上游昵称", null, "u-name-3",
            Tokens("at-2"), null, CancellationToken.None, isUserSuppliedName: false);

        var row = await db.DeviceOAuthAccounts.InSingleAsync(account.Id);
        row!.DisplayName.Should().Be("测试号A", "登录时命名的自定义名不被重登冲掉");
        (await db.Sites.InSingleAsync(account.LinkedSiteId))!.Name.Should().Be("测试号A");
    }

    private (DeviceOAuthAccountProvisioner Provisioner, AppDbContext Db) CreateProvisioner()
    {
        var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (new DeviceOAuthAccountProvisioner(
            db,
            scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>(),
            new SiteCascadeDeleter(db),
            NullLogger<DeviceOAuthAccountProvisioner>.Instance), db);
    }

    private static MiniMaxOAuthVendor CreateVendor()
        => new(new HttpClient(), NullLogger<MiniMaxOAuthVendor>.Instance);

    private static DeviceOAuthTokenSet Tokens(string access) => new()
    {
        AccessToken = access,
        RefreshToken = "rt-" + access,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(2),
    };

    public void Dispose()
    {
        _serviceProvider.Dispose();
        try { File.Delete(_databasePath); } catch { }
        try { File.Delete(_databasePath + "-wal"); } catch { }
        try { File.Delete(_databasePath + "-shm"); } catch { }
    }
}
