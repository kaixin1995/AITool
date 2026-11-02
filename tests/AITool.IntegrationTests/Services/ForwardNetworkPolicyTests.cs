using System.Net;
using System.Text;
using AITool.Application.Networking;
using AITool.Application.Proxy;
using AITool.Domain.Codex;
using AITool.Domain.Google;
using AITool.Domain.Models;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;

namespace AITool.IntegrationTests.Services;

/// <summary>Temporary SQLite and fake HTTP only: no production database or external network.</summary>
public sealed class ForwardNetworkPolicyTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"aitool-forward-policy-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;
    private readonly ISqlSugarClient _db;
    private readonly ProxyProfileSecretService _secrets = new(new EphemeralDataProtectionProvider());
    private readonly AccountNetworkPolicyStore _policies;
    private readonly ForwardNetworkPolicyResolver _resolver;

    public ForwardNetworkPolicyTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        // Includes CopyNew clients opened by the resolver/store: no fixture-owned pool
        // should keep this file locked or require clearing unrelated tests' SQLite pools.
        services.AddSqlSugar($"Data Source={_path};Pooling=False");
        services.AddSingleton(_secrets);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<AccountNetworkPolicyStore>();
        services.AddSingleton<IAccountNetworkPolicyStore>(sp => sp.GetRequiredService<AccountNetworkPolicyStore>());
        services.AddSingleton<ProxyRequestMetadataCache>();
        _services = services.BuildServiceProvider();
        _db = _services.GetRequiredService<ISqlSugarClient>();
        SqlSugarSetup.InitializeDatabase(_db);
        _db.Deleteable<SystemRuntimeSettings>().ExecuteCommand();
        _db.Insertable(new SystemRuntimeSettings { Id = 1, DeveloperProxyProfilesEnabled = true }).ExecuteCommand();
        _policies = _services.GetRequiredService<AccountNetworkPolicyStore>();
        _resolver = new(_services.GetRequiredService<IServiceScopeFactory>(), _policies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Typed_http_client_factory_resolves_forward_service_with_or_without_optional_network_services(bool registerNetworkServices)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (registerNetworkServices)
        {
            services.AddSingleton<INetworkTransport>(_ => new FakeTransport(_ => throw new InvalidOperationException("No send expected.")));
            services.AddSingleton<IForwardNetworkPolicyResolver>(new FakeResolver(() =>
                throw new InvalidOperationException("No policy resolution expected.")));
        }
        services.AddHttpClient<IProxyForwardService, ProxyForwardService>();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IProxyForwardService>().Should().BeOfType<ProxyForwardService>();
        provider.GetRequiredService<IProxyForwardService>().Should().BeOfType<ProxyForwardService>();
        typeof(ProxyForwardService).GetConstructors().Should().ContainSingle(
            "typed HttpClient activation must have exactly one applicable public constructor");
    }

    [Fact]
    public async Task Account_direct_and_profile_override_invalid_mapping_and_policy_is_live()
    {
        var account = AddAccount();
        var request = Request(account);
        request.EgressProxyUrl = "file:///never-send";
        await _policies.SetAsync(account, new(NetworkMode.Direct));
        var direct = await _resolver.ResolveAsync(request);
        direct.Configuration.Mode.Should().Be(NetworkMode.Direct);
        direct.UseDefaultClient.Should().BeFalse();
        var profile = AddProfile();
        await _policies.SetAsync(account, new(NetworkMode.ProxyProfile, profile.Id));
        var proxied = await _resolver.ResolveAsync(request);
        proxied.Configuration.ProxyProfileId.Should().Be(profile.Id);
        proxied.IsolationKey.Should().Be(direct.IsolationKey);
        await _policies.SetAsync(account, new(NetworkMode.Legacy));
        await Fails(() => _resolver.ResolveAsync(request), "invalid_egress_proxy");
    }

    [Fact]
    public async Task Profile_reference_and_old_key_read_live_secrets_endpoint_revision_and_enablement()
    {
        var profile = AddProfile();
        _secrets.TryApplyForSave(profile, profile.ProxyUrl, "Replace", "private-user", "private-password", out _).Should().BeTrue();
        _db.Updateable(profile).ExecuteCommand();
        var request = Request();
        request.EgressProxyUrl = $"profile:{profile.Id:D}";
        var resolved = (await _resolver.ResolveAsync(request)).Configuration;
        resolved.ProxyProfileId.Should().Be(profile.Id);
        resolved.ProxyEndpoint!.UserInfo.Should().BeEmpty();
        resolved.Credentials!.UserName.Should().Be("private-user");
        resolved.Credentials.Password.Should().Be("private-password");
        request.EgressProxyUrl = profile.Key;
        (await _resolver.ResolveAsync(request)).Configuration.ProxyProfileId.Should().Be(profile.Id);
        request.EgressProxyUrl = $"profile:{profile.Id:D}";
        profile.Key = "renamed";
        profile.ProxyUrl = "socks5://127.0.0.1:12346";
        profile.Revision++;
        _db.Updateable(profile).ExecuteCommand();
        var changed = (await _resolver.ResolveAsync(request)).Configuration;
        changed.ProfileRevision.Should().Be(profile.Revision);
        changed.ProxyEndpoint!.Scheme.Should().Be("socks5");
        profile.IsEnabled = false;
        _db.Updateable(profile).ExecuteCommand();
        await Fails(() => _resolver.ResolveAsync(request), "proxy_profile_disabled");
    }

    [Fact]
    public async Task Legacy_url_userinfo_is_decoded_separately_and_never_retained_in_endpoint()
    {
        var request = Request();
        request.EgressProxyUrl = "http://name%3Apart:p%40ss%3Aword+plus@127.0.0.1:12345";
        var first = (await _resolver.ResolveAsync(request)).Configuration;
        first.ProxyEndpoint!.UserInfo.Should().BeEmpty();
        first.Credentials!.UserName.Should().Be("name:part");
        first.Credentials.Password.Should().Be("p@ss:word+plus");
        var again = (await _resolver.ResolveAsync(request)).Configuration;
        again.ProxyProfileId.Should().Be(first.ProxyProfileId);
        request.EgressProxyUrl = "http://other:new-secret@127.0.0.1:12345";
        (await _resolver.ResolveAsync(request)).Configuration.ProxyProfileId.Should().NotBe(first.ProxyProfileId!.Value);
        request.EgressProxyUrl = "http://private:secret@127.0.0.1:12345/forbidden";
        var error = await Fails(() => _resolver.ResolveAsync(request), "invalid_egress_proxy");
        error.ToString().Should().NotContain("private").And.NotContain("secret");
    }

    [Fact]
    public async Task Global_off_preserves_ordinary_default_but_blocks_account_profile_and_keeps_direct()
    {
        var account = AddAccount();
        var profile = AddProfile();
        await _policies.SetAsync(account, new(NetworkMode.ProxyProfile, profile.Id));
        SetGlobal(false);
        var ordinary = Request();
        ordinary.EgressProxyUrl = $"profile:{profile.Id:D}";
        var defaultPolicy = await _resolver.ResolveAsync(ordinary);
        defaultPolicy.UseDefaultClient.Should().BeTrue();
        defaultPolicy.Configuration.Mode.Should().Be(NetworkMode.Legacy);
        await Fails(() => _resolver.ResolveAsync(Request(account)), "proxy_profiles_disabled");
        await _policies.SetAsync(account, new(NetworkMode.Direct));
        (await _resolver.ResolveAsync(Request(account))).Configuration.Mode.Should().Be(NetworkMode.Direct);
    }

    [Fact]
    public async Task No_selector_preserves_default_client_and_account_legacy_uses_isolated_transport()
    {
        (await _resolver.ResolveAsync(Request())).UseDefaultClient.Should().BeTrue();
        var account = AddAccount();
        var managed = await _resolver.ResolveAsync(Request(account));
        managed.UseDefaultClient.Should().BeFalse();
        managed.Configuration.Mode.Should().Be(NetworkMode.Legacy);
        var request = Request(account);
        request.EgressProxyUrl = "http://127.0.0.1:12345";
        (await _resolver.ResolveAsync(request)).Configuration.Mode.Should().Be(NetworkMode.ProxyProfile);
    }

    [Fact]
    public async Task Missing_resolver_blocks_before_prepare_or_send_but_old_constructor_still_serves_plain_requests()
    {
        var sends = 0;
        using var client = new HttpClient(new Handler(_ => { sends++; return JsonResponse(); }));
        var forward = new ProxyForwardService(client, NullLogger<ProxyForwardService>.Instance);
        var prepared = 0;
        var request = Request(new("codex", Guid.NewGuid()));
        request.PrepareTargetCredentialAsync = (_, _) => { prepared++; return Task.CompletedTask; };
        var blocked = await forward.ForwardAsync(request);
        blocked.StatusCode.Should().Be(503);
        blocked.ErrorMessage.Should().Contain("forward_network_resolver_unavailable");
        sends.Should().Be(0);
        prepared.Should().Be(0);
        (await forward.ForwardAsync(Request())).Success.Should().BeTrue();
        sends.Should().Be(1);
        request = Request();
        request.EgressProxyUrl = "http://user:secret@127.0.0.1:1";
        (await forward.ForwardAsync(request)).ErrorMessage.Should().NotContain("secret");
        sends.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revocation_after_401_blocks_refresh_callback_and_never_sends_default(bool streaming)
    {
        var siteId = Guid.NewGuid();
        var account = AddAccount(siteId);
        var credentials = new FakeCredentials(account, siteId);
        var profile = AddProfile();
        await _policies.SetAsync(account, new(NetworkMode.ProxyProfile, profile.Id));
        using var transport = new FakeTransport(_ =>
        {
            SetGlobal(false);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("unauthorized") };
        });
        using var defaultClient = new HttpClient(new Handler(_ => throw new InvalidOperationException("default client must not send")));
        var forward = new ProxyForwardService(defaultClient, NullLogger<ProxyForwardService>.Instance, transport, _resolver, credentials);
        var refreshes = 0;
        var request = Request(account, siteId);
        request.EnableStreaming = streaming;
        request.RefreshTargetApiKeyAsync = (_, _) => { refreshes++; return Task.FromResult<string?>("replacement"); };
        var result = streaming
            ? await forward.ForwardStreamingAsync(request, (_, _) => Task.CompletedTask)
            : await forward.ForwardAsync(request);
        result.StatusCode.Should().Be(503);
        result.ErrorMessage.Should().Contain("proxy_profiles_disabled");
        credentials.Calls.Should().Be(1);
        refreshes.Should().Be(0);
        transport.Sends.Should().Be(1);
        transport.Active.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Each_401_retry_re_resolves_policy_and_lease_outlives_response_body(bool streaming)
    {
        var calls = 0;
        var resolver = new FakeResolver(() =>
        {
            calls++;
            return new(ResolvedNetworkConfiguration.Direct, "account:test");
        });
        FakeTransport? transport = null;
        transport = new FakeTransport(send =>
        {
            if (send == 1) return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("unauthorized") };
            return streaming ? SseResponse() : JsonResponse();
        });
        using (transport)
        using (var defaultClient = new HttpClient(new Handler(_ => throw new InvalidOperationException("default send"))))
        {
            var forward = new ProxyForwardService(defaultClient, NullLogger<ProxyForwardService>.Instance, transport, resolver);
            var request = Request();
            request.EnableStreaming = streaming;
            request.RefreshTargetApiKeyAsync = (_, _) => Task.FromResult<string?>("new-key");
            var result = streaming
                ? await forward.ForwardStreamingAsync(request, (_, _) => { transport.Active.Should().Be(1); return Task.CompletedTask; })
                : await forward.ForwardAsync(request);
            result.Success.Should().BeTrue();
            calls.Should().Be(3, "first attempt, refresh preflight, and retry all resolve live policy");
            transport.Sends.Should().Be(2);
            transport.Active.Should().Be(0);
            transport.Leases.Should().OnlyContain(x => x.Disposed && x.ResponseDisposed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Output_reference_guard_failure_is_terminal_before_or_after_first_content(bool afterFirstContent)
    {
        var resolver = new FakeResolver(() => new(ResolvedNetworkConfiguration.Direct, "account:test"));
        using var transport = new FakeTransport(_ => SseResponse());
        using var defaultClient = new HttpClient(new Handler(_ => throw new InvalidOperationException("default send")));
        var forward = new ProxyForwardService(defaultClient, NullLogger<ProxyForwardService>.Instance, transport, resolver);
        var request = Request();
        request.EnableStreaming = true;
        request.RetryCount = 3;
        var expected = new UpstreamReferenceException("reference_commit_failed", "The upstream reference could not be committed.");
        var events = 0;
        var action = () => forward.ForwardStreamingAsync(request, (_, _) =>
        {
            events++;
            if (!afterFirstContent || events == 2) throw expected;
            return Task.CompletedTask;
        });

        var thrown = await action.Should().ThrowAsync<UpstreamReferenceException>();
        thrown.Which.Should().BeSameAs(expected);
        events.Should().Be(afterFirstContent ? 2 : 1);
        transport.Sends.Should().Be(1, "a failed output ownership commit must never replay an upstream request");
        transport.Active.Should().Be(0);
        transport.Leases.Should().OnlyContain(x => x.Disposed && x.ResponseDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Output_network_policy_failure_is_terminal_before_or_after_first_content(bool afterFirstContent)
    {
        var resolver = new FakeResolver(() => new(ResolvedNetworkConfiguration.Direct, "account:test"));
        using var transport = new FakeTransport(_ => SseResponse());
        using var defaultClient = new HttpClient(new Handler(_ => throw new InvalidOperationException("default send")));
        var forward = new ProxyForwardService(defaultClient, NullLogger<ProxyForwardService>.Instance, transport, resolver);
        var request = Request();
        request.EnableStreaming = true;
        request.RetryCount = 3;
        var events = 0;
        var result = await forward.ForwardStreamingAsync(request, (_, _) =>
        {
            events++;
            if (!afterFirstContent || events == 2)
                throw new NetworkPolicyException("proxy_profile_disabled", "The selected proxy profile is disabled.");
            return Task.CompletedTask;
        });

        result.Success.Should().BeFalse();
        result.StatusCode.Should().Be(503);
        result.ErrorMessage.Should().Contain("proxy_profile_disabled");
        events.Should().Be(afterFirstContent ? 2 : 1);
        transport.Sends.Should().Be(1);
        transport.Active.Should().Be(0);
        transport.Leases.Should().OnlyContain(x => x.Disposed && x.ResponseDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoked_policy_is_resolved_once_without_internal_retry(bool streaming)
    {
        var resolves = 0;
        var resolver = new FakeResolver(() =>
        {
            resolves++;
            throw new NetworkPolicyException("proxy_profile_disabled", "The selected proxy profile is disabled.");
        });
        using var transport = new FakeTransport(_ => JsonResponse());
        using var defaultClient = new HttpClient(new Handler(_ => throw new InvalidOperationException("default send")));
        var forward = new ProxyForwardService(defaultClient, NullLogger<ProxyForwardService>.Instance, transport, resolver);
        var request = Request();
        request.EnableStreaming = streaming;
        request.RetryCount = 3;
        var result = streaming
            ? await forward.ForwardStreamingAsync(request, (_, _) => Task.CompletedTask)
            : await forward.ForwardAsync(request);

        result.StatusCode.Should().Be(503);
        resolves.Should().Be(1);
        transport.Sends.Should().Be(0);
        transport.Leases.Should().BeEmpty();
    }

    [Fact]
    public async Task Policy_changed_by_401_refresh_is_re_read_before_retry_send()
    {
        var siteId = Guid.NewGuid();
        var account = AddAccount(siteId);
        var credentials = new FakeCredentials(account, siteId);
        var profile = AddProfile();
        await _policies.SetAsync(account, new(NetworkMode.ProxyProfile, profile.Id));
        using var transport = new FakeTransport(send => send == 1
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("unauthorized") }
            : JsonResponse());
        using var defaultClient = new HttpClient(new Handler(_ => throw new InvalidOperationException("default send")));
        var forward = new ProxyForwardService(defaultClient, NullLogger<ProxyForwardService>.Instance, transport, _resolver, credentials);
        var request = Request(account, siteId);
        request.RefreshTargetApiKeyAsync = async (_, ct) =>
        {
            await _policies.SetAsync(account, new(NetworkMode.Direct), cancellationToken: ct);
            return "new-key";
        };
        (await forward.ForwardAsync(request)).Success.Should().BeTrue();
        credentials.Calls.Should().Be(2);
        transport.Configurations.Select(c => c.Mode).Should().Equal(NetworkMode.ProxyProfile, NetworkMode.Direct);
        transport.Active.Should().Be(0);
    }

    [Fact]
    public async Task Preparation_is_preflighted_and_a_policy_change_before_retry_is_not_frozen()
    {
        var calls = 0;
        var prepared = 0;
        var resolver = new FakeResolver(() =>
        {
            calls++;
            if (calls >= 2) throw new NetworkPolicyException("proxy_profile_disabled", "Blocked.");
            return new(ResolvedNetworkConfiguration.Direct, "account:test");
        });
        using var transport = new FakeTransport(_ => JsonResponse());
        using var defaultClient = new HttpClient(new Handler(_ => throw new InvalidOperationException("default send")));
        var forward = new ProxyForwardService(defaultClient, NullLogger<ProxyForwardService>.Instance, transport, resolver);
        var request = Request();
        request.PrepareTargetCredentialAsync = (_, _) => { prepared++; return Task.CompletedTask; };
        var result = await forward.ForwardAsync(request);
        result.StatusCode.Should().Be(503);
        prepared.Should().Be(1);
        transport.Sends.Should().Be(0);
    }

    [Fact]
    public async Task Route_reference_clone_chat_and_fallback_preserve_owner_and_raw_profile_identity()
    {
        var profile = AddProfile();
        var marker = $"profile:{profile.Id:D}";
        var site = new Site { Name = "physical", BaseUrl = "https://never-called.invalid", ApiKey = "key", EgressProxyUrl = marker };
        var account = AddAccount(site.Id);
        var model = new ModelLibraryItem { ModelName = "upstream", DisplayName = "upstream" };
        var source = new ProxyRouteEntry { EntryName = "source" };
        _db.Insertable(site).ExecuteCommand();
        _db.Insertable(model).ExecuteCommand();
        _db.Insertable(new SiteModelMapping { SiteId = site.Id, ModelLibraryItemId = model.Id, RemoteModelName = "remote" }).ExecuteCommand();
        _db.Insertable(source).ExecuteCommand();
        _db.Insertable(new[]
        {
            new ProxyRouteRule { ExternalModelName = "source", SiteId = site.Id, UpstreamModelName = model.ModelName, SiteModelName = "remote" },
            new ProxyRouteRule { ExternalModelName = "parent", TargetKind = "RouteEntry", TargetRouteEntryId = source.Id }
        }).ExecuteCommand();
        var cache = _services.GetRequiredService<ProxyRequestMetadataCache>();
        var parent = (await cache.CaptureEffectiveRouteTargetsAsync("parent", CancellationToken.None)).Single();
        parent.AccountIdentity.Should().Be(account);
        parent.EgressProxyUrl.Should().Be(marker);
        parent.Clone().AccountIdentity.Should().Be(account);
        parent.Clone().EgressProxyUrl.Should().Be(marker);
        parent.SourceRouteEntryId.Should().Be(source.Id);
        (await cache.CaptureEffectiveRouteTargetsAsync("source", CancellationToken.None)).Single().AccountIdentity.Should().Be(account);
        var chat = (await cache.GetChatTargetsAsync(CancellationToken.None)).Single();
        chat.AccountIdentity.Should().Be(account);
        chat.EgressProxyUrl.Should().Be(marker);
        var fallback = await cache.GetFallbackTargetAsync(model.Id, CancellationToken.None);
        fallback!.AccountIdentity.Should().Be(account);
        fallback.EgressProxyUrl.Should().Be(marker);
        SetGlobal(false);
        cache.InvalidateRouteTargets();
        (await cache.CaptureEffectiveRouteTargetsAsync("parent", CancellationToken.None)).Single().EgressProxyUrl.Should().Be(marker);
    }

    [Fact]
    public async Task Ambiguous_eligible_owner_blocks_cache_instead_of_choosing_first()
    {
        var site = new Site { Name = "ambiguous", BaseUrl = "https://never-called.invalid", ApiKey = "key" };
        AddAccount(site.Id);
        _db.Insertable(new GoogleAccount { LinkedSiteId = site.Id }).ExecuteCommand();
        _db.Insertable(site).ExecuteCommand();
        _db.Insertable(new ProxyRouteRule { ExternalModelName = "route", SiteId = site.Id, SiteModelName = "model" }).ExecuteCommand();
        var cache = _services.GetRequiredService<ProxyRequestMetadataCache>();
        await Fails(() => cache.GetAllRouteTargetsAsync(CancellationToken.None), "ambiguous_account_owner");
    }

    private AccountIdentity AddAccount(Guid? siteId = null)
    {
        var account = new CodexAccount { LinkedSiteId = siteId ?? Guid.NewGuid() };
        _db.Insertable(account).ExecuteCommand();
        return new("codex", account.Id);
    }

    private ProxyProfile AddProfile()
    {
        var profile = new ProxyProfile { Key = Guid.NewGuid().ToString("N"), Name = "test", ProxyUrl = "http://127.0.0.1:12345", Revision = 1 };
        _db.Insertable(profile).ExecuteCommand();
        return profile;
    }

    private void SetGlobal(bool enabled) => _db.Updateable<SystemRuntimeSettings>()
        .SetColumns(s => s.DeveloperProxyProfilesEnabled == enabled).Where(s => s.Id == 1).ExecuteCommand();

    private static ProxyForwardRequest Request(AccountIdentity? account = null, Guid? siteId = null) => new()
    {
        AccountIdentity = account,
        SiteId = siteId,
        ManagedSource = siteId is null ? null : "codex",
        AllowNonSiteTarget = account is null && siteId is null,
        TargetBaseUrl = "https://never-called.invalid",
        TargetModelName = "test",
        RequestBody = "{\"model\":\"test\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}",
        RequestTimeoutSeconds = 30
    };

    private static HttpResponseMessage JsonResponse() => new(HttpStatusCode.OK)
    {
        Content = new TrackingContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}")
    };

    private static HttpResponseMessage SseResponse() => new(HttpStatusCode.OK)
    {
        Content = new TrackingContent("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":null}]}\n\ndata: [DONE]\n\n", "text/event-stream")
    };

    private static async Task<NetworkPolicyException> Fails(Func<Task> action, string code)
    {
        var assertion = await action.Should().ThrowAsync<NetworkPolicyException>();
        assertion.Which.Code.Should().Be(code);
        return assertion.Which;
    }

    public void Dispose()
    {
        _services.Dispose();
        // AddSqlSugar registers a pre-created singleton instance; the fixture owns its
        // lifetime. AppDbContext.Dispose is intentionally a no-op, so close it explicitly.
        _db.Dispose();
        // Never ClearAllPools: parallel shared-memory fixtures depend on their own pools.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Delete(_path + suffix);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(25);
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(25);
                }
            }
        }
    }

    private sealed class FakeCredentials(AccountIdentity account, Guid siteId) : IForwardCredentialResolver
    {
        public int Calls { get; private set; }

        public Task<ForwardCredentialLease> ResolveAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.AccountIdentity != account || request.SiteId != siteId || request.ManagedSource != "codex" || request.AllowNonSiteTarget)
                throw new NetworkPolicyException("forward_credential_invalid", "Unexpected credential context.");
            Calls++;
            return Task.FromResult(new ForwardCredentialLease(request.TargetApiKey));
        }
    }

    private sealed class FakeResolver(Func<ForwardNetworkPolicyResolution> resolve) : IForwardNetworkPolicyResolver
    {
        public Task<ForwardNetworkPolicyResolution> ResolveAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(resolve());
    }

    private sealed class Handler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _sends;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(++_sends));
    }

    private sealed class TrackingContent(string content, string mediaType = "application/json") : StringContent(content, Encoding.UTF8, mediaType)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class FakeTransport(Func<int, HttpResponseMessage> respond) : INetworkTransport, IDisposable
    {
        private HttpResponseMessage Respond() => respond(++Sends);
        public int Sends { get; private set; }
        public int Active { get; private set; }
        public List<Lease> Leases { get; } = [];
        public List<ResolvedNetworkConfiguration> Configurations { get; } = [];

        public ValueTask<INetworkTransportLease> AcquireAsync(ResolvedNetworkConfiguration configuration, string isolationScope,
            string purpose, CancellationToken cancellationToken = default)
        {
            Active++;
            Configurations.Add(configuration);
            var lease = new Lease(this);
            Leases.Add(lease);
            return ValueTask.FromResult<INetworkTransportLease>(lease);
        }

        public void Dispose() { foreach (var lease in Leases) lease.Client.Dispose(); }

        public sealed class Lease : INetworkTransportLease
        {
            private readonly FakeTransport _owner;
            private HttpResponseMessage? _response;
            public HttpClient Client { get; }
            public bool Disposed { get; private set; }
            public bool ResponseDisposed => _response?.Content is not TrackingContent tracked || tracked.Disposed;
            public Lease(FakeTransport owner)
            {
                _owner = owner;
                Client = new HttpClient(new Handler(_ =>
                {
                    _response = owner.Respond();
                    return _response;
                }));
            }
            public ValueTask DisposeAsync()
            {
                ResponseDisposed.Should().BeTrue("response content must be disposed before the transport lease");
                Disposed.Should().BeFalse();
                Disposed = true;
                _owner.Active--;
                return ValueTask.CompletedTask;
            }
        }
    }
}
