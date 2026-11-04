using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AITool.Application.Networking;
using AITool.Application.OAuth;
using AITool.Application.Proxy;
using AITool.Domain.OAuth;
using AITool.Domain.Operations;
using AITool.Infrastructure.OAuth;
using AITool.Infrastructure.Persistence;
using AITool.Web.Controllers.Admin;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.OAuth;

/// <summary>Recording resolver/transport only: no upstream, system proxy, or production database.</summary>
public sealed class DeviceOAuthNetworkTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aitool-device-network-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;
    private readonly AppDbContext _db;
    private readonly RecordingResolver _resolver = new();
    private readonly RecordingTransport _transport = new();
    private readonly DeviceOAuthVendorRegistry _registry;
    private readonly NetworkBoundDeviceOAuthVendorFactory _factory;
    private readonly AccountNetworkPolicyStore _store;

    public DeviceOAuthNetworkTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSqlSugar($"Data Source={_path}");
        services.AddScoped<ProxyRequestMetadataCache>();
        _services = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_services.GetRequiredService<SqlSugar.ISqlSugarClient>());
        _scope = _services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
        _registry = new DeviceOAuthVendorRegistry([
            new CopilotOAuthVendor(new HttpClient(new ForbiddenHandler())),
            new ClaudeOAuthVendor(new HttpClient(new ForbiddenHandler())),
            new QoderOAuthVendor(new HttpClient(new ForbiddenHandler()))
        ]);
        _factory = new(new AccountNetworkClientFactory(_resolver, _transport), _resolver, _registry,
            NullLogger<MiniMaxOAuthVendor>.Instance);
        _store = new(_services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            new ProxyProfileSecretService(new EphemeralDataProtectionProvider()));
    }

    [Theory]
    [InlineData("minimax-cn", "www.minimaxi.com", "/v1/api/openplatform/coding_plan/remains", false)]
    [InlineData("minimax-global", "platform.minimax.io", "/v1/api/openplatform/coding_plan/remains", false)]
    [InlineData("workbuddy-cn", "www.codebuddy.cn", "/v2/billing/meter/get-user-resource", false)]
    [InlineData("workbuddy-global", "www.workbuddy.ai", "/billing/meter/get-user-resource", false)]
    [InlineData("qoder", "openapi.qoder.sh", "/sash/api/v2/me/usage", false)]
    [InlineData("qoder-cn", "openapi.qoder.com.cn", "/sash/api/v2/me/usage", false)]
    [InlineData("trae-cn", "trae-api-cn.mchost.guru", "/api/ide/v1/batch_get_detail_param", true)]
    [InlineData("trae-global", "coresg-normal.trae.ai", "/api/ide/v1/batch_get_detail_param", true)]
    [InlineData("kiro", "management.us-east-1.kiro.dev", "/Get-Usage-Limits", false)]
    [InlineData("copilot", "api.github.com", "/copilot_internal/user", false)]
    [InlineData("claude", "models.dev", "/api.json", true)]
    [InlineData("zcode", "api.z.ai", "/api/monitor/usage/quota/limit", false)]
    public async Task Every_factory_variant_preserves_region_timeout_and_account_or_pending_binding(
        string vendorKey, string host, string path, bool models)
    {
        var defaultSends = 0;
        using var defaultClient = new HttpClient(new ForbiddenHandler(() => Interlocked.Increment(ref defaultSends)));
        var registry = new DeviceOAuthVendorRegistry([
            new MiniMaxOAuthVendor(defaultClient, NullLogger<MiniMaxOAuthVendor>.Instance),
            new MiniMaxOAuthVendor(defaultClient, NullLogger<MiniMaxOAuthVendor>.Instance, global: true),
            new WorkBuddyOAuthVendor(defaultClient), new WorkBuddyOAuthVendor(defaultClient, global: true),
            new QoderOAuthVendor(defaultClient), new QoderOAuthVendor(defaultClient, china: true),
            new TraeOAuthVendor(defaultClient), new TraeOAuthVendor(defaultClient, global: true),
            new KiroOAuthVendor(defaultClient), new CopilotOAuthVendor(defaultClient),
            new ClaudeOAuthVendor(defaultClient), new ZCodeOAuthVendor(defaultClient)
        ]);
        var factory = new NetworkBoundDeviceOAuthVendorFactory(new AccountNetworkClientFactory(_resolver, _transport),
            _resolver, registry, NullLogger<MiniMaxOAuthVendor>.Instance);
        // Quota-capable vendors receive a genuine refusal response, avoiding invented successful
        // quota formats. Claude/Trae have passive quota only, so exercise their real model APIs.
        _transport.StatusCode = models ? HttpStatusCode.OK : HttpStatusCode.Unauthorized;
        _transport.ResponseContent = () => new StringContent(vendorKey == "claude"
            ? """{"anthropic":{"models":{"claude-sonnet-4-5":{"id":"claude-sonnet-4-5","name":"Sonnet","modalities":{"output":["text"]}}}}}"""
            : """{"function_configs":[{"function":"chat_v3","config_info_list":[{"config_name":"model-a","display_name":"A","usage":"chat_completion","config_switch":true}]}]}""");
        var account = Account(vendorKey, "matrix-token");
        var operation = await factory.CreateOperationAsync(new(NetworkMode.ProxyProfile, Guid.NewGuid()),
            "pending-matrix:" + vendorKey, default);
        using var accountBound = factory.ForAccount(account, "matrix-account");
        using var pendingBound = factory.ForOperation(vendorKey, operation, "matrix-pending");
        var metadata = registry.Find(vendorKey)!;
        foreach (var bound in new[] { accountBound, pendingBound })
        {
            bound.Vendor.VendorKey.Should().Be(vendorKey);
            bound.Vendor.Label.Should().Be(metadata.Label);
            bound.Vendor.ModelCatalogScope.Should().Be(metadata.ModelCatalogScope);
            bound.Vendor.ChatSiteConfig.Should().BeEquivalentTo(metadata.ChatSiteConfig);
            bound.Vendor.DefaultModels.Should().BeEquivalentTo(metadata.DefaultModels);
            VendorClient(bound.Vendor).Timeout.Should().Be(TimeSpan.FromSeconds(20));
            await Invoke(bound.Vendor);
        }
        _transport.Sends.Should().HaveCount(2);
        _transport.Sends.Should().OnlyContain(send => send.Host == host && send.Path == path);
        var expectedAuth = vendorKey == "claude" ? null
            : vendorKey.StartsWith("trae-", StringComparison.Ordinal) ? "Cloud-IDE-JWT matrix-token"
            : vendorKey == "zcode" ? "matrix-token"
            : vendorKey == "copilot" ? "Bearer refresh-matrix-token" : "Bearer matrix-token";
        _transport.Sends.Should().OnlyContain(send => send.Authorization == expectedAuth);
        _transport.Sends.Should().ContainSingle(send => send.Scope == Scope(account));
        _transport.Sends.Should().ContainSingle(send => send.Scope == operation.IsolationKey);
        _transport.Acquisitions.Should().ContainSingle(a => a.Scope == Scope(account)
            && a.Purpose == "matrix-account" && a.Mode == NetworkMode.Direct);
        _transport.Acquisitions.Should().ContainSingle(a => a.Scope == operation.IsolationKey
            && a.Purpose == "matrix-pending" && a.Mode == NetworkMode.ProxyProfile);
        _resolver.Accounts.Should().OnlyContain(identity => identity == NetworkBoundDeviceOAuthVendorFactory.Identity(account.Id));
        _resolver.Operations.Should().OnlyContain(context => context == operation && context.ExpectedProfileRevision == 7);

        _resolver.RevokedAccounts.Add(account.Id);
        _resolver.RevokedSessions.Add(operation.IsolationKey);
        await ((Func<Task>)(() => Invoke(accountBound.Vendor))).Should().ThrowAsync<NetworkPolicyException>();
        await ((Func<Task>)(() => Invoke(pendingBound.Vendor))).Should().ThrowAsync<NetworkPolicyException>();
        _transport.Sends.Should().HaveCount(2, "revoked bindings must not fall back to an unbound send");
        Volatile.Read(ref defaultSends).Should().Be(0, "registry/default clients are metadata-only for every branch");

        async Task Invoke(IDeviceOAuthVendor vendor)
        {
            if (models)
                (await vendor.FetchModelsAsync(Token(account), null, default)).Should().NotBeEmpty();
            else
                (await vendor.QueryQuotaAsync(Token(account), null, default)).CredentialInvalid.Should().BeTrue();
        }
    }

    // Inspect only the operation-owned wrapper's timeout; no production seam or pooled client
    // mutation is needed to prove all constructor branches retain the historical 20-second budget.
    private static HttpClient VendorClient(IDeviceOAuthVendor vendor)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var implementation = vendor.GetType().GetFields(flags)
            .Single(field => field.FieldType == typeof(IDeviceOAuthVendor)).GetValue(vendor)!;
        return (HttpClient)implementation.GetType().GetFields(flags)
            .Single(field => field.FieldType == typeof(HttpClient)).GetValue(implementation)!;
    }

    [Fact]
    public async Task Accounts_A_B_models_are_isolated_and_repeat_sends_resolve_current_policy()
    {
        var a = Account("copilot", "A");
        var b = Account("copilot", "B");
        using var first = _factory.ForAccount(a, "models");
        using var second = _factory.ForAccount(b, "models");
        await Task.WhenAll(first.Vendor.FetchModelsAsync(Token(a), null, default),
            second.Vendor.FetchModelsAsync(Token(b), null, default));
        _transport.Sends.Should().Contain(s => s.Scope == Scope(a) && s.Authorization == "Bearer A");
        _transport.Sends.Should().Contain(s => s.Scope == Scope(b) && s.Authorization == "Bearer B");
        var resolutionsA = _resolver.Accounts.Count(identity => identity.AccountId == a.Id);
        var resolutionsB = _resolver.Accounts.Count(identity => identity.AccountId == b.Id);
        resolutionsA.Should().BeGreaterThan(0);
        resolutionsB.Should().BeGreaterThan(0);
        _resolver.RevokedAccounts.Add(a.Id);
        await first.Vendor.Invoking(v => v.FetchModelsAsync(Token(a), null, default))
            .Should().ThrowAsync<NetworkPolicyException>();
        _resolver.Accounts.Count(identity => identity.AccountId == a.Id).Should().BeGreaterThan(resolutionsA,
            "the repeated A operation must revalidate even though revocation prevents a transport send");
        await second.Vendor.FetchModelsAsync(Token(b), null, default);
        _resolver.Accounts.Count(identity => identity.AccountId == b.Id).Should().BeGreaterThan(resolutionsB,
            "the repeated B operation must independently revalidate its current policy");
        _transport.Sends.Count(s => s.Scope == Scope(a)).Should().Be(1);
        _transport.Sends.Count(s => s.Scope == Scope(b)).Should().Be(2);
        _resolver.Accounts.Should().OnlyContain(identity => identity.ProviderKey == "device-oauth");
    }

    [Fact]
    public async Task Cross_login_pending_polls_keep_distinct_fixed_context_and_revocation_fails_closed()
    {
        var controller = Controller();
        var a = Data(await controller.StartSignIn(new() { Vendor = "copilot", NetworkSelection = new(NetworkMode.ProxyProfile, Guid.NewGuid()) }, default));
        var b = Data(await controller.StartSignIn(new() { Vendor = "copilot" }, default));
        var sessionA = a.GetProperty("sessionId").GetString()!;
        var sessionB = b.GetProperty("sessionId").GetString()!;
        await Task.WhenAll(controller.PollSignIn(new() { SessionId = sessionA }, default),
            controller.PollSignIn(new() { SessionId = sessionB }, default));
        await controller.PollSignIn(new() { SessionId = sessionA }, default);
        var keyA = "device-oauth-login:" + sessionA;
        var keyB = "device-oauth-login:" + sessionB;
        _resolver.Operations.Where(o => o.IsolationKey == keyA).Should().OnlyContain(o =>
            o.Selection.Mode == NetworkMode.ProxyProfile && o.ExpectedProfileRevision == 7);
        _resolver.Operations.Where(o => o.IsolationKey == keyB).Should().OnlyContain(o => o.Selection.Mode == NetworkMode.Legacy);
        var count = _transport.Sends.Count;
        _resolver.RevokedSessions.Add(keyA);
        (await controller.PollSignIn(new() { SessionId = sessionA }, default)).Should().BeOfType<BadRequestObjectResult>();
        _transport.Sends.Count.Should().Be(count);
        Data(await controller.PollSignIn(new() { SessionId = sessionB }, default)).GetProperty("status").GetString().Should().Be("pending");
    }

    [Fact]
    public async Task Callback_parallel_polls_exchange_once_and_publish_one_final_account()
    {
        var controller = Controller();
        var start = Data(await controller.StartSignIn(new() { Vendor = "claude" }, default));
        var sessionId = start.GetProperty("sessionId").GetString()!;
        var uri = new Uri(start.GetProperty("authorizeUrl").GetString()!);
        var state = System.Web.HttpUtility.ParseQueryString(uri.Query).Get("state");
        var callback = "http://localhost:54545/callback?code=test&state=" + Uri.EscapeDataString(state!);
        var polls = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => controller.PollSignIn(
            new() { SessionId = sessionId, CallbackUrl = callback }, default)));
        polls.Select(p => Data(p).GetProperty("accountId").GetGuid()).Distinct().Should().ContainSingle();
        _transport.Sends.Count(s => s.Path.Contains("/token")).Should().Be(1);
        (await _db.DeviceOAuthAccounts.ToListAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task No_site_credential_refresh_uses_exact_account_and_network_failure_is_not_reauth()
    {
        var a = Account("qoder", "A");
        var b = Account("qoder", "B");
        await _db.InsertAsync(a);
        await _db.InsertAsync(b);
        var refresh = RefreshService();
        (await refresh.RefreshForAccountAsync(b.Id, "B", default)).Should().Be("new-token");
        _transport.Sends.Should().ContainSingle(s => s.Scope == Scope(b));
        _transport.Sends.Should().NotContain(s => s.Scope == Scope(a));
        (await _db.DeviceOAuthAccounts.InSingleAsync(a.Id)).AccessToken.Should().Be("A");
        _resolver.RevokedAccounts.Add(a.Id);
        await refresh.Invoking(r => r.RefreshForAccountAsync(a.Id, "A", default)).Should().ThrowAsync<NetworkPolicyException>();
        (await _db.DeviceOAuthAccounts.InSingleAsync(a.Id)).RequiresReauth.Should().BeFalse();
        (await refresh.RefreshAsync(Guid.Empty, "A", default)).Should().BeNull();
    }

    [Fact]
    public async Task Approved_rotating_refresh_persists_after_revocation_during_response_and_next_send_is_blocked()
    {
        var account = Account("qoder", "old-token");
        await _db.InsertAsync(account);
        var bodyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.ResponseContent = () => new PausedContent(bodyStarted, releaseBody,
            """{"token":"rotated-token","refresh_token":"rotated-refresh"}""");
        var refresh = RefreshService();
        var pending = refresh.RefreshForAccountAsync(account.Id, "old-token", CancellationToken.None);
        var checksAtResponse = 0;
        try
        {
            await bodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            checksAtResponse = _resolver.Accounts.Count;
            _resolver.RevokedAccounts.Add(account.Id);
        }
        finally { releaseBody.TrySetResult(); }

        (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("rotated-token");
        _resolver.Accounts.Count.Should().Be(checksAtResponse,
            "an approved successful refresh must not validate again before persisting its rotated credentials");
        var saved = await _db.DeviceOAuthAccounts.InSingleAsync(account.Id);
        saved.AccessToken.Should().Be("rotated-token");
        saved.RefreshToken.Should().Be("rotated-refresh");
        saved.RequiresReauth.Should().BeFalse();
        _transport.Sends.Should().ContainSingle();
        var previousChecks = _resolver.Accounts.Count;
        await refresh.Invoking(r => r.RefreshForAccountAsync(account.Id, "rotated-token", CancellationToken.None))
            .Should().ThrowAsync<NetworkPolicyException>();
        _resolver.Accounts.Count.Should().BeGreaterThan(previousChecks);
        _transport.Sends.Should().ContainSingle("the next operation must fail before any further HTTP request");
    }

    [Fact]
    public async Task Bound_vendor_request_budget_cancels_stalled_body_even_with_no_caller_cancellation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.ResponseContent = () => new PausedContent(started, neverReleased, "{}");
        var shortBudgetFactory = new NetworkBoundDeviceOAuthVendorFactory(
            new AccountNetworkClientFactory(_resolver, _transport), _resolver, _registry,
            NullLogger<MiniMaxOAuthVendor>.Instance, TimeSpan.FromMilliseconds(100));
        using var bound = shortBudgetFactory.ForAccount(Account("copilot", "token"), "models-timeout");
        var call = bound.Vendor.FetchModelsAsync(new() { AccessToken = "token" }, null, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ((Func<Task>)(async () => { await call.WaitAsync(TimeSpan.FromSeconds(5)); }))
            .Should().ThrowAsync<OperationCanceledException>();
        _transport.Sends.Should().ContainSingle();
        neverReleased.Task.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Manual_quota_401_retry_without_site_keeps_one_account_identity()
    {
        var account = Account("qoder", "stale");
        var other = Account("qoder", "other");
        await _db.InsertAsync(account);
        await _db.InsertAsync(other);
        var quota = new DeviceOAuthQuotaService(_db, _registry, Cache(),
            _services.GetRequiredService<IMemoryCache>(), RefreshService(),
            NullLogger<DeviceOAuthQuotaService>.Instance, networkVendors: _factory);
        var result = await quota.ForceRefreshAsync(account.Id, default);
        result.Success.Should().BeTrue();
        _transport.Sends.Should().HaveCount(3);
        _transport.Sends.Should().OnlyContain(s => s.Scope == Scope(account));
        (await _db.DeviceOAuthAccounts.InSingleAsync(other.Id)).AccessToken.Should().Be("other");
        _resolver.RevokedAccounts.Add(account.Id);
        await quota.Invoking(q => q.ForceRefreshAsync(account.Id, default)).Should().ThrowAsync<NetworkPolicyException>();
        (await _db.DeviceOAuthAccounts.InSingleAsync(account.Id)).RequiresReauth.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Background_401_and_manual_refresh_share_one_rotation_and_reload_after_wait(bool backgroundFirst)
    {
        var site = new AITool.Domain.Sites.Site { Name = "managed", BaseUrl = "https://example.test", ApiKey = "stale", ManagedSource = DeviceOAuthAccountProvisioner.ManagedSource };
        await _db.InsertAsync(site);
        var account = Account("qoder", "stale");
        account.LinkedSiteId = site.Id;
        account.TokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _db.InsertAsync(account);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.ResponseContent = () => new PausedContent(started, release,
            """{"token":"one-rotation","refresh_token":"one-refresh"}""");
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddMemoryCache();
        registrations.AddSqlSugar($"Data Source={_path}");
        registrations.AddScoped<ProxyRequestMetadataCache>();
        registrations.AddSingleton(_registry);
        registrations.AddSingleton(_factory);
        using var services = registrations.BuildServiceProvider();
        var worker = new DeviceOAuthTokenRefreshService(services, NullLogger<DeviceOAuthTokenRefreshService>.Instance);
        var refresh = RefreshService();
        Task background;
        Task<string?> manual;
        Task<string?> on401;
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedCount = 0;
        void OnQueued(Guid id)
        {
            if (id == account.Id && Interlocked.Increment(ref queuedCount) == 2) queued.TrySetResult();
        }
        DeviceOAuthRefreshGate.Queued += OnQueued;
        try
        {
        if (backgroundFirst)
        {
            background = worker.RefreshDueAccountsAsync(default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            manual = refresh.RefreshForAccountAsync(account.Id, "stale", default);
            on401 = refresh.RefreshAsync(site.Id, "stale", default);
        }
        else
        {
            manual = refresh.RefreshForAccountAsync(account.Id, "stale", default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            background = worker.RefreshDueAccountsAsync(default);
            on401 = refresh.RefreshAsync(site.Id, "stale", default);
        }
        try
        {
            await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _transport.Sends.Should().ContainSingle("both contenders have actually queued for the account lease");
            background.IsCompleted.Should().BeFalse();
            manual.IsCompleted.Should().BeFalse();
            on401.IsCompleted.Should().BeFalse();
            // The approved exchange must persist even when later callers can no longer send.
            _resolver.RevokedAccounts.Add(account.Id);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(background, manual, on401).WaitAsync(TimeSpan.FromSeconds(5));
        (await manual).Should().Be("one-rotation");
        (await on401).Should().Be("one-rotation");
        var persisted = await _db.DeviceOAuthAccounts.InSingleAsync(account.Id);
        persisted.AccessToken.Should().Be("one-rotation");
        persisted.RefreshToken.Should().Be("one-refresh");
        persisted.RequiresReauth.Should().BeFalse();
        (await _db.Sites.InSingleAsync(site.Id)).ApiKey.Should().Be("one-rotation");
        _transport.Sends.Should().ContainSingle("waiters must reload and skip the already consumed refresh token");
        await refresh.Invoking(r => r.RefreshForAccountAsync(account.Id, "one-rotation", default))
            .Should().ThrowAsync<NetworkPolicyException>();
        _transport.Sends.Should().ContainSingle();
        }
        finally
        {
            release.TrySetResult();
            DeviceOAuthRefreshGate.Queued -= OnQueued;
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Site_key_write_fault_keeps_rotation_and_repairs_without_second_exchange(bool background, bool siteEnabled)
    {
        var site = new AITool.Domain.Sites.Site
        {
            Name = "managed", BaseUrl = "https://example.test", ApiKey = "stale", IsEnabled = siteEnabled,
            ManagedSource = DeviceOAuthAccountProvisioner.ManagedSource
        };
        var account = Account("qoder", "stale");
        account.LinkedSiteId = site.Id;
        account.TokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _db.InsertAsync(site);
        await _db.InsertAsync(account);
        await _db.InsertAsync(new AITool.Domain.Proxy.ProxyRouteRule
        {
            SiteId = site.Id, ExternalModelName = "test", UpstreamModelName = "test", SiteModelName = "test"
        });
        var initialRoutes = await Cache().GetAllRouteTargetsAsync(default);
        if (siteEnabled) initialRoutes.Should().ContainSingle(r => r.ApiKey == "stale");
        else initialRoutes.Should().BeEmpty();
        await _db.Client.Ado.ExecuteCommandAsync("CREATE TRIGGER fail_device_key BEFORE UPDATE OF ApiKey ON Sites BEGIN SELECT RAISE(ABORT, 'injected site key failure'); END;");
        var refresh = RefreshService();
        if (background)
        {
            var registrations = new ServiceCollection();
            registrations.AddLogging();
            registrations.AddMemoryCache();
            registrations.AddSqlSugar($"Data Source={_path}");
            registrations.AddScoped<ProxyRequestMetadataCache>();
            registrations.AddSingleton(_registry);
            registrations.AddSingleton(_factory);
            using var services = registrations.BuildServiceProvider();
            var worker = new DeviceOAuthTokenRefreshService(services, NullLogger<DeviceOAuthTokenRefreshService>.Instance);
            await worker.RefreshDueAccountsAsync(default);
            // The separate provider has a separate memory cache.
        }
        else
            (await refresh.RefreshAsync(site.Id, "stale", default)).Should().BeNull();

        var saved = await _db.DeviceOAuthAccounts.InSingleAsync(account.Id);
        saved.AccessToken.Should().Be("new-token");
        saved.RefreshToken.Should().Be("new-refresh");
        var siteAfterFailure = await _db.Sites.InSingleAsync(site.Id);
        siteAfterFailure.ApiKey.Should().Be("stale");
        var resolver = new ForwardCredentialResolver(_services.GetRequiredService<IServiceScopeFactory>());
        using (var credential = await resolver.ResolveAsync(ForwardRequest(site, account), default))
        {
            credential.ApiKey.Should().Be("new-token", "the send boundary must not use the stale site snapshot");
            credential.IsDeviceOAuth.Should().BeTrue();
        }
        siteAfterFailure.IsEnabled.Should().Be(siteEnabled, "credential publication must not change user-controlled enablement");
        // Warm the post-failure route snapshot to exercise repair invalidation.
        var failedRoutes = await Cache().GetAllRouteTargetsAsync(default);
        if (siteEnabled) failedRoutes.Should().ContainSingle(r => r.ApiKey == "stale");
        else failedRoutes.Should().BeEmpty();
        (await refresh.RefreshAsync(site.Id, "stale", default)).Should().BeNull("a failed repair must not return a token");
        _transport.Sends.Should().ContainSingle();
        await _db.Client.Ado.ExecuteCommandAsync("DROP TRIGGER fail_device_key;");
        (await refresh.RefreshAsync(site.Id, "stale", default)).Should().Be("new-token");
        (await _db.Sites.InSingleAsync(site.Id)).ApiKey.Should().Be("new-token");
        (await _db.Sites.InSingleAsync(site.Id)).IsEnabled.Should().Be(siteEnabled);
        var repairedRoutes = await Cache().GetAllRouteTargetsAsync(default);
        if (siteEnabled) repairedRoutes.Should().ContainSingle(r => r.ApiKey == "new-token");
        else repairedRoutes.Should().BeEmpty();
        _transport.Sends.Should().ContainSingle("the consumed refresh token must never be sent again");
    }

    [Fact]
    public async Task Site_refresh_rechecks_owner_after_wait_and_never_returns_another_binding_token()
    {
        var site = new AITool.Domain.Sites.Site { Name = "managed", BaseUrl = "https://example.test", ApiKey = "old",
            ManagedSource = DeviceOAuthAccountProvisioner.ManagedSource };
        var account = Account("qoder", "new");
        account.LinkedSiteId = site.Id;
        await _db.InsertAsync(site);
        await _db.InsertAsync(account);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnQueued(Guid id) { if (id == account.Id) queued.TrySetResult(); }
        DeviceOAuthRefreshGate.Queued += OnQueued;
        Task<string?>? pendingRefresh = null;
        try
        {
            using (await DeviceOAuthRefreshGate.AcquireAsync(account.Id, default))
            {
                var pending = RefreshService().RefreshAsync(site.Id, "old", default);
                await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using var client = _db.Client.CopyNew();
                await client.Updateable<DeviceOAuthAccount>()
                    .SetColumns(a => new DeviceOAuthAccount { LinkedSiteId = Guid.Empty })
                    .Where(a => a.Id == account.Id).ExecuteCommandAsync();
                // Release the lease before awaiting the queued operation.
                pendingRefresh = pending;
            }
            (await pendingRefresh!.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeNull();
            (await _db.Sites.InSingleAsync(site.Id)).ApiKey.Should().Be("old");
            _transport.Sends.Should().BeEmpty();
        }
        finally { DeviceOAuthRefreshGate.Queued -= OnQueued; }
    }

    [Fact]
    public async Task Send_credential_resolver_uses_live_owner_and_blocks_orphans_and_ambiguous_site()
    {
        var site = new AITool.Domain.Sites.Site { Name = "managed", BaseUrl = "https://example.test", ApiKey = "stale",
            ManagedSource = DeviceOAuthAccountProvisioner.ManagedSource };
        var account = Account("qoder", "live-token");
        account.LinkedSiteId = site.Id;
        await _db.InsertAsync(site);
        await _db.InsertAsync(account);
        var resolver = new ForwardCredentialResolver(_services.GetRequiredService<IServiceScopeFactory>());
        var request = ForwardRequest(site, account);
        using (var live = await resolver.ResolveAsync(request))
        {
            live.ApiKey.Should().Be("live-token");
            live.IsDeviceOAuth.Should().BeTrue();
        }
        var google = new AITool.Domain.Google.GoogleAccount { LinkedSiteId = site.Id };
        await _db.InsertAsync(google);
        await resolver.Invoking(r => r.ResolveAsync(request)).Should().ThrowAsync<NetworkPolicyException>();
        await _db.Client.Deleteable<AITool.Domain.Google.GoogleAccount>().Where(a => a.Id == google.Id).ExecuteCommandAsync();
        await _db.Client.Deleteable<DeviceOAuthAccount>().Where(a => a.Id == account.Id).ExecuteCommandAsync();
        await resolver.Invoking(r => r.ResolveAsync(request)).Should().ThrowAsync<NetworkPolicyException>();
        _transport.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelled_waiter_never_refreshes_and_releases_its_gate_reference()
    {
        var account = Account("qoder", "old");
        await _db.InsertAsync(account);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnQueued(Guid id) { if (id == account.Id) queued.TrySetResult(); }
        DeviceOAuthRefreshGate.Queued += OnQueued;
        using var cancellation = new CancellationTokenSource();
        try
        {
            using (await DeviceOAuthRefreshGate.AcquireAsync(account.Id, default))
            {
                var pending = RefreshService().RefreshForAccountAsync(account.Id, "old", cancellation.Token);
                await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancellation.Cancel();
                await ((Func<Task>)(async () => { await pending; })).Should().ThrowAsync<OperationCanceledException>();
                _transport.Sends.Should().BeEmpty();
            }
            using var next = await DeviceOAuthRefreshGate.AcquireAsync(account.Id, default).WaitAsync(TimeSpan.FromSeconds(5));
            (await _db.DeviceOAuthAccounts.InSingleAsync(account.Id)).RefreshToken.Should().Be("refresh-old");
        }
        finally { DeviceOAuthRefreshGate.Queued -= OnQueued; }
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("google")]
    [InlineData("kimi")]
    [InlineData("xai")]
    public async Task Site_repair_rechecks_cross_provider_owner_after_wait_and_refuses_shared_site(string provider)
    {
        var site = new AITool.Domain.Sites.Site { Name = "managed", BaseUrl = "https://example.test", ApiKey = "old",
            ManagedSource = DeviceOAuthAccountProvisioner.ManagedSource };
        var account = Account("qoder", "rotated");
        account.LinkedSiteId = site.Id;
        await _db.InsertAsync(site);
        await _db.InsertAsync(account);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnQueued(Guid id) { if (id == account.Id) queued.TrySetResult(); }
        DeviceOAuthRefreshGate.Queued += OnQueued;
        Task<string?> pending;
        using var refreshScope = _services.CreateScope();
        try
        {
            using (await DeviceOAuthRefreshGate.AcquireAsync(account.Id, default))
            {
                var refreshDb = refreshScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var refresh = new DeviceOAuthCredentialRefreshService(refreshDb, _registry, Cache(),
                    NullLogger<DeviceOAuthCredentialRefreshService>.Instance, _factory);
                pending = refresh.RefreshAsync(site.Id, "old", default);
                await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
                // Publish the conflicting owner after the caller found the Device owner and
                // demonstrably queued; the repair must query ownership only after acquiring it.
                switch (provider)
                {
                    case "codex": await _db.InsertAsync(new AITool.Domain.Codex.CodexAccount { LinkedSiteId = site.Id }); break;
                    case "google": await _db.InsertAsync(new AITool.Domain.Google.GoogleAccount { LinkedSiteId = site.Id }); break;
                    case "kimi": await _db.InsertAsync(new AITool.Domain.Kimi.KimiAccount { LinkedSiteId = site.Id }); break;
                    case "xai": await _db.InsertAsync(new AITool.Domain.Xai.XaiAccount { LinkedSiteId = site.Id }); break;
                }
            }
            (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeNull();
            (await _db.Sites.InSingleAsync(site.Id)).ApiKey.Should().Be("old");
            (await _db.DeviceOAuthAccounts.InSingleAsync(account.Id)).AccessToken.Should().Be("rotated");
            _transport.Sends.Should().BeEmpty();
        }
        finally { DeviceOAuthRefreshGate.Queued -= OnQueued; }
    }

    [Theory]
    [InlineData("kimi")]
    [InlineData("xai")]
    [InlineData("device")]
    public async Task Site_repair_ignores_soft_deleted_owners_consistent_with_network_policy_store(string provider)
    {
        var site = new AITool.Domain.Sites.Site { Name = "managed", BaseUrl = "https://example.test", ApiKey = "old",
            ManagedSource = DeviceOAuthAccountProvisioner.ManagedSource };
        var account = Account("qoder", "rotated");
        account.LinkedSiteId = site.Id;
        await _db.InsertAsync(site);
        await _db.InsertAsync(account);
        switch (provider)
        {
            case "kimi": await _db.InsertAsync(new AITool.Domain.Kimi.KimiAccount { LinkedSiteId = site.Id, IsDeleted = true }); break;
            case "xai": await _db.InsertAsync(new AITool.Domain.Xai.XaiAccount { LinkedSiteId = site.Id, IsDeleted = true }); break;
            case "device":
                var deleted = Account("qoder", "deleted");
                deleted.LinkedSiteId = site.Id;
                deleted.IsDeleted = true;
                await _db.InsertAsync(deleted);
                break;
        }
        (await RefreshService().RefreshAsync(site.Id, "old", default)).Should().Be("rotated");
        (await _db.Sites.InSingleAsync(site.Id)).ApiKey.Should().Be("rotated");
        _transport.Sends.Should().BeEmpty();
    }

    [Fact]
    public async Task Relogin_waits_for_active_rotation_before_replacing_credentials()
    {
        var account = Account("qoder", "old");
        account.ExternalUserId = "same-user";
        await _db.InsertAsync(account);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.ResponseContent = () => new PausedContent(started, release,
            """{"token":"rotated","refresh_token":"rotated-refresh"}""");
        void OnQueued(Guid id) { if (id == account.Id) queued.TrySetResult(); }
        DeviceOAuthRefreshGate.Queued += OnQueued;
        try
        {
            var refresh = RefreshService().RefreshForAccountAsync(account.Id, "old", default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var provisioner = new DeviceOAuthAccountProvisioner(db, Cache(), new SiteCascadeDeleter(db),
                NullLogger<DeviceOAuthAccountProvisioner>.Instance, _store);
            var relogin = provisioner.ProvisionAsync(_registry.Find("qoder")!, "name", null, "same-user",
                new() { AccessToken = "login-token", RefreshToken = "login-refresh" }, null, default);
            await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            relogin.IsCompleted.Should().BeFalse();
            release.TrySetResult();
            (await refresh.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("rotated");
            (await relogin.WaitAsync(TimeSpan.FromSeconds(5))).Id.Should().Be(account.Id);
            var final = await _db.DeviceOAuthAccounts.InSingleAsync(account.Id);
            final.AccessToken.Should().Be("login-token");
            final.RefreshToken.Should().Be("login-refresh");
        }
        finally { release.TrySetResult(); DeviceOAuthRefreshGate.Queued -= OnQueued; }
    }

    [Fact]
    public async Task Deletion_waits_for_running_refresh_then_removes_account_and_site()
    {
        var site = new AITool.Domain.Sites.Site { Name = "managed", BaseUrl = "https://example.test", ApiKey = "old",
            ManagedSource = DeviceOAuthAccountProvisioner.ManagedSource };
        var account = Account("qoder", "old");
        account.LinkedSiteId = site.Id;
        await _db.InsertAsync(site);
        await _db.InsertAsync(account);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.ResponseContent = () => new PausedContent(started, release,
            """{"token":"new-token","refresh_token":"new-refresh"}""");
        void OnQueued(Guid id) { if (id == account.Id) queued.TrySetResult(); }
        DeviceOAuthRefreshGate.Queued += OnQueued;
        try
        {
            var refresh = RefreshService().RefreshForAccountAsync(account.Id, "old", default);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var deletionScope = _services.CreateScope();
            var deletionDb = deletionScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var provisioner = new DeviceOAuthAccountProvisioner(deletionDb, Cache(), new SiteCascadeDeleter(deletionDb),
                NullLogger<DeviceOAuthAccountProvisioner>.Instance, _store);
            var deletion = provisioner.DeleteAsync(account.Id, default);
            await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            deletion.IsCompleted.Should().BeFalse();
            release.TrySetResult();
            (await refresh.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("new-token");
            await deletion.WaitAsync(TimeSpan.FromSeconds(5));
            (await _db.DeviceOAuthAccounts.InSingleAsync(account.Id)).Should().BeNull();
            (await _db.Sites.InSingleAsync(site.Id)).Should().BeNull();
            _transport.Sends.Should().ContainSingle();
        }
        finally { release.TrySetResult(); DeviceOAuthRefreshGate.Queued -= OnQueued; }
    }

    [Fact]
    public async Task Background_refresh_is_account_bound_and_network_revoke_does_not_require_reauth()
    {
        var account = Account("qoder", "due");
        account.TokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _db.InsertAsync(account);
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddMemoryCache();
        registrations.AddSqlSugar($"Data Source={_path}");
        registrations.AddScoped<ProxyRequestMetadataCache>();
        registrations.AddSingleton(_registry);
        registrations.AddSingleton(_factory);
        using var services = registrations.BuildServiceProvider();
        var worker = new DeviceOAuthTokenRefreshService(services, NullLogger<DeviceOAuthTokenRefreshService>.Instance);
        await worker.RefreshDueAccountsAsync(default);
        _transport.Sends.Should().ContainSingle(s => s.Scope == Scope(account));
        var blocked = Account("qoder", "blocked");
        blocked.TokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _db.InsertAsync(blocked);
        _resolver.RevokedAccounts.Add(blocked.Id);
        await worker.RefreshDueAccountsAsync(default);
        (await _db.DeviceOAuthAccounts.InSingleAsync(blocked.Id)).RequiresReauth.Should().BeFalse();
        _transport.Sends.Should().NotContain(s => s.Scope == Scope(blocked));
    }

    [Fact]
    public async Task Provision_dedup_preserves_unspecified_policy_updates_explicit_and_delete_cleans_atomically()
    {
        var provisioner = Provisioner();
        var vendor = _registry.Find("qoder")!;
        var first = await provisioner.ProvisionAsync(vendor, "name", "u@example.test", "user", Token(Account("qoder", "A")),
            null, default, networkSelection: new(NetworkMode.Direct));
        var again = await provisioner.ProvisionAsync(vendor, "name", "u@example.test", "user", Token(first), null, default);
        again.Id.Should().Be(first.Id);
        (await _store.GetAsync(NetworkBoundDeviceOAuthVendorFactory.Identity(first.Id))).Selection.Mode.Should().Be(NetworkMode.Direct);
        await provisioner.ProvisionAsync(vendor, "name", "u@example.test", "user", Token(first), null, default,
            networkSelection: new(NetworkMode.Legacy));
        (await _store.GetAsync(NetworkBoundDeviceOAuthVendorFactory.Identity(first.Id))).Selection.Mode.Should().Be(NetworkMode.Legacy);
        await provisioner.DeleteAsync(first.Id, default);
        (await _db.Client.Queryable<AccountNetworkPolicy>().CountAsync()).Should().Be(0);
        (await _db.DeviceOAuthAccounts.ToListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Explicit_selection_without_factory_and_failed_policy_provision_never_fall_back()
    {
        var controller = Controller(bound: false);
        (await controller.StartSignIn(new() { Vendor = "copilot", NetworkSelection = new(NetworkMode.Direct) }, default))
            .Should().BeOfType<BadRequestObjectResult>();
        _transport.Sends.Should().BeEmpty();
        var provisioner = Provisioner();
        await provisioner.Invoking(p => p.ProvisionAsync(_registry.Find("qoder")!, "name", null, "new-user",
            new() { AccessToken = "secret" }, null, default, networkSelection: new(NetworkMode.ProxyProfile, Guid.NewGuid()),
            expectedProfileRevision: 7)).Should().ThrowAsync<NetworkPolicyException>();
        (await _db.DeviceOAuthAccounts.ToListAsync()).Should().BeEmpty();
    }

    private DeviceOAuthAccountProvisioner Provisioner() => new(_db, Cache(), new SiteCascadeDeleter(_db),
        NullLogger<DeviceOAuthAccountProvisioner>.Instance, _store);
    private ProxyRequestMetadataCache Cache() => _scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
    private DeviceOAuthCredentialRefreshService RefreshService() => new(_db, _registry, Cache(),
        NullLogger<DeviceOAuthCredentialRefreshService>.Instance, _factory);
    private DeviceOAuthAccountsApiController Controller(bool bound = true) => new(_registry, Provisioner(),
        new(_db, _registry, Cache(), _services.GetRequiredService<IMemoryCache>(), RefreshService(),
            NullLogger<DeviceOAuthQuotaService>.Instance, networkVendors: _factory), _db, bound ? _factory : null);
    private static DeviceOAuthAccount Account(string vendor, string token) => new()
    {
        VendorKey = vendor, DisplayName = token, AccessToken = token, RefreshToken = "refresh-" + token,
        LinkedSiteId = Guid.Empty, IsEnabled = true
    };
    private static DeviceOAuthTokenSet Token(DeviceOAuthAccount a) => new() { AccessToken = a.AccessToken!, RefreshToken = a.RefreshToken };
    private static ProxyForwardRequest ForwardRequest(AITool.Domain.Sites.Site site, DeviceOAuthAccount account) => new()
    {
        SiteId = site.Id, ManagedSource = DeviceOAuthAccountProvisioner.ManagedSource,
        TargetBaseUrl = site.BaseUrl, TargetApiKey = site.ApiKey,
        AccountIdentity = NetworkBoundDeviceOAuthVendorFactory.Identity(account.Id)
    };
    private static string Scope(DeviceOAuthAccount a) => $"device-oauth:{a.Id:D}";
    private static JsonElement Data(IActionResult result) => JsonSerializer.SerializeToElement(((OkObjectResult)result).Value,
        new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("data");

    private sealed class RecordingResolver : IAccountNetworkPolicyResolver
    {
        public ConcurrentBag<AccountIdentity> Accounts { get; } = [];
        public ConcurrentBag<NetworkOperationContext> Operations { get; } = [];
        public HashSet<Guid> RevokedAccounts { get; } = [];
        public HashSet<string> RevokedSessions { get; } = [];
        public Task<ResolvedNetworkConfiguration> ResolveForAccountAsync(AccountIdentity account, CancellationToken cancellationToken = default)
        {
            Accounts.Add(account);
            if (RevokedAccounts.Contains(account.AccountId)) throw new NetworkPolicyException("proxy_profile_disabled", "Profile disabled.");
            return Task.FromResult(ResolvedNetworkConfiguration.Direct);
        }
        public Task<ResolvedNetworkConfiguration> ResolveAsync(NetworkOperationContext operation, CancellationToken cancellationToken = default)
        {
            // The initial unpinned resolution is not a vendor operation.
            if (operation.Selection.Mode != NetworkMode.ProxyProfile || operation.ExpectedProfileRevision is not null)
                Operations.Add(operation);
            if (RevokedSessions.Contains(operation.IsolationKey)) throw new NetworkPolicyException("proxy_profile_revision_conflict", "Profile changed.");
            return Task.FromResult(new ResolvedNetworkConfiguration
            {
                Mode = operation.Selection.Mode, ProxyProfileId = operation.Selection.ProxyProfileId, ProfileRevision = 7
            });
        }
    }
    private sealed record Send(string Scope, string Path, string? Authorization, string Host);
    private sealed record Acquisition(string Scope, string Purpose, NetworkMode Mode);
    private sealed class RecordingTransport : INetworkTransport
    {
        public ConcurrentBag<Send> Sends { get; } = [];
        public ConcurrentBag<Acquisition> Acquisitions { get; } = [];
        public Func<HttpContent>? ResponseContent { get; set; }
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public ValueTask<INetworkTransportLease> AcquireAsync(ResolvedNetworkConfiguration configuration, string isolationScope,
            string purpose, CancellationToken cancellationToken = default)
        {
            Acquisitions.Add(new Acquisition(isolationScope, purpose, configuration.Mode));
            return ValueTask.FromResult<INetworkTransportLease>(new Lease(new HttpClient(new Handler(this, isolationScope))));
        }
        private sealed class Lease(HttpClient client) : INetworkTransportLease
        {
            public HttpClient Client => client;
            public ValueTask DisposeAsync() { client.Dispose(); return ValueTask.CompletedTask; }
        }
        private sealed class Handler(RecordingTransport owner, string scope) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                owner.Sends.Add(new Send(scope, path, request.Headers.Authorization?.ToString(), request.RequestUri.Host));
                if (path.Contains("/usage") && request.Headers.Authorization?.Parameter == "stale")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
                var json = path.Contains("/usage") ? """{"displayMode":"enterprise"}"""
                    : path.Contains("device/code")
                    ? """{"device_code":"opaque","user_code":"1234","verification_uri":"https://example.test/device","expires_in":900}"""
                    : path.Contains("deviceToken/refresh") ? """{"token":"new-token","refresh_token":"new-refresh"}"""
                    : path.Contains("/token") && request.RequestUri.Host.Contains("anthropic")
                        ? """{"access_token":"claude-token","refresh_token":"refresh","expires_in":3600,"account":{"email_address":"u@example.test"}}"""
                    : path.Contains("access_token") || path.Contains("/token") ? """{"error":"authorization_pending"}"""
                    : """{"data":[{"id":"model-a","name":"A","capabilities":{"type":"chat"},"model_picker_enabled":true,"policy":{"state":"enabled"}}]}""";
                return Task.FromResult(new HttpResponseMessage(owner.StatusCode)
                {
                    Content = owner.ResponseContent?.Invoke() ?? new StringContent(json)
                });
            }
        }
    }
    private sealed class PausedContent(TaskCompletionSource started, TaskCompletionSource release, string json) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(json), cancellationToken);
        }
    }
    private sealed class ForbiddenHandler(Action? onSend = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onSend?.Invoke();
            throw new InvalidOperationException("Metadata registry must never perform remote I/O.");
        }
    }
    public void Dispose()
    {
        _scope.Dispose();
        _services.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_path + suffix); } catch { }
    }
}
