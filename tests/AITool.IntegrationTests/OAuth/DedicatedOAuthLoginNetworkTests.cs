using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AITool.Application.Codex;
using AITool.Application.Networking;
using AITool.Application.Google;
using AITool.Application.Kimi;
using AITool.Application.Xai;
using AITool.Domain.Codex;
using AITool.Domain.Google;
using AITool.Domain.Kimi;
using AITool.Domain.Xai;
using AITool.Domain.Sites;
using AITool.Domain.Operations;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Codex;
using AITool.Infrastructure.Google;
using AITool.Infrastructure.Kimi;
using AITool.Infrastructure.Xai;
using AITool.Web.Controllers.Admin;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.OAuth;

/// <summary>No production services, database, system proxy, or external network.</summary>
public sealed class DedicatedOAuthLoginNetworkTests
{
    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("xai")]
    public async Task Login_pins_profile_before_first_request_and_changed_profile_aborts_exchange(string provider)
    {
        var resolver = new Resolver();
        var transport = new Transport();
        var factory = Factory(resolver, transport);
        var selection = new AccountNetworkSelection(NetworkMode.ProxyProfile, Guid.NewGuid());
        var start = await Start(provider, factory, selection);
        var count = transport.Sends.Count;
        resolver.Revoked = true;
        (await Complete(provider, factory, start)).Should().BeOfType<BadRequestObjectResult>();
        transport.Sends.Count.Should().Be(count);
        resolver.Operations.Should().Contain(o => o.ExpectedProfileRevision == 7 && o.Selection == selection);
    }

    [Theory]
    [InlineData("kimi")]
    [InlineData("xai")]
    public async Task Explicit_device_selection_requires_opaque_handle_and_ignores_client_device_code(string provider)
    {
        var resolver = new Resolver();
        var transport = new Transport();
        var factory = Factory(resolver, transport);
        var start = await Start(provider, factory, new(NetworkMode.Direct));
        var count = transport.Sends.Count;
        IActionResult missing = provider == "kimi"
            ? await Kimi(factory).PollToken(new() { DeviceCode = start.GetProperty("deviceCode").GetString()! }, default)
            : await Xai(factory).PollToken(new() { DeviceCode = start.GetProperty("deviceCode").GetString()! }, default);
        missing.Should().BeOfType<BadRequestObjectResult>();
        transport.Sends.Count.Should().Be(count);
        var id = start.GetProperty("serverSessionId").GetString()!;
        IActionResult pending = provider == "kimi"
            ? await Kimi(factory).PollToken(new() { ServerSessionId = id, DeviceCode = "attacker-controlled" }, default)
            : await Xai(factory).PollToken(new() { ServerSessionId = id, DeviceCode = "attacker-controlled" }, default);
        Data(pending).GetProperty("status").GetString().Should().Be("pending");
        transport.Sends.Last().Body.Should().Contain("device_code=server-issued");
        transport.Sends.Last().Body.Should().NotContain("attacker-controlled");
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    public async Task Callback_handle_is_consumed_once_even_when_exchange_fails(string provider)
    {
        var resolver = new Resolver();
        var transport = new Transport();
        var factory = Factory(resolver, transport);
        var start = await Start(provider, factory, new(NetworkMode.Direct));
        await Complete(provider, factory, start);
        var count = transport.Sends.Count;
        (await Complete(provider, factory, start)).Should().BeOfType<BadRequestObjectResult>();
        transport.Sends.Count.Should().Be(count);
    }

    [Theory]
    [InlineData("kimi")]
    [InlineData("xai")]
    public async Task Missing_or_unknown_device_session_never_sends_client_codes(string provider)
    {
        var transport = new Transport();
        var factory = Factory(new Resolver(), transport);
        IActionResult result = provider == "kimi"
            ? await Kimi(factory).PollToken(new() { DeviceCode = "unknown" }, default)
            : await Xai(factory).PollToken(new() { DeviceCode = "unknown" }, default);
        result.Should().BeOfType<BadRequestObjectResult>();
        transport.Sends.Should().BeEmpty();
    }

    [Theory]
    [InlineData("codex", false)]
    [InlineData("codex", true)]
    [InlineData("google", false)]
    [InlineData("google", true)]
    [InlineData("kimi", false)]
    [InlineData("kimi", true)]
    [InlineData("xai", false)]
    [InlineData("xai", true)]
    public async Task Duplicate_raw_and_multipart_imports_preserve_policy_and_use_exact_existing_account(string provider, bool multipart)
    {
        using var fixture = new ImportFixture();
        var id = await fixture.Seed(provider);
        var controller = fixture.Controller(provider);
        var json = provider == "codex"
            ? """{"access_token":"existing-token","refresh_token":"refresh","id_token":"fixture-token","account_id":"workspace","email":"u@example.test"}"""
            : provider == "google" ? """{"refresh_token":"refresh","email":"u@example.test"}"""
            : """{"access_token":"existing-token","refresh_token":"refresh","email":"u@example.test","device_id":"device"}""";
        SetBody(controller, json, multipart);
        var result = await fixture.Import(controller, provider);
        result.Should().BeOfType<OkObjectResult>();
        (await fixture.Store.GetAsync(new(provider, id))).Selection.Mode.Should().Be(NetworkMode.Direct);
        fixture.Transport.Sends.Should().OnlyContain(s => s.Scope == $"{provider}:{id:D}");
        if (provider == "google") fixture.Transport.Sends.Should().NotBeEmpty();
        else fixture.Transport.Sends.Should().BeEmpty("凭证导入不应自动拉取模型目录");
        (await fixture.Db.Client.Queryable<AccountNetworkPolicy>().CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("xai")]
    public async Task Unknown_refresh_only_import_requires_explicit_network_choice_before_any_send(string provider)
    {
        using var fixture = new ImportFixture();
        var controller = fixture.Controller(provider);
        SetBody(controller, """{"refresh_token":"unknown-refresh"}""", false);
        var result = await fixture.Import(controller, provider);
        var payload = JsonSerializer.Serialize(((ObjectResult)result).Value);
        payload.Should().Contain(provider == "google" ? "network_selection_required" : "explicit network selection");
        fixture.Transport.Sends.Should().BeEmpty();
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("kimi")]
    [InlineData("xai")]
    public async Task Failed_explicit_policy_publication_rolls_back_credentials_for_final_existing_identity(string provider)
    {
        using var fixture = new ImportFixture();
        var id = await fixture.Seed(provider);
        var controller = fixture.Controller(provider);
        var json = provider == "codex"
            ? """{"access_token":"replacement","refresh_token":"refresh","id_token":"fixture-token","account_id":"workspace","email":"u@example.test"}"""
            : provider == "google" ? """{"refresh_token":"refresh","email":"u@example.test"}"""
            : """{"access_token":"replacement","refresh_token":"refresh","email":"u@example.test","device_id":"device"}""";
        SetBody(controller, json, false);
        await fixture.Import(controller, provider, (int)NetworkMode.ProxyProfile, Guid.NewGuid());
        (await fixture.Store.GetAsync(new(provider, id))).Selection.Mode.Should().Be(NetworkMode.Direct);
        var token = provider switch
        {
            "codex" => (await fixture.Db.CodexAccounts.InSingleAsync(id)).AccessToken,
            "google" => (await fixture.Db.GoogleAccounts.InSingleAsync(id)).AccessToken,
            "kimi" => (await fixture.Db.KimiAccounts.InSingleAsync(id)).AccessToken,
            _ => (await fixture.Db.XaiAccounts.InSingleAsync(id)).AccessToken
        };
        token.Should().Be("existing-token");
        (await fixture.Db.Sites.ToListAsync()).Should().ContainSingle().Which.ApiKey.Should().Be("existing-token");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Google_existing_refresh_rotation_survives_profile_change_before_publication(bool disable)
    {
        using var fixture = new ImportFixture(realResolver: true);
        var id = await fixture.Seed("google");
        if (!await fixture.Db.Client.Queryable<SystemRuntimeSettings>().AnyAsync(x => x.Id == 1))
            await fixture.Db.InsertAsync(new SystemRuntimeSettings { Id = 1, DeveloperProxyProfilesEnabled = true });
        else
            await fixture.Db.Client.Updateable<SystemRuntimeSettings>()
                .SetColumns(x => x.DeveloperProxyProfilesEnabled == true).Where(x => x.Id == 1).ExecuteCommandAsync();
        var profile = new ProxyProfile { Key = "rotation-test", Name = "test", ProxyUrl = "http://127.0.0.1:9988", Revision = 1 };
        await fixture.Db.InsertAsync(profile);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.ReturnedRefreshToken = "rotated-refresh";
        fixture.Transport.BeforeTokenResponse = async () => { arrived.TrySetResult(); await release.Task; };
        var stale = await fixture.Db.GoogleAccounts.InSingleAsync(id);
        var controller = fixture.Controller("google");
        SetBody(controller, """{"refresh_token":"refresh","email":"u@example.test"}""", false);
        var importing = fixture.Import(controller, "google", (int)NetworkMode.ProxyProfile, profile.Id);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var background = fixture.RunStaleGoogleBackgroundAsync(stale);
        background.IsCompleted.Should().BeFalse("the import owns the same canonical account credential lock");
        fixture.Transport.Sends.Should().ContainSingle();
        try
        {
            profile.Revision++;
            profile.IsEnabled = !disable;
            await fixture.Db.Client.Updateable(profile).UpdateColumns(x => new { x.Revision, x.IsEnabled }).ExecuteCommandAsync();
        }
        finally { release.TrySetResult(); }
        var result = (ObjectResult)await importing;
        (await background).Should().BeFalse("the waiting background refresh must reload and discard its stale snapshot");
        result.StatusCode.Should().Be(207);
        var payload = JsonSerializer.Serialize(result.Value);
        payload.Should().Contain("\"credentialsUpdated\":true").And.Contain("\"networkPolicyUpdated\":false");
        payload.Should().Contain(disable ? "proxy_profile_disabled" : "proxy_profile_revision_conflict");
        var account = await fixture.Db.GoogleAccounts.InSingleAsync(id);
        account.AccessToken.Should().Be("new-token");
        account.RefreshToken.Should().Be("rotated-refresh");
        (await fixture.Db.Sites.InSingleAsync(account.LinkedSiteId)).ApiKey.Should().Be("new-token");
        (await fixture.Store.GetAsync(new("google", id))).Selection.Mode.Should().Be(NetworkMode.Direct);
        fixture.Transport.Sends.Should().ContainSingle("no metadata or model requests may follow the changed profile");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Google_refresh_survives_caller_cancellation_after_send(bool background)
    {
        using var fixture = new ImportFixture();
        var id = await fixture.Seed("google");
        var original = await fixture.Db.GoogleAccounts.InSingleAsync(id);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.ReturnedRefreshToken = "rotation-after-cancel";
        fixture.Transport.BeforeTokenResponse = async () => { arrived.TrySetResult(); await release.Task; };
        using var cancellation = new CancellationTokenSource();
        Task operation = background ? fixture.RunStaleGoogleBackgroundAsync(original, cancellation.Token)
            : fixture.RunGoogle401Async(id, cancellation.Token);
        try
        {
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            release.TrySetResult();
            await operation.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.TrySetResult(); }
        using var verification = fixture.Db.Client.CopyNew();
        var account = await verification.Queryable<GoogleAccount>().InSingleAsync(id);
        account.AccessToken.Should().Be("new-token");
        account.RefreshToken.Should().Be("rotation-after-cancel");
        (await verification.Queryable<Site>().InSingleAsync(account.LinkedSiteId)).ApiKey.Should().Be("new-token");
        fixture.Transport.Sends.Should().ContainSingle();
    }

    [Fact]
    public async Task Google_older_started_login_cannot_replace_credentials_published_before_callback()
    {
        using var fixture = new ImportFixture();
        var id = await fixture.Seed("google");
        fixture.Transport.DistinguishGoogleTokens = true;
        fixture.Transport.ReturnedRefreshToken = "rotated-before-callback";
        var controller = (GoogleAccountsApiController)fixture.Controller("google");
        var start = Data(await controller.StartOAuth(new() { NetworkSelection = new(NetworkMode.Direct) }, default));
        var importer = fixture.Controller("google");
        SetBody(importer, """{"refresh_token":"refresh","email":"u@example.test"}""", false);
        (await fixture.Import(importer, "google")).Should().BeOfType<OkObjectResult>();
        var callback = "http://localhost/callback?code=code&state=" + Uri.EscapeDataString(start.GetProperty("state").GetString()!);
        (await controller.CompleteOAuth(new() { CallbackUrl = callback }, default)).Should().BeOfType<OkObjectResult>();
        var account = await fixture.Db.GoogleAccounts.InSingleAsync(id);
        account.AccessToken.Should().Be("import-token");
        account.RefreshToken.Should().Be("rotated-before-callback");
        (await fixture.Db.Sites.InSingleAsync(account.LinkedSiteId)).ApiKey.Should().Be("import-token");
    }

    [Fact]
    public async Task Google_login_waits_for_import_and_does_not_overwrite_rotated_credentials()
    {
        using var fixture = new ImportFixture();
        var id = await fixture.Seed("google");
        var original = await fixture.Db.GoogleAccounts.InSingleAsync(id);
        await fixture.Db.Client.Updateable<GoogleAccount>()
            .SetColumns(a => new GoogleAccount { IsEnabled = false, ManuallyDisabled = true, DisabledByUpstream = true })
            .Where(a => a.Id == id).ExecuteCommandAsync();
        await fixture.Db.Client.Updateable<Site>().SetColumns(s => s.IsEnabled == false)
            .Where(s => s.Id == original.LinkedSiteId).ExecuteCommandAsync();
        var loginArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLogin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var importArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseImport = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Transport.BeforeTokenRequestResponse = async body =>
        {
            if (body.Contains("authorization_code")) { loginArrived.TrySetResult(); await releaseLogin.Task; }
            else { importArrived.TrySetResult(); await releaseImport.Task; }
        };
        fixture.Transport.ReturnedRefreshToken = "rotated-refresh";
        fixture.Transport.DistinguishGoogleTokens = true;
        var controller = (GoogleAccountsApiController)fixture.Controller("google");
        var start = Data(await controller.StartOAuth(new() { NetworkSelection = new(NetworkMode.Direct) }, default));
        var callback = "http://localhost/callback?code=code&state=" + Uri.EscapeDataString(start.GetProperty("state").GetString()!);
        var login = controller.CompleteOAuth(new() { CallbackUrl = callback }, default);
        try
        {
            await loginArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var importer = fixture.Controller("google");
            SetBody(importer, """{"refresh_token":"refresh","email":"u@example.test"}""", false);
            var importing = fixture.Import(importer, "google");
            await importArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            releaseLogin.TrySetResult();
            // Login can retrieve metadata, but publication must wait for the import's GUID lock.
            await fixture.Transport.UserInfoArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var guard = Task.Delay(TimeSpan.FromMilliseconds(500));
            (await Task.WhenAny(login, guard)).Should().BeSameAs(guard,
                "login publication must wait while import holds the account credential lock");
            releaseImport.TrySetResult();
            (await importing.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeOfType<OkObjectResult>();
            (await login.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeOfType<OkObjectResult>();
        }
        finally { releaseLogin.TrySetResult(); releaseImport.TrySetResult(); }
        var account = await fixture.Db.GoogleAccounts.InSingleAsync(id);
        account.RefreshToken.Should().Be("rotated-refresh");
        account.AccessToken.Should().Be("import-token");
        account.IsEnabled.Should().BeFalse();
        account.ManuallyDisabled.Should().BeTrue();
        account.DisabledByUpstream.Should().BeTrue();
        var site = await fixture.Db.Sites.InSingleAsync(account.LinkedSiteId);
        site.ApiKey.Should().Be("import-token");
        site.IsEnabled.Should().BeFalse();
        (await fixture.Db.GoogleAccounts.ToListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Google_unknown_import_with_explicit_network_creates_account_and_binds_policy()
    {
        using var fixture = new ImportFixture(realResolver: true);
        fixture.Transport.ReturnedRefreshToken = "rotated-new";
        var controller = fixture.Controller("google");
        SetBody(controller, """{"refresh_token":"new-refresh","email":"untrusted@example.test"}""", false);
        var result = await fixture.Import(controller, "google", (int)NetworkMode.Direct);
        result.Should().BeOfType<OkObjectResult>();
        var account = (await fixture.Db.GoogleAccounts.ToListAsync()).Should().ContainSingle().Which;
        account.Email.Should().Be("u@example.test");
        account.AccessToken.Should().Be("new-token");
        account.RefreshToken.Should().Be("rotated-new");
        (await fixture.Store.GetAsync(new("google", account.Id))).Selection.Mode.Should().Be(NetworkMode.Direct);
        fixture.Transport.Sends.Should().Contain(s => s.Path.Contains("token"));
        fixture.Transport.Sends.Should().Contain(s => s.Path.Contains("userinfo"));
    }

    [Fact]
    public async Task Google_unknown_import_conflicting_with_existing_identity_does_not_overwrite()
    {
        using var fixture = new ImportFixture(realResolver: true);
        var id = await fixture.Seed("google");
        fixture.Transport.ReturnedRefreshToken = "rotated-unknown";
        var controller = fixture.Controller("google");
        SetBody(controller, """{"refresh_token":"different-candidate"}""", false);
        var result = (ObjectResult)await fixture.Import(controller, "google", (int)NetworkMode.Direct);
        result.StatusCode.Should().Be(207);
        JsonSerializer.Serialize(result.Value).Should().Contain("account_identity_required");
        fixture.Transport.Sends.Should().Contain(s => s.Path.Contains("userinfo"));
        var account = await fixture.Db.GoogleAccounts.InSingleAsync(id);
        account.RefreshToken.Should().Be("refresh");
        (await fixture.Db.GoogleAccounts.ToListAsync()).Should().ContainSingle();
        (await fixture.Db.Sites.InSingleAsync(account.LinkedSiteId)).ApiKey.Should().Be("existing-token");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Google_email_or_unverified_access_token_does_not_bind_a_different_refresh_token(bool includeAccessToken)
    {
        using var fixture = new ImportFixture();
        var id = await fixture.Seed("google");
        var controller = fixture.Controller("google");
        SetBody(controller, includeAccessToken
            ? """{"refresh_token":"other-account-token","email":"u@example.test","access_token":"existing-token"}"""
            : """{"refresh_token":"other-account-token","email":"u@example.test"}""", false);
        var result = (ObjectResult)await fixture.Import(controller, "google", (int)NetworkMode.Direct);
        result.StatusCode.Should().Be(207);
        JsonSerializer.Serialize(result.Value).Should().Contain("account_identity_required");
        fixture.Transport.Sends.Should().Contain(s => s.Path.Contains("userinfo"));
        var account = await fixture.Db.GoogleAccounts.InSingleAsync(id);
        account.RefreshToken.Should().Be("refresh");
        (await fixture.Db.Sites.InSingleAsync(account.LinkedSiteId)).ApiKey.Should().Be("existing-token");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Google_manual_replacement_preserves_state_on_failure(bool invalid, bool oldTokenPresent)
    {
        using var fixture = new ImportFixture();
        var id = await fixture.Seed("google");
        if (!oldTokenPresent)
            await fixture.Db.Client.Updateable<GoogleAccount>().SetColumns(a => a.RefreshToken == null)
                .Where(a => a.Id == id).ExecuteCommandAsync();
        fixture.Transport.InvalidRefresh = invalid;
        fixture.Transport.DistinguishGoogleTokens = true;
        fixture.Transport.ReturnedRefreshToken = ""; // Google may omit a replacement refresh token.
        var controller = (GoogleAccountsApiController)fixture.Controller("google");
        if (invalid)
            await FluentActions.Awaiting(() => controller.Update(id, new() { RefreshToken = "candidate", DisplayName = "new-name" }, default))
                .Should().ThrowAsync<Exception>();
        else
            (await controller.Update(id, new() { RefreshToken = "candidate", DisplayName = "new-name" }, default)).Should().BeOfType<OkObjectResult>();
        var account = await fixture.Db.GoogleAccounts.InSingleAsync(id);
        account.RefreshToken.Should().Be(invalid ? (oldTokenPresent ? "refresh" : null) : "candidate");
        account.AccessToken.Should().Be(invalid ? "existing-token" : "import-token");
        if (invalid) account.DisplayName.Should().NotBe("new-name");
        else account.DisplayName.Should().Be("new-name");
        var site = await fixture.Db.Sites.InSingleAsync(account.LinkedSiteId);
        site.ApiKey.Should().Be(account.AccessToken);
        site.Name.Should().Be(invalid ? "test" : "new-name");
        (await fixture.Store.GetAsync(new("google", id))).Selection.Mode.Should().Be(NetworkMode.Direct);
    }

    [Theory]
    [InlineData("kimi")]
    [InlineData("xai")]
    public async Task Concurrent_successful_device_polls_exchange_once_then_replay_is_rejected(string provider)
    {
        using var fixture = new ImportFixture();
        var controller = fixture.Controller(provider);
        var start = Data(provider == "kimi"
            ? await ((KimiAccountsApiController)controller).StartDeviceFlow(null, default)
            : await ((XaiAccountsApiController)controller).StartDeviceFlow(default));
        var session = start.GetProperty("serverSessionId").GetString();
        Task<IActionResult> Poll() => provider == "kimi"
            ? ((KimiAccountsApiController)controller).PollToken(new() { ServerSessionId = session }, default)
            : ((XaiAccountsApiController)controller).PollToken(new() { ServerSessionId = session }, default);
        var results = await Task.WhenAll(Poll(), Poll(), Poll());
        results.OfType<OkObjectResult>().Should().ContainSingle();
        results.OfType<BadRequestObjectResult>().Should().HaveCount(2);
        fixture.Transport.Sends.Count(s => s.Path.EndsWith("/token", StringComparison.Ordinal)).Should().Be(1);
        (await Poll()).Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Kimi_candidate_refresh_failure_keeps_old_credentials_and_display_name()
    {
        using var fixture = new ImportFixture();
        var id = await fixture.Seed("kimi");
        fixture.Transport.FailRefresh = true;
        var result = await ((KimiAccountsApiController)fixture.Controller("kimi")).UpdateAccount(id,
            new() { DisplayName = "must-not-persist", RefreshToken = "replacement-candidate" }, default);
        result.Should().BeOfType<BadRequestObjectResult>();
        var account = await fixture.Db.KimiAccounts.InSingleAsync(id);
        account.AccessToken.Should().Be("existing-token");
        account.RefreshToken.Should().Be("refresh");
        account.DisplayName.Should().NotBe("must-not-persist");
        account.ManuallyDisabled.Should().BeFalse();
        (await fixture.Db.Sites.InSingleAsync(account.LinkedSiteId)).ApiKey.Should().Be("existing-token");
    }

    private static void SetBody(ControllerBase controller, string json, bool multipart)
    {
        var http = new DefaultHttpContext();
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        if (multipart)
        {
            http.Request.ContentType = "multipart/form-data; boundary=test";
            var files = new FormFileCollection { new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "credential.json") };
            http.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>(), files);
        }
        else
        {
            http.Request.ContentType = "application/json";
            http.Request.Body = new MemoryStream(bytes);
        }
        controller.ControllerContext = new ControllerContext { HttpContext = http };
    }

    private sealed class ImportFixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"aitool-dedicated-import-{Guid.NewGuid():N}.db");
        private readonly ServiceProvider _services;
        private readonly IServiceScope _scope;
        public AppDbContext Db { get; }
        public AccountNetworkPolicyStore Store { get; }
        public Transport Transport { get; } = new() { Successful = true };
        private readonly NetworkBoundOAuthClientFactory _factory;
        private readonly ProxyRequestMetadataCache _cache;
        public ImportFixture(bool realResolver = false)
        {
            var registrations = new ServiceCollection();
            registrations.AddLogging();
            registrations.AddMemoryCache();
            registrations.AddSqlSugar($"Data Source={_path}");
            registrations.AddScoped<ProxyRequestMetadataCache>();
            _services = registrations.BuildServiceProvider();
            SqlSugarSetup.InitializeDatabase(_services.GetRequiredService<SqlSugar.ISqlSugarClient>());
            _scope = _services.CreateScope();
            Db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
            _cache = _scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
            Store = new(_services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
                new ProxyProfileSecretService(new EphemeralDataProtectionProvider()));
            _factory = realResolver
                ? new(new AccountNetworkClientFactory(Store, Transport), NullLoggerFactory.Instance, new Versions(), Store)
                : Factory(new Resolver(), Transport);
        }
        public async Task<Guid> Seed(string provider)
        {
            var id = Guid.NewGuid();
            var site = new Site { Name = "test", BaseUrl = "https://example.test", ApiKey = "existing-token" };
            await Db.InsertAsync(site);
            if (provider == "codex") await Db.InsertAsync(new CodexAccount { Id = id, LinkedSiteId = site.Id, Email = "u@example.test", AccountId = "workspace", AccessToken = "existing-token", RefreshToken = "refresh" });
            else if (provider == "google") await Db.InsertAsync(new GoogleAccount { Id = id, LinkedSiteId = site.Id, Email = "u@example.test", AccountKind = "Antigravity", AccessToken = "existing-token", RefreshToken = "refresh" });
            else if (provider == "kimi") await Db.InsertAsync(new KimiAccount { Id = id, LinkedSiteId = site.Id, Email = "u@example.test", DeviceId = "device", AccessToken = "existing-token", RefreshToken = "refresh" });
            else await Db.InsertAsync(new XaiAccount { Id = id, LinkedSiteId = site.Id, Email = "u@example.test", AccessToken = "existing-token", RefreshToken = "refresh" });
            await Store.SetAsync(new(provider, id), new(NetworkMode.Direct));
            return id;
        }
        public ControllerBase Controller(string provider)
        {
            var cascade = new SiteCascadeDeleter(Db);
            if (provider == "codex") return new CodexApiController(Db, null!, new(Db, _cache, new Catalog(), cascade,
                NullLogger<CodexAccountProvisioner>.Instance, _factory, Store), null!, null!, null!, null!, null!, null!,
                NullLogger<CodexApiController>.Instance, _cache, _factory);
            if (provider == "google") return new GoogleAccountsApiController(Db, new GoogleOAuthClient(new HttpClient(new Forbidden())), new(Db, _cache, null!, null!, cascade,
                NullLogger<GoogleAccountProvisioner>.Instance, _factory, Store), null!, null!, null!,
                NullLogger<GoogleAccountsApiController>.Instance, _factory);
            if (provider == "kimi") return new KimiAccountsApiController(Db, null!, new(Db, _cache, null!,
                new KimiCredentialRefreshService(Db, null!, _cache, NullLogger<KimiCredentialRefreshService>.Instance, _factory), cascade,
                NullLogger<KimiAccountProvisioner>.Instance, _factory, Store), null!, null!, null!,
                NullLogger<KimiAccountsApiController>.Instance, _factory);
            return new XaiAccountsApiController(Db, null!, new(Db, _cache, null!, cascade,
                NullLogger<XaiAccountProvisioner>.Instance, _factory, Store), null!, null!,
                NullLogger<XaiAccountsApiController>.Instance, _factory);
        }
        public Task<bool> RunStaleGoogleBackgroundAsync(GoogleAccount stale, CancellationToken cancellationToken = default)
        {
            // Invoke one worker iteration directly to deterministically queue the already-read stale
            // account while the import owns the lock, without timers, scan delays, or live services.
            var worker = new GoogleTokenRefreshService(_services, NullLogger<GoogleTokenRefreshService>.Instance, null!);
            var iteration = typeof(GoogleTokenRefreshService).GetMethod("RefreshOneAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            return (Task<bool>)iteration.Invoke(worker, new object?[] { Db, _cache, null, stale, cancellationToken, _factory })!;
        }
        public Task<string?> RunGoogle401Async(Guid id, CancellationToken cancellationToken)
            => new GoogleCredentialRefreshService(Db, null!, _cache, NullLogger<GoogleCredentialRefreshService>.Instance, _factory)
                .RefreshForAccountAsync(id, "existing-token", cancellationToken);
        public Task<IActionResult> Import(ControllerBase controller, string provider, int? networkMode = null, Guid? proxyProfileId = null) => provider switch
        {
            "codex" => ((CodexApiController)controller).ImportCredential(null, default, networkMode, proxyProfileId),
            "google" => ((GoogleAccountsApiController)controller).ImportCredential(null, null, default, networkMode, proxyProfileId),
            "kimi" => ((KimiAccountsApiController)controller).ImportCredential(null, default, networkMode, proxyProfileId),
            _ => ((XaiAccountsApiController)controller).ImportCredential(null, default, networkMode, proxyProfileId)
        };
        public void Dispose()
        {
            _scope.Dispose(); _services.Dispose();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) try { File.Delete(_path + suffix); } catch { }
        }
    }
    private sealed class Catalog : ICodexModelCatalog
    {
        public IReadOnlyList<string> GetModelsForPlan(string? planType) => [];
        public Task<CodexCatalogRefreshResult> TryRefreshFromRemoteAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new CodexCatalogRefreshResult { Success = true });
    }

    private static NetworkBoundOAuthClientFactory Factory(Resolver resolver, Transport transport)
        => new(new AccountNetworkClientFactory(resolver, transport), NullLoggerFactory.Instance, new Versions(), resolver);
    private static KimiAccountsApiController Kimi(NetworkBoundOAuthClientFactory factory) => new(null!,
        new KimiOAuthClient(new HttpClient(new Forbidden()), NullLogger<KimiOAuthClient>.Instance), null!, null!, null!, null!,
        NullLogger<KimiAccountsApiController>.Instance, factory);
    private static XaiAccountsApiController Xai(NetworkBoundOAuthClientFactory factory) => new(null!,
        new XaiOAuthClient(new HttpClient(new Forbidden()), NullLogger<XaiOAuthClient>.Instance), null!, null!, null!,
        NullLogger<XaiAccountsApiController>.Instance, factory);
    private static CodexApiController Codex(NetworkBoundOAuthClientFactory factory) => new(null!,
        new CodexOAuthClient(new HttpClient(new Forbidden())), null!, null!, null!, null!, null!, null!, null!,
        NullLogger<CodexApiController>.Instance, null!, factory);
    private static GoogleAccountsApiController Google(NetworkBoundOAuthClientFactory factory) => new(null!,
        new GoogleOAuthClient(new HttpClient(new Forbidden())), null!, null!, null!, null!,
        NullLogger<GoogleAccountsApiController>.Instance, factory);
    private static async Task<JsonElement> Start(string provider, NetworkBoundOAuthClientFactory factory, AccountNetworkSelection selection)
        => Data(provider switch
        {
            "codex" => await Codex(factory).StartOAuth(new() { NetworkSelection = selection }, default),
            "google" => await Google(factory).StartOAuth(new() { NetworkSelection = selection }, default),
            "kimi" => await Kimi(factory).StartDeviceFlow(new() { NetworkSelection = selection }, default),
            _ => await Xai(factory).StartDeviceFlow(default, new() { NetworkSelection = selection })
        });
    private static async Task<IActionResult> Complete(string provider, NetworkBoundOAuthClientFactory factory, JsonElement start)
    {
        if (provider == "kimi") return await Kimi(factory).PollToken(new() { ServerSessionId = start.GetProperty("serverSessionId").GetString() }, default);
        if (provider == "xai") return await Xai(factory).PollToken(new() { ServerSessionId = start.GetProperty("serverSessionId").GetString() }, default);
        var callback = "http://localhost/callback?code=code&state=" + Uri.EscapeDataString(start.GetProperty("state").GetString()!);
        return provider == "codex"
            ? await Codex(factory).CompleteOAuth(new() { CallbackUrl = callback }, default)
            : await Google(factory).CompleteOAuth(new() { CallbackUrl = callback }, default);
    }
    private static JsonElement Data(IActionResult result) => JsonSerializer.SerializeToElement(((OkObjectResult)result).Value,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private sealed class Versions : ICodexClientVersionResolver
    {
        public Task<CodexClientVersionInfo> ResolveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new CodexClientVersionInfo { ClientVersion = "1", UserAgent = "test" });
    }
    private sealed class Resolver : IAccountNetworkPolicyResolver
    {
        public ConcurrentQueue<NetworkOperationContext> Operations { get; } = new();
        public bool Revoked { get; set; }
        public Task<ResolvedNetworkConfiguration> ResolveAsync(NetworkOperationContext operation, CancellationToken cancellationToken = default)
        {
            Operations.Enqueue(operation);
            if (Revoked) throw new NetworkPolicyException("proxy_profile_revision_conflict", "Profile changed.");
            return Task.FromResult(new ResolvedNetworkConfiguration
            { Mode = operation.Selection.Mode, ProxyProfileId = operation.Selection.ProxyProfileId, ProfileRevision = 7 });
        }
        public Task<ResolvedNetworkConfiguration> ResolveForAccountAsync(AccountIdentity account, CancellationToken cancellationToken = default)
            => Task.FromResult(ResolvedNetworkConfiguration.Direct);
    }
    private sealed record Send(string Scope, string Path, string Body);
    private sealed class Transport : INetworkTransport
    {
        public ConcurrentQueue<Send> Sends { get; } = new();
        public bool Successful { get; init; }
        public bool FailRefresh { get; set; }
        public string ReturnedRefreshToken { get; set; } = "refresh";
        public Func<Task>? BeforeTokenResponse { get; set; }
        public Func<string, Task>? BeforeTokenRequestResponse { get; set; }
        public TaskCompletionSource UserInfoArrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DistinguishGoogleTokens { get; set; }
        public bool InvalidRefresh { get; set; }
        public ValueTask<INetworkTransportLease> AcquireAsync(ResolvedNetworkConfiguration configuration, string isolationScope,
            string purpose, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<INetworkTransportLease>(new Lease(new HttpClient(new Handler(this, isolationScope))));
        private sealed class Lease(HttpClient client) : INetworkTransportLease
        {
            public HttpClient Client => client;
            public ValueTask DisposeAsync() { client.Dispose(); return ValueTask.CompletedTask; }
        }
        private sealed class Handler(Transport owner, string scope) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                owner.Sends.Enqueue(new(scope, path, body));
                if (path.Contains("userinfo")) owner.UserInfoArrived.TrySetResult();
                if (path.Contains("token") && owner.BeforeTokenRequestResponse is not null)
                    await owner.BeforeTokenRequestResponse(body);
                if (path.Contains("token") && owner.BeforeTokenResponse is not null)
                    await owner.BeforeTokenResponse();
                cancellationToken.ThrowIfCancellationRequested();
                if (owner.InvalidRefresh && path.Contains("token"))
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""") };
                if (owner.FailRefresh && path.Contains("token"))
                    throw new NetworkPolicyException("proxy_profile_disabled", "Profile disabled.");
                if (owner.Successful)
                {
                    var discoveryRequest = path.Contains("openid-configuration");
                    var deviceRequest = path.Contains("device") && !path.Contains("token");
                    var successfulJson = discoveryRequest ? """{"device_authorization_endpoint":"https://auth.x.ai/device","token_endpoint":"https://auth.x.ai/token"}"""
                        : deviceRequest ? """{"device_code":"server-issued","user_code":"ABCD","verification_uri":"https://example.test/verify","expires_in":600,"interval":5}"""
                        : path.Contains("token")
                        ? JsonSerializer.Serialize(new { access_token = owner.DistinguishGoogleTokens ? (body.Contains("authorization_code") ? "login-token" : "import-token") : "new-token", refresh_token = owner.ReturnedRefreshToken, expires_in = 3600 })
                        : path.Contains("userinfo") ? """{"email":"u@example.test"}"""
                        : path.Contains("loadCodeAssist") ? """{"cloudaicompanionProject":"project","currentTier":{"id":"free"}}"""
                        : """{"models":{},"data":[]}""";
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(successfulJson) };
                }
                var discovery = path.Contains("openid-configuration");
                var device = path.Contains("device") && !path.Contains("token");
                var json = discovery ? """{"device_authorization_endpoint":"https://auth.x.ai/device","token_endpoint":"https://auth.x.ai/token"}"""
                    : device ? """{"device_code":"server-issued","user_code":"ABCD","verification_uri":"https://example.test/verify","expires_in":600,"interval":5}"""
                    : """{"error":"authorization_pending"}""";
                return new HttpResponseMessage(device || discovery ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
                { Content = new StringContent(json) };
            }
        }
    }
    private sealed class Forbidden : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Unbound client must never send.");
    }
}
