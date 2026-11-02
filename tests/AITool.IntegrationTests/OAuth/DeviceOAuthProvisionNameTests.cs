using AITool.Application.OAuth;
using AITool.Application.Kimi;
using AITool.Application.Xai;
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
        var client = _serviceProvider.GetRequiredService<SqlSugar.ISqlSugarClient>();
        var settings = client.Queryable<AITool.Domain.Operations.SystemRuntimeSettings>().InSingle(1);
        if (settings is null)
            client.Insertable(new AITool.Domain.Operations.SystemRuntimeSettings { Id = 1, OAuthFeaturesEnabled = true }).ExecuteCommand();
        else
        {
            settings.OAuthFeaturesEnabled = true;
            client.Updateable(settings).ExecuteCommand();
        }
    }

    [Fact]
    public async Task Provision_does_not_import_default_models_and_relogin_preserves_selection()
    {
        var (provisioner, db) = CreateProvisioner();
        var vendor = CreateVendor();
        var account = await provisioner.ProvisionAsync(vendor, "manual", null, "manual-models",
            Tokens("at-1"), null, CancellationToken.None);
        (await db.ModelLibraryItems.ToListAsync()).Should().BeEmpty();
        (await db.SiteModelMappings.ToListAsync()).Should().BeEmpty();

        await provisioner.SyncSelectedModelsAsync(account.Id, [("selected-model", "selected-model", true)], CancellationToken.None);
        await provisioner.ProvisionAsync(vendor, "manual", null, "manual-models", Tokens("at-2"), null, CancellationToken.None);
        (await db.ModelLibraryItems.ToListAsync()).Select(x => x.ModelName).Should().Equal("selected-model");
        (await db.SiteModelMappings.ToListAsync()).Should().ContainSingle(x => x.RemoteModelName == "selected-model" && x.IsEnabled);
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

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task Relogin_clears_reauth_without_overriding_other_disable_reasons(bool manual, bool feature, bool expectedEnabled)
    {
        var (provisioner, db) = CreateProvisioner();
        var vendor = CreateVendor();
        var account = await provisioner.ProvisionAsync(vendor, "test", null, "reauth-user", Tokens("old"), null, CancellationToken.None);
        var row = (await db.DeviceOAuthAccounts.InSingleAsync(account.Id))!;
        row.RequiresReauth = true;
        row.IsEnabled = false;
        row.ManuallyDisabled = manual;
        row.DisabledByFeatureToggle = feature;
        await db.UpdateAsync(row);
        var site = (await db.Sites.InSingleAsync(account.LinkedSiteId))!;
        site.IsEnabled = false;
        await db.UpdateAsync(site);

        await provisioner.ProvisionAsync(vendor, "test", null, "reauth-user", Tokens("new"), null, CancellationToken.None);

        row = (await db.DeviceOAuthAccounts.InSingleAsync(account.Id))!;
        row.RequiresReauth.Should().BeFalse();
        row.ManuallyDisabled.Should().Be(manual);
        row.DisabledByFeatureToggle.Should().Be(feature);
        row.IsEnabled.Should().Be(expectedEnabled);
        row.AccessToken.Should().Be("new");
        (await db.Sites.InSingleAsync(account.LinkedSiteId))!.IsEnabled.Should().Be(expectedEnabled);
    }

    [Fact]
    public async Task Relogin_while_global_oauth_is_off_stays_disabled_and_records_feature_reason()
    {
        var (provisioner, db) = CreateProvisioner();
        var vendor = CreateVendor();
        var account = await provisioner.ProvisionAsync(vendor, "test", null, "feature-off-user", Tokens("old"), null, default);
        var row = (await db.DeviceOAuthAccounts.InSingleAsync(account.Id))!;
        row.RequiresReauth = true;
        row.IsEnabled = false;
        await db.UpdateAsync(row);
        var settings = await db.Client.Queryable<AITool.Domain.Operations.SystemRuntimeSettings>().InSingleAsync(1);
        if (settings is null)
            await db.InsertAsync(new AITool.Domain.Operations.SystemRuntimeSettings { Id = 1, OAuthFeaturesEnabled = false });
        else
        {
            settings.OAuthFeaturesEnabled = false;
            await db.UpdateAsync(settings);
        }

        await provisioner.ProvisionAsync(vendor, "test", null, "feature-off-user", Tokens("new"), null, default);

        row = (await db.DeviceOAuthAccounts.InSingleAsync(account.Id))!;
        row.RequiresReauth.Should().BeFalse();
        row.DisabledByFeatureToggle.Should().BeTrue();
        row.IsEnabled.Should().BeFalse();
        (await db.Sites.InSingleAsync(row.LinkedSiteId))!.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Manual_enable_and_refresh_token_edit_do_not_bypass_rejected_access_token()
    {
        var (provisioner, db) = CreateProvisioner();
        var account = await provisioner.ProvisionAsync(CreateVendor(), "test", null, "blocked-user", Tokens("old"), null, CancellationToken.None);
        var row = (await db.DeviceOAuthAccounts.InSingleAsync(account.Id))!;
        row.RequiresReauth = true;
        row.IsEnabled = false;
        row.ManuallyDisabled = true;
        await db.UpdateAsync(row);
        var site = (await db.Sites.InSingleAsync(account.LinkedSiteId))!;
        site.IsEnabled = false;
        await db.UpdateAsync(site);

        await provisioner.ToggleAsync(account.Id, true, CancellationToken.None);
        await provisioner.UpdateAsync(account.Id, "test", "replacement-refresh", CancellationToken.None);

        row = (await db.DeviceOAuthAccounts.InSingleAsync(account.Id))!;
        row.ManuallyDisabled.Should().BeFalse();
        row.RequiresReauth.Should().BeTrue();
        row.IsEnabled.Should().BeFalse();
        row.RefreshToken.Should().Be("replacement-refresh");
        (await db.Sites.InSingleAsync(account.LinkedSiteId))!.IsEnabled.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task Kimi_and_xai_relogin_preserve_independent_disable_reasons(bool manual, bool feature, bool expectedEnabled)
    {
        var (_, db) = CreateProvisioner();
        var cache = _serviceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        var cascade = new SiteCascadeDeleter(db);
        var kimi = new KimiAccountProvisioner(db, cache, null!, null!, cascade, NullLogger<KimiAccountProvisioner>.Instance);
        var xai = new XaiAccountProvisioner(db, cache, null!, cascade, NullLogger<XaiAccountProvisioner>.Instance);
        var ki = new KimiProvisionInput { UserId = "user", AccessToken = "old", RefreshToken = "refresh" };
        var xi = new XaiProvisionInput { ExternalUserId = "user", AccessToken = "old", RefreshToken = "refresh" };
        var k = await kimi.ProvisionFromTokensAsync(ki, default);
        var x = await xai.ProvisionFromTokensAsync(xi, default);
        k.IsEnabled = x.IsEnabled = false;
        k.RequiresReauth = x.RequiresReauth = true;
        k.ManuallyDisabled = x.ManuallyDisabled = manual;
        k.DisabledByFeatureToggle = x.DisabledByFeatureToggle = feature;
        await db.UpdateAsync(k);
        await db.UpdateAsync(x);
        foreach (var id in new[] { k.LinkedSiteId, x.LinkedSiteId })
        {
            var site = (await db.Sites.InSingleAsync(id))!;
            site.IsEnabled = false;
            await db.UpdateAsync(site);
        }
        ki.AccessToken = xi.AccessToken = "new";
        await kimi.ProvisionFromTokensAsync(ki, default);
        await xai.ProvisionFromTokensAsync(xi, default);
        k = (await db.KimiAccounts.InSingleAsync(k.Id))!;
        x = (await db.XaiAccounts.InSingleAsync(x.Id))!;
        k.RequiresReauth.Should().BeFalse();
        x.RequiresReauth.Should().BeFalse();
        k.ManuallyDisabled.Should().Be(manual);
        x.ManuallyDisabled.Should().Be(manual);
        k.IsEnabled.Should().Be(expectedEnabled);
        x.IsEnabled.Should().Be(expectedEnabled);
        (await db.Sites.InSingleAsync(k.LinkedSiteId))!.IsEnabled.Should().Be(expectedEnabled);
        (await db.Sites.InSingleAsync(x.LinkedSiteId))!.IsEnabled.Should().Be(expectedEnabled);
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
