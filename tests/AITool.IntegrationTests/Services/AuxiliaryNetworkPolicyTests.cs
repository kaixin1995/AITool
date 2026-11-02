using System.Net;
using System.Reflection;
using AITool.Application.Networking;
using AITool.Application.Proxy;
using AITool.Application.Sites;
using AITool.Application.UsageLogs;
using AITool.Domain.Detection;
using AITool.Domain.Models;
using AITool.Domain.Operations;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Health;
using AITool.Infrastructure.OpenAI;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Infrastructure.Scheduling;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Nodes;
using AITool.Infrastructure.Sites;
using AITool.Web.Controllers.Admin;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITool.IntegrationTests.Services;

/// <summary>Only fake handlers and isolated temporary SQLite databases; never external HTTP.</summary>
public sealed class AuxiliaryNetworkPolicyTests
{
    [Theory]
    [InlineData("custom-balance")]
    [InlineData("deepseek")]
    [InlineData("zhipu")]
    public async Task Quota_providers_preserve_selector_and_lease_through_body(string providerKey)
    {
        using var fallback = new HttpClient(new NeverSend());
        using var transport = new CapturingTransport(providerKey switch
        {
            "custom-balance" => "{\"balance\":12}",
            "deepseek" => "{\"balance_infos\":[{\"currency\":\"USD\",\"total_balance\":\"12\"}]}",
            _ => "{\"success\":true,\"data\":{\"limits\":[{\"type\":\"TOKENS_LIMIT\",\"percentage\":12}]}}"
        });
        var policies = new CapturingPolicies();
        var provider = Provider(providerKey, fallback, policies, transport);
        var context = new SiteQuotaSiteContext("https://api.deepseek.com",
            new("https://quota.invalid/balance", "GET", "$.balance", "USD"), "profile:chosen");
        await provider.QueryAsync(context, "key-a", default);
        policies.Requests.Should().NotBeEmpty().And.OnlyContain(x => x.EgressProxyUrl == "profile:chosen");
        transport.Calls.Should().NotBeEmpty().And.OnlyContain(x => x.Configuration.Mode == NetworkMode.ProxyProfile);
        transport.ReadWhileLeased.Should().BeTrue();
        transport.Active.Should().Be(0);
        transport.Authorizations.Should().OnlyContain(x => x == "Bearer key-a" || x == "key-a");
    }

    [Fact]
    public async Task Explicit_bad_selector_never_uses_default_client()
    {
        using var fallback = new HttpClient(new NeverSend());
        var policies = new CapturingPolicies { Reject = true };
        using var transport = new CapturingTransport("{}");
        var provider = Provider("deepseek", fallback, policies, transport);
        var result = await provider.QueryAsync(new SiteQuotaSiteContext("https://api.deepseek.com", null, "missing-profile"), "key", default);
        result.Success.Should().BeFalse();
        policies.Requests.Should().ContainSingle();
        transport.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Account_quota_uses_live_account_factory_and_direct_transport_not_ordinary_selector()
    {
        using var fallback = new HttpClient(new NeverSend());
        using var transport = new CapturingTransport("{\"balance\":3}");
        var accounts = new AccountPolicies();
        var ordinary = new CapturingPolicies { Reject = true };
        var provider = new CustomBalanceSiteQuotaProvider(fallback, NullLogger<CustomBalanceSiteQuotaProvider>.Instance,
            new AccountNetworkClientFactory(accounts, transport), ordinary, transport);
        var account = new AccountIdentity("codex", Guid.NewGuid());
        var result = await provider.QueryAsync(new SiteQuotaSiteContext("https://site.invalid", new("https://quota.invalid", "GET", "$.balance", "USD"),
            "invalid-ordinary-selector", account), "account-key", default);
        result.Success.Should().BeTrue();
        accounts.Accounts.Should().Equal(account);
        ordinary.Requests.Should().BeEmpty();
        transport.Calls.Should().ContainSingle().Which.Configuration.Mode.Should().Be(NetworkMode.Direct);
        transport.Active.Should().Be(0);
    }

    [Fact]
    public async Task Quota_service_builds_context_separately_for_each_key()
    {
        using var fixture = new Database();
        var site = fixture.AddSite();
        var first = new SiteKey { Id = Guid.NewGuid(), SiteId = site.Id, KeyValue = "first", EgressProxyUrl = "profile:first", Priority = 0 };
        var second = new SiteKey { Id = Guid.NewGuid(), SiteId = site.Id, KeyValue = "second", EgressProxyUrl = "direct", Priority = 1 };
        fixture.Db.SiteKeys.Add(first);
        fixture.Db.SiteKeys.Add(second);
        await fixture.Db.SaveChangesAsync();
        var provider = new ContextProvider();
        var account = new AccountIdentity("codex", Guid.NewGuid());
        var service = new SiteQuotaService(fixture.Db, [provider], NullLogger<SiteQuotaService>.Instance, new OwnerStore(site.Id, account));
        await service.RefreshSiteAsync(site.Id, default);
        provider.Contexts.Should().HaveCount(2);
        provider.Contexts.Should().Contain(x => x.Key == "first" && x.Site.SiteKeyId == first.Id && x.Site.EgressProxyUrl == "profile:first");
        provider.Contexts.Should().Contain(x => x.Key == "second" && x.Site.SiteKeyId == second.Id && x.Site.EgressProxyUrl == "direct");
        provider.Contexts.Should().OnlyContain(x => x.Site.AccountIdentity == account);
    }

    [Fact]
    public async Task Catalog_and_health_use_selected_key_owner_and_unresolved_selector()
    {
        using var fixture = new Database();
        var site = fixture.AddSite();
        var key = new SiteKey { Id = Guid.NewGuid(), SiteId = site.Id, KeyValue = "chosen-secret", EgressProxyUrl = "profile:chosen", IsEnabled = true };
        fixture.Db.SiteKeys.Add(key);
        var model = new ModelLibraryItem { Id = Guid.NewGuid(), ModelName = "model", IsEnabled = true };
        fixture.Db.ModelLibraryItems.Add(model);
        var mapping = new SiteModelMapping { Id = Guid.NewGuid(), SiteId = site.Id, ModelLibraryItemId = model.Id, RemoteModelName = "remote", EgressProxyUrl = "mapping-profile", IsEnabled = true };
        fixture.Db.SiteModelMappings.Add(mapping);
        await fixture.Db.SaveChangesAsync();
        var account = new AccountIdentity("codex", Guid.NewGuid());
        var selector = new SiteKeySelector(fixture.Db, new OwnerStore(site.Id, account));
        (await selector.GetActiveKeyAsync(site.Id)).Should().Be("chosen-secret");
        var credential = await selector.GetActiveCredentialAsync(site.Id);
        credential.SiteKeyId.Should().Be(key.Id);
        using var fallback = new HttpClient(new NeverSend());
        var policies = new CapturingPolicies();
        using var transport = new CapturingTransport("{\"data\":[{\"id\":\"remote\"}]}");
        var catalog = new OpenAiSiteCatalogClient(fallback, selector, policies, transport);
        (await catalog.GetModelsAsync(site, default)).Should().Equal("remote");
        policies.Requests.Single().AccountIdentity.Should().Be(account);
        policies.Requests.Single().EgressProxyUrl.Should().Be("profile:chosen");
        var forward = new ForwardCapture();
        await new ModelHealthRequestService(fixture.Db, forward, new UsageSink(), selector,
            sessionPreparation: fixture.SessionPreparation()).ProbeMappingAsync(mapping.Id, "test", default);
        forward.Request!.TargetApiKey.Should().Be("chosen-secret");
        forward.Request.AccountIdentity.Should().Be(account);
        forward.Request.EgressProxyUrl.Should().Be("mapping-profile");
    }

    [Fact]
    public async Task Selected_chat_clone_preserves_account_egress_and_emulation()
    {
        var account = new AccountIdentity("google", Guid.NewGuid());
        var siteId = Guid.NewGuid();
        using var fixture = new Database(new OwnerStore(siteId, account));
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var site = new Site { Id = siteId, Name = "selected", BaseUrl = "https://selected.invalid",
            ApiKey = "fallback", IsEnabled = true, SupportsOpenAi = true, ClientEmulation = "Antigravity", ManagedSource = "Google" };
        var key = new SiteKey { Id = Guid.NewGuid(), SiteId = siteId, KeyValue = "selected-secret", IsEnabled = true, EgressProxyUrl = "profile:selected" };
        var model = new ModelLibraryItem { Id = Guid.NewGuid(), ModelName = "selected-model", IsEnabled = true };
        var mapping = new SiteModelMapping { Id = Guid.NewGuid(), SiteId = siteId, ModelLibraryItemId = model.Id, RemoteModelName = "remote", IsEnabled = true };
        fixture.Db.Sites.Add(site);
        fixture.Db.SiteKeys.Add(key);
        fixture.Db.ModelLibraryItems.Add(model);
        fixture.Db.SiteModelMappings.Add(mapping);
        fixture.Db.GoogleAccounts.Add(new AITool.Domain.Google.GoogleAccount { Id = account.AccountId, LinkedSiteId = siteId, ProjectId = "project" });
        await fixture.Db.SaveChangesAsync();
        var metadata = new ProxyRequestMetadataCache(memory, fixture.Services.GetRequiredService<IServiceScopeFactory>());
        var controller = new ChatApiController(null!, null!, null!, null!, metadata, null!, null!, null!);
        var method = typeof(ChatApiController).GetMethod("ResolveSelectedTargetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var target = await (Task<CachedFallbackTarget?>)method.Invoke(controller, [model.Id, mapping.Id, CancellationToken.None])!;
        target!.AccountIdentity.Should().Be(account);
        target.SiteKeyId.Should().Be(key.Id);
        target.ApiKey.Should().Be("selected-secret");
        target.EgressProxyUrl.Should().Be("profile:selected");
        target.ClientEmulation.Should().Be("Antigravity");
        target.GoogleProjectId.Should().Be("project");
        target.ManagedSource.Should().Be("Google");
    }

    [Fact]
    public async Task Chat_raw_stream_resolves_policy_and_holds_lease_until_SSE_is_consumed()
    {
        var policies = new CapturingPolicies();
        using var transport = new CapturingTransport("data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\ndata: [DONE]\n\n");
        var account = new AccountIdentity("codex", Guid.NewGuid());
        var siteId = Guid.NewGuid();
        var credentials = new RecordingCredentials();
        using var services = new ServiceCollection().AddSingleton<IForwardCredentialResolver>(credentials).BuildServiceProvider();
        var controller = new ChatApiController(null!, null!, null!, null!, null!, null!, null!, null!, policies, transport)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = services } }
        };
        var chunks = new List<string>();
        var method = typeof(ChatApiController).GetMethod("ForwardStreamAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        // Reflection requires every parameter, including the optional network context arguments.
        var task = (Task)method.Invoke(controller, ["OpenAI", "https://stream.invalid", "standard-root", "stream-key", "model", "hello", false,
            "high", (Func<string, Task>)(s => { chunks.Add(s); return Task.CompletedTask; }), (Func<string, Task>)(_ => Task.CompletedTask),
            30, new Dictionary<string, string>(), CancellationToken.None, null, "profile:stream", account, siteId, "Codex"])!;
        await task;
        task.GetType().GetProperty("Result")!.GetValue(task)!.GetType().GetProperty("Success")!
            .GetValue(task.GetType().GetProperty("Result")!.GetValue(task)!).Should().Be(true);
        chunks.Should().Contain("hello");
        policies.Requests.Should().ContainSingle().Which.AccountIdentity.Should().Be(account);
        policies.Requests.Single().EgressProxyUrl.Should().Be("profile:stream");
        policies.Requests.Single().SiteId.Should().Be(siteId);
        policies.Requests.Single().ManagedSource.Should().Be("Codex");
        credentials.Requests.Should().ContainSingle().Which.SiteId.Should().Be(siteId);
        credentials.Requests.Single().ManagedSource.Should().Be("Codex");
        credentials.Requests.Single().AccountIdentity.Should().Be(account);
        transport.ReadWhileLeased.Should().BeTrue();
        transport.Active.Should().Be(0);
    }

    [Fact]
    public async Task Diagnostic_target_and_AI_model_clones_preserve_credential_policy()
    {
        using var fixture = new Database();
        var site = fixture.AddSite();
        fixture.Db.SiteKeys.Add(new SiteKey { Id = Guid.NewGuid(), SiteId = site.Id, KeyValue = "probe-secret",
            EgressProxyUrl = "direct", IsEnabled = true });
        await fixture.Db.SaveChangesAsync();
        var account = new AccountIdentity("google", Guid.NewGuid());
        var selector = new SiteKeySelector(fixture.Db, new OwnerStore(site.Id, account));
        var controller = new DeveloperInvocationsApiController(null!, null!, null!, null!, null!, fixture.Db, null!, null!, selector);
        var resolver = typeof(DeveloperInvocationsApiController).GetMethod("ResolveTestSiteInfoAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var task = (Task)resolver.Invoke(controller, [new DeveloperAutoDiagnoseLoopRequest { TargetSiteId = site.Id }, CancellationToken.None])!;
        await task;
        var result = ((string SiteName, string BaseUrl, string ApiKey, string Mode, string Protocol,
            string? Project, string? Headers, string? Egress, AccountIdentity? Account, Guid? SiteId, string? ManagedSource))task.GetType().GetProperty("Result")!.GetValue(task)!;
        result.ApiKey.Should().Be("probe-secret");
        result.Egress.Should().Be("direct");
        result.Account.Should().Be(account);
        result.SiteId.Should().Be(site.Id);
        result.ManagedSource.Should().Be(site.ManagedSource);
        var clone = typeof(DeveloperInvocationsApiController).GetMethod("ToFallbackTarget", BindingFlags.Static | BindingFlags.NonPublic,
            null, [typeof(Guid), typeof(CachedChatTarget)], null)!;
        var cloned = (CachedFallbackTarget)clone.Invoke(null, [Guid.NewGuid(), new CachedChatTarget
            { AccountIdentity = account, EgressProxyUrl = "profile:ai-model", SiteKeyId = Guid.NewGuid() }])!;
        cloned.AccountIdentity.Should().Be(account);
        cloned.EgressProxyUrl.Should().Be("profile:ai-model");
    }

    [Fact]
    public async Task Health_merged_headers_are_account_isolated_and_internal_retry_is_stable()
    {
        using var fixture = new Database();
        var site = fixture.AddSite();
        site.ManagedSource = "Codex";
        site.ProtocolType = "Responses";
        site.SupportsOpenAi = false;
        site.SupportsResponses = true;
        site.ClientEmulation = "custom-profile";
        site.ExtraHeadersJson = "{\"Session-Id\":\"site-session\",\"X-Layer\":\"site\"}";
        var model = new ModelLibraryItem { Id = Guid.NewGuid(), ModelName = "model", IsEnabled = true,
            ExtraHeadersJson = "{\"Session-Id\":\"model-session\",\"X-Layer\":\"model\"}" };
        var mapping = new SiteModelMapping { Id = Guid.NewGuid(), SiteId = site.Id, ModelLibraryItemId = model.Id,
            RemoteModelName = "remote", IsEnabled = true, EgressProxyUrl = "mapping-profile",
            ExtraHeadersJson = "{\"Session-Id\":\"mapping-session\",\"X-Layer\":\"mapping\",\"Thread-Id\":\"mapping-thread\"}" };
        await fixture.Db.UpdateAsync(site, default);
        fixture.Db.ModelLibraryItems.Add(model);
        fixture.Db.SiteModelMappings.Add(mapping);
        fixture.Db.SystemRuntimeSettings.Add(new SystemRuntimeSettings { DetectionRetryCount = 1 });
        await fixture.Db.SaveChangesAsync();
        var account = new AccountIdentity("codex", Guid.NewGuid());
        var owners = new OwnerStore(site.Id, account);
        var accountPolicies = new AccountPolicies();
        var policies = new RecordingResolver(new ForwardNetworkPolicyResolver(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(), accountPolicies));
        using var transport = new RecordingForwardTransport { FailFirstResponse = true };
        using var fallback = new HttpClient(new NeverSend());
        var forwarder = new ProxyForwardService(fallback, NullLogger<ProxyForwardService>.Instance, transport, policies, new RecordingCredentials());
        var health = new ModelHealthRequestService(fixture.Db, forwarder, new UsageSink(),
            new SiteKeySelector(fixture.Db, owners), new HeaderCatalog(), fixture.SessionPreparation());
        (await health.ProbeMappingAsync(mapping.Id, "test", default)).Status.Should().Be("success");
        transport.Requests.Should().HaveCount(2);
        var first = transport.Requests[0];
        var retry = transport.Requests[1];
        first.Headers["X-Layer"].Should().Be("mapping");
        first.Headers["X-Profile"].Should().Be("profile");
        first.Headers["Session-Id"].Should().MatchRegex("^[0-9a-f]{64}$").And.NotBe("mapping-session");
        first.Headers["Thread-Id"].Should().NotBe("mapping-thread");
        JsonNode.Parse(first.Body)!["prompt_cache_key"]!.ToString().Should().Be(first.Headers["Session-Id"]);
        retry.Body.Should().Be(first.Body);
        retry.Headers["Session-Id"].Should().Be(first.Headers["Session-Id"]);
        retry.Headers["Thread-Id"].Should().Be(first.Headers["Thread-Id"]);
        policies.Requests.Should().HaveCount(2).And.OnlyContain(x => x.AccountIdentity == account && x.EgressProxyUrl == "mapping-profile" && x.SiteId == site.Id && x.ManagedSource == "Codex");
        transport.Calls.Should().OnlyContain(x => x.Mode == NetworkMode.Direct);
        accountPolicies.Accounts.Should().Equal(account, account);

        owners.Account = account with { ProviderKey = "google" };
        (await health.ProbeMappingAsync(mapping.Id, "test", default)).Status.Should().Be("success");
        transport.Requests[2].Headers["Session-Id"].Should().NotBe(first.Headers["Session-Id"]);
        policies.Requests.Last().AccountIdentity.Should().Be(owners.Account);
        owners.Account = account with { AccountId = Guid.NewGuid() };
        (await health.ProbeMappingAsync(mapping.Id, "test", default)).Status.Should().Be("success");
        transport.Requests[3].Headers["Session-Id"].Should().NotBe(first.Headers["Session-Id"]);
        accountPolicies.Accounts.Should().Equal(account, account, account with { ProviderKey = "google" }, owners.Account);
        transport.Scopes[0].Should().Be(transport.Scopes[1]).And.NotBe(transport.Scopes[2]).And.NotBe(transport.Scopes[3]);
    }

    [Fact]
    public async Task Google_health_builds_and_isolates_the_real_antigravity_request()
    {
        using var fixture = new Database();
        var site = fixture.AddSite();
        site.BaseUrl = "https://daily-cloudcode-pa.googleapis.com";
        site.ProtocolType = "Gemini";
        site.ManagedSource = "Google";
        site.SupportsOpenAi = false;
        await fixture.Db.UpdateAsync(site, default);
        var account = new AccountIdentity("google", Guid.NewGuid());
        fixture.Db.GoogleAccounts.Add(new AITool.Domain.Google.GoogleAccount { Id = account.AccountId, LinkedSiteId = site.Id, ProjectId = "test-project" });
        var model = new ModelLibraryItem { Id = Guid.NewGuid(), ModelName = "model", IsEnabled = true };
        var mapping = new SiteModelMapping { Id = Guid.NewGuid(), SiteId = site.Id, ModelLibraryItemId = model.Id, RemoteModelName = "remote", IsEnabled = true };
        fixture.Db.ModelLibraryItems.Add(model);
        fixture.Db.SiteModelMappings.Add(mapping);
        await fixture.Db.SaveChangesAsync();
        using var fallback = new HttpClient(new NeverSend());
        using var transport = new RecordingForwardTransport();
        var policies = new RecordingResolver(new ForwardNetworkPolicyResolver(fixture.Services.GetRequiredService<IServiceScopeFactory>(), new AccountPolicies()));
        var forward = new ProxyForwardService(fallback, NullLogger<ProxyForwardService>.Instance, transport, policies, new RecordingCredentials());
        var health = new ModelHealthRequestService(fixture.Db, forward, new UsageSink(),
            new SiteKeySelector(fixture.Db, new OwnerStore(site.Id, account)), sessionPreparation: fixture.SessionPreparation());
        (await health.ProbeMappingAsync(mapping.Id, "test", default)).Status.Should().Be("success");
        var sent = transport.Requests.Single();
        sent.Url.Should().EndWith("/v1internal:generateContent");
        var body = JsonNode.Parse(sent.Body)!;
        body["project"]!.ToString().Should().Be("test-project");
        var request = body["request"] ?? body;
        var session = request["sessionId"]!.ToString();
        long.Parse(session).Should().BeInRange(1, long.MaxValue);
        request["labels"]!["trajectory_id"]!.ToString().Should().Be(session);
        policies.Requests.Single().AccountIdentity.Should().Be(account);
        policies.Requests.Single().SiteId.Should().Be(site.Id);
        policies.Requests.Single().ManagedSource.Should().Be("Google");
    }

    [Fact]
    public async Task Ordinary_gemini_site_retains_existing_health_path_and_body()
    {
        using var fixture = new Database();
        var site = fixture.AddSite();
        site.ProtocolType = "Gemini";
        site.SupportsOpenAi = false;
        site.BaseUrl = "https://daily-cloudcode-pa.googleapis.com";
        await fixture.Db.UpdateAsync(site, default);
        var model = new ModelLibraryItem { Id = Guid.NewGuid(), ModelName = "model", IsEnabled = true };
        var mapping = new SiteModelMapping { Id = Guid.NewGuid(), SiteId = site.Id, ModelLibraryItemId = model.Id, RemoteModelName = "remote", IsEnabled = true };
        fixture.Db.ModelLibraryItems.Add(model);
        fixture.Db.SiteModelMappings.Add(mapping);
        await fixture.Db.SaveChangesAsync();
        using var fallback = new HttpClient(new NeverSend());
        using var transport = new RecordingForwardTransport();
        var policies = new CapturingPolicies();
        var forward = new ProxyForwardService(fallback, NullLogger<ProxyForwardService>.Instance, transport, policies, new RecordingCredentials());
        var health = new ModelHealthRequestService(fixture.Db, forward, new UsageSink(), new SiteKeySelector(fixture.Db));
        (await health.ProbeMappingAsync(mapping.Id, "test", default)).Status.Should().Be("success");
        var sent = transport.Requests.Single();
        sent.Url.Should().EndWith("/chat/completions").And.NotContain("v1internal");
        var body = JsonNode.Parse(sent.Body)!;
        body["messages"].Should().NotBeNull();
        body["request"].Should().BeNull();
        policies.Requests.Single().AccountIdentity.Should().BeNull();
    }

    [Fact]
    public async Task Health_ordinary_site_preserves_merged_headers_and_managed_missing_dependencies_fail_closed()
    {
        using var fixture = new Database();
        var site = fixture.AddSite();
        site.ExtraHeadersJson = "{\"Session-Id\":\"ordinary-session\"}";
        await fixture.Db.UpdateAsync(site, default);
        var model = new ModelLibraryItem { Id = Guid.NewGuid(), ModelName = "model", IsEnabled = true };
        var mapping = new SiteModelMapping { Id = Guid.NewGuid(), SiteId = site.Id, ModelLibraryItemId = model.Id, RemoteModelName = "remote", IsEnabled = true };
        fixture.Db.ModelLibraryItems.Add(model);
        fixture.Db.SiteModelMappings.Add(mapping);
        await fixture.Db.SaveChangesAsync();
        using var transport = new RecordingForwardTransport();
        using var fallback = new HttpClient(new NeverSend());
        var policies = new CapturingPolicies();
        var forward = new ProxyForwardService(fallback, NullLogger<ProxyForwardService>.Instance, transport, policies, new RecordingCredentials());
        var ordinary = new ModelHealthRequestService(fixture.Db, forward, new UsageSink(), new SiteKeySelector(fixture.Db));
        (await ordinary.ProbeMappingAsync(mapping.Id, "test", default)).Status.Should().Be("success");
        transport.Requests.Last().Headers["Session-Id"].Should().Be("ordinary-session");
        policies.Requests.Last().AccountIdentity.Should().BeNull();
        policies.Requests.Last().SiteId.Should().Be(site.Id);
        policies.Requests.Last().ManagedSource.Should().BeNull();
        var count = transport.Requests.Count;
        site.ManagedSource = "Codex";
        await fixture.Db.UpdateAsync(site, default);
        var missingOwner = await ordinary.ProbeMappingAsync(mapping.Id, "test", default);
        missingOwner.Status.Should().Be("fail");
        missingOwner.ErrorMessage.Should().NotBeNullOrWhiteSpace().And.NotContain(site.ApiKey);
        var managed = new ModelHealthRequestService(fixture.Db, forward, new UsageSink(),
            new SiteKeySelector(fixture.Db, new OwnerStore(site.Id, new AccountIdentity("codex", Guid.NewGuid()))));
        var missingPreparation = await managed.ProbeMappingAsync(mapping.Id, "test", default);
        missingPreparation.Status.Should().Be("fail");
        missingPreparation.ErrorMessage.Should().NotBeNullOrWhiteSpace().And.NotContain(site.ApiKey);
        var selector = new SiteKeySelector(fixture.Db, new OwnerStore(site.Id, new AccountIdentity("codex", Guid.NewGuid())));
        var missingNetwork = new ModelHealthRequestService(fixture.Db,
            new ProxyForwardService(fallback, NullLogger<ProxyForwardService>.Instance), new UsageSink(), selector,
            sessionPreparation: fixture.SessionPreparation());
        (await missingNetwork.ProbeMappingAsync(mapping.Id, "test", default)).Status.Should().Be("fail");
        var missingTransport = new ModelHealthRequestService(fixture.Db,
            new ProxyForwardService(fallback, NullLogger<ProxyForwardService>.Instance, networkPolicyResolver: policies, credentialResolver: new RecordingCredentials()),
            new UsageSink(), selector, sessionPreparation: fixture.SessionPreparation());
        (await missingTransport.ProbeMappingAsync(mapping.Id, "test", default)).Status.Should().Be("fail");
        transport.Requests.Should().HaveCount(count);
    }

    [Fact]
    public async Task Assistant_uses_managed_identity_network_policy_and_stable_session_on_empty_retry()
    {
        var siteId = Guid.NewGuid();
        var account = new AccountIdentity("codex", Guid.NewGuid());
        using var fixture = new Database(new OwnerStore(siteId, account));
        var site = new Site { Id = siteId, Name = "assistant", BaseUrl = "https://assistant.invalid", ApiKey = "secret",
            ManagedSource = "Codex", SupportsOpenAi = false, SupportsResponses = true, IsEnabled = true,
            ExtraHeadersJson = "{\"Session-Id\":\"configured-session\"}" };
        var model = new ModelLibraryItem { Id = Guid.NewGuid(), ModelName = "model", IsEnabled = true };
        var mapping = new SiteModelMapping { Id = Guid.NewGuid(), SiteId = siteId, ModelLibraryItemId = model.Id,
            RemoteModelName = "remote", IsEnabled = true, EgressProxyUrl = "profile:assistant" };
        fixture.Db.Sites.Add(site);
        fixture.Db.ModelLibraryItems.Add(model);
        fixture.Db.SiteModelMappings.Add(mapping);
        fixture.Db.SystemRuntimeSettings.Add(new SystemRuntimeSettings { DefaultAiTargetMappingId = mapping.Id });
        await fixture.Db.SaveChangesAsync();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var metadata = new ProxyRequestMetadataCache(memory, fixture.Services.GetRequiredService<IServiceScopeFactory>());
        var accountPolicies = new AccountPolicies();
        var policies = new RecordingResolver(new ForwardNetworkPolicyResolver(
            fixture.Services.GetRequiredService<IServiceScopeFactory>(), accountPolicies));
        using var transport = new RecordingForwardTransport { EmptyFirstResponse = true };
        using var fallback = new HttpClient(new NeverSend());
        var forward = new ProxyForwardService(fallback, NullLogger<ProxyForwardService>.Instance, transport, policies, new RecordingCredentials());
        var assistant = new AiAssistantService(fixture.Db, metadata, forward, NullLogger<AiAssistantService>.Instance, fixture.SessionPreparation());
        (await assistant.CompleteAsync("hello", default)).Success.Should().BeTrue();
        transport.Requests.Should().HaveCount(2);
        var first = transport.Requests[0];
        first.Headers["Session-Id"].Should().MatchRegex("^[0-9a-f]{64}$").And.NotBe("configured-session");
        JsonNode.Parse(first.Body)!["prompt_cache_key"]!.ToString().Should().Be(first.Headers["Session-Id"]);
        transport.Requests[1].Body.Should().Be(first.Body);
        transport.Requests[1].Headers["Session-Id"].Should().Be(first.Headers["Session-Id"]);
        policies.Requests.Should().HaveCount(2).And.OnlyContain(x => x.AccountIdentity == account && x.EgressProxyUrl == "profile:assistant" && x.SiteId == siteId && x.ManagedSource == "Codex");
        accountPolicies.Accounts.Should().Equal(account, account);
        transport.Calls.Should().OnlyContain(x => x.Mode == NetworkMode.Direct);
        var missingPreparation = new AiAssistantService(fixture.Db, metadata, forward, NullLogger<AiAssistantService>.Instance);
        (await missingPreparation.CompleteAsync("hello", default)).Success.Should().BeFalse();
        var noResolver = new ProxyForwardService(fallback, NullLogger<ProxyForwardService>.Instance);
        var missingResolver = new AiAssistantService(fixture.Db, metadata, noResolver, NullLogger<AiAssistantService>.Instance, fixture.SessionPreparation());
        (await missingResolver.CompleteAsync("hello", default)).Success.Should().BeFalse();
        var noCredentials = new ProxyForwardService(fallback, NullLogger<ProxyForwardService>.Instance, transport, policies);
        var missingCredentials = new AiAssistantService(fixture.Db, metadata, noCredentials, NullLogger<AiAssistantService>.Instance, fixture.SessionPreparation());
        (await missingCredentials.CompleteAsync("hello", default)).Success.Should().BeFalse();
        var noTransport = new ProxyForwardService(fallback, NullLogger<ProxyForwardService>.Instance, networkPolicyResolver: policies, credentialResolver: new RecordingCredentials());
        var missingTransport = new AiAssistantService(fixture.Db, metadata, noTransport, NullLogger<AiAssistantService>.Instance, fixture.SessionPreparation());
        (await missingTransport.CompleteAsync("hello", default)).Success.Should().BeFalse();
        transport.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task Assistant_ordinary_site_is_unchanged_and_orphaned_managed_site_is_blocked()
    {
        using var fixture = new Database(new OwnerStore(Guid.NewGuid(), new AccountIdentity("codex", Guid.NewGuid())));
        var site = fixture.AddSite();
        site.ExtraHeadersJson = "{\"Session-Id\":\"ordinary-session\"}";
        await fixture.Db.UpdateAsync(site, default);
        var model = new ModelLibraryItem { Id = Guid.NewGuid(), ModelName = "model", IsEnabled = true };
        var mapping = new SiteModelMapping { Id = Guid.NewGuid(), SiteId = site.Id, ModelLibraryItemId = model.Id,
            RemoteModelName = "remote", IsEnabled = true };
        fixture.Db.ModelLibraryItems.Add(model);
        fixture.Db.SiteModelMappings.Add(mapping);
        fixture.Db.SystemRuntimeSettings.Add(new SystemRuntimeSettings { DefaultAiTargetMappingId = mapping.Id });
        await fixture.Db.SaveChangesAsync();
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new ProxyRequestMetadataCache(memory, fixture.Services.GetRequiredService<IServiceScopeFactory>());
        using var transport = new RecordingForwardTransport();
        using var fallback = new HttpClient(new NeverSend());
        var policies = new CapturingPolicies();
        var forward = new ProxyForwardService(fallback, NullLogger<ProxyForwardService>.Instance, transport, policies, new RecordingCredentials());
        var assistant = new AiAssistantService(fixture.Db, cache, forward, NullLogger<AiAssistantService>.Instance);
        (await assistant.CompleteAsync("hello", default)).Success.Should().BeTrue();
        transport.Requests.Single().Headers["Session-Id"].Should().Be("ordinary-session");
        policies.Requests.Single().AccountIdentity.Should().BeNull();
        policies.Requests.Single().SiteId.Should().Be(site.Id);
        policies.Requests.Single().ManagedSource.Should().BeNullOrEmpty();
        site.ManagedSource = "Codex";
        await fixture.Db.UpdateAsync(site, default);
        using var freshMemory = new MemoryCache(new MemoryCacheOptions());
        var freshCache = new ProxyRequestMetadataCache(freshMemory, fixture.Services.GetRequiredService<IServiceScopeFactory>());
        var orphaned = new AiAssistantService(fixture.Db, freshCache, forward, NullLogger<AiAssistantService>.Instance, fixture.SessionPreparation());
        (await orphaned.CompleteAsync("hello", default)).Success.Should().BeFalse();
        transport.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Detection_scheduler_completes_mixed_batch_and_continues_later_batches()
    {
        var path = Path.Combine(Path.GetTempPath(), $"detection-isolation-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddSqlSugar($"Data Source={path}");
        var calls = new SchedulerForwardCapture();
        var logs = new SchedulerUsageSink();
        services.AddSingleton<IProxyForwardService>(calls);
        services.AddSingleton<IUsageLogService>(logs);
        services.AddScoped<SiteKeySelector>();
        services.AddScoped<ModelHealthRequestService>();
        using (var provider = services.BuildServiceProvider())
        {
            var db = provider.GetRequiredService<AppDbContext>();
            db.Client.Ado.ExecuteCommand("PRAGMA journal_mode=WAL;");
            db.Client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
            db.Client.CodeFirst.InitTables(typeof(Site), typeof(SiteKey), typeof(ModelLibraryItem),
                typeof(SiteModelMapping), typeof(SystemRuntimeSettings), typeof(DetectionTask), typeof(DetectionTaskExecution));
            var model = new ModelLibraryItem { Id = Guid.NewGuid(), ModelName = "scheduler-model", IsEnabled = true };
            await db.InsertAsync(model);
            var good1 = new Site { Id = Guid.NewGuid(), Name = "good-first", BaseUrl = "https://first.invalid", ApiKey = "first-key" };
            var bad = new Site { Id = Guid.NewGuid(), Name = "second", BaseUrl = "https://second.invalid", ApiKey = "secret-never-log" };
            var good2 = new Site { Id = Guid.NewGuid(), Name = "good-later", BaseUrl = "https://later.invalid", ApiKey = "last-key" };
            await db.InsertAsync(good1);
            await db.InsertAsync(bad);
            await db.InsertAsync(good2);
            var mappings = new[] { good1, bad, good2 }.Select(site => new SiteModelMapping
            {
                Id = Guid.NewGuid(), SiteId = site.Id, ModelLibraryItemId = model.Id,
                RemoteModelName = site.Name, IsEnabled = true
            }).ToArray();
            foreach (var mapping in mappings) await db.InsertAsync(mapping);
            await db.InsertAsync(new SystemRuntimeSettings { DetectionConcurrency = 2 });
            var task = new DetectionTask { Name = "mixed", IsEnabled = true };
            await db.InsertAsync(task);
            // Verify the scheduler's actual query order so the failed and successful probes
            // are in the same first batch and another successful probe is in the next batch.
            var order = await db.SiteModelMappings.ToListAsync();
            order.Should().HaveCount(3);
            var blockedMapping = order[1];
            var blockedSite = new[] { good1, bad, good2 }.Single(x => x.Id == blockedMapping.SiteId);
            blockedSite.ManagedSource = "Codex";
            blockedSite.ApiKey = "secret-never-log";
            await db.UpdateAsync(blockedSite);
            var scheduler = new DetectionTaskSchedulerService(provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<DetectionTaskSchedulerService>.Instance);
            await scheduler.ExecuteDetectionTaskAsync(task.Id, CancellationToken.None);
            var execution = (await db.DetectionTaskExecutions.ToListAsync()).Single();
            execution.Status.Should().Be("completed");
            execution.FinishedAt.Should().NotBeNull();
            execution.Summary.Should().Contain("成功 2").And.Contain("失败 1");
            calls.Sites.Should().BeEquivalentTo(new[] { order[0].SiteId, order[2].SiteId });
            calls.Sites.Should().NotContain(blockedSite.Id);
            var updated = await db.SiteModelMappings.ToListAsync();
            updated.Single(x => x.Id == blockedMapping.Id).LastStatus.Should().Be("fail");
            logs.Entries.Should().HaveCount(3);
            var failure = logs.Entries.Single(x => x.TargetSiteId == blockedSite.Id);
            failure.Status.Should().Be("fail");
            failure.ErrorMessage.Should().NotBeNullOrWhiteSpace().And.NotContain(blockedSite.ApiKey);
        }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(path + suffix); } catch (IOException) { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Health_owner_lookup_failure_and_uncanceled_dependency_interrupt_are_safe(bool cancel)
    {
        using var fixture = new Database();
        var site = fixture.AddSite();
        site.ManagedSource = "Codex";
        await fixture.Db.UpdateAsync(site, default);
        var model = new ModelLibraryItem { Id = Guid.NewGuid(), ModelName = "lookup-model" };
        var mapping = new SiteModelMapping { Id = Guid.NewGuid(), SiteId = site.Id, ModelLibraryItemId = model.Id, RemoteModelName = "remote" };
        fixture.Db.ModelLibraryItems.Add(model);
        fixture.Db.SiteModelMappings.Add(mapping);
        var forward = new ForwardCapture();
        var sink = new SchedulerUsageSink();
        var health = new ModelHealthRequestService(fixture.Db, forward, sink,
            new SiteKeySelector(fixture.Db, new ThrowingOwnerStore(cancel)));
        // An OCE from a dependency does not mean the caller canceled.
        var result = await health.ProbeMappingAsync(mapping.Id, "test", default);
        result.Status.Should().Be("fail");
        result.ErrorMessage.Should().NotContain("secret-never-log");
        sink.Entries.Should().ContainSingle().Which.ErrorMessage.Should().NotContain("secret-never-log");
        forward.Request.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Health_session_preparation_failure_and_uncanceled_dependency_interrupt_are_safe(bool cancel)
    {
        using var fixture = new Database();
        var site = fixture.AddSite();
        site.ManagedSource = "Codex";
        await fixture.Db.UpdateAsync(site, default);
        var model = new ModelLibraryItem { Id = Guid.NewGuid(), ModelName = "session-model" };
        var mapping = new SiteModelMapping { Id = Guid.NewGuid(), SiteId = site.Id, ModelLibraryItemId = model.Id, RemoteModelName = "remote" };
        fixture.Db.ModelLibraryItems.Add(model);
        fixture.Db.SiteModelMappings.Add(mapping);
        var forward = new ForwardCapture();
        var sink = new SchedulerUsageSink();
        var health = new ModelHealthRequestService(fixture.Db, forward, sink,
            new SiteKeySelector(fixture.Db, new OwnerStore(site.Id, new AccountIdentity("codex", Guid.NewGuid()))),
            sessionPreparation: new ThrowingPreparation(cancel));
        // An OCE from a dependency does not mean the caller canceled.
        var result = await health.ProbeMappingAsync(mapping.Id, "test", default);
        result.Status.Should().Be("fail");
        result.ErrorMessage.Should().NotContain("secret-never-log");
        sink.Entries.Should().ContainSingle().Which.ErrorMessage.Should().NotContain("secret-never-log");
        forward.Request.Should().BeNull();
    }

    [Theory]
    [InlineData("io")]
    [InlineData("exception")]
    public async Task Real_forward_interrupts_are_safe_failures_and_scheduler_finishes_later_batches(string kind)
    {
        var path = Path.Combine(Path.GetTempPath(), $"detection-forward-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddSqlSugar($"Data Source={path}");
        var handler = new InterruptedForwardHandler(kind);
        using var fallback = new HttpClient(handler);
        var forward = new ProxyForwardService(fallback, NullLogger<ProxyForwardService>.Instance,
            networkPolicyResolver: new DefaultNetworkResolver(), credentialResolver: new RecordingCredentials());
        var logs = new SchedulerUsageSink();
        services.AddSingleton<IProxyForwardService>(forward);
        services.AddSingleton<IUsageLogService>(logs);
        services.AddScoped<SiteKeySelector>();
        services.AddScoped<ModelHealthRequestService>();
        using (var provider = services.BuildServiceProvider())
        {
            var db = provider.GetRequiredService<AppDbContext>();
            db.Client.Ado.ExecuteCommand("PRAGMA journal_mode=WAL;");
            db.Client.Ado.ExecuteCommand("PRAGMA busy_timeout=5000;");
            db.Client.CodeFirst.InitTables(typeof(Site), typeof(SiteKey), typeof(ModelLibraryItem),
                typeof(SiteModelMapping), typeof(SystemRuntimeSettings), typeof(DetectionTask), typeof(DetectionTaskExecution));
            var model = new ModelLibraryItem { Id = Guid.NewGuid(), ModelName = "transport-model" };
            await db.InsertAsync(model);
            for (var i = 0; i < 3; i++)
            {
                var site = new Site { Id = Guid.NewGuid(), Name = $"site-{i}", BaseUrl = $"https://site-{i}.invalid", ApiKey = "sentinel-secret" };
                await db.InsertAsync(site);
                await db.InsertAsync(new SiteModelMapping { Id = Guid.NewGuid(), SiteId = site.Id,
                    ModelLibraryItemId = model.Id, RemoteModelName = $"remote-{i}" });
            }
            await db.InsertAsync(new SystemRuntimeSettings { DetectionConcurrency = 2, DetectionRetryCount = 0 });
            var task = new DetectionTask { Name = "transport-failure", IsEnabled = true };
            await db.InsertAsync(task);
            var order = await db.SiteModelMappings.ToListAsync();
            var failedSite = (await db.Sites.ToListAsync()).Single(x => x.Id == order[1].SiteId);
            handler.BlockedHost = new Uri(failedSite.BaseUrl).Host;
            var scheduler = new DetectionTaskSchedulerService(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<DetectionTaskSchedulerService>.Instance);
            await scheduler.ExecuteDetectionTaskAsync(task.Id, default);
            var execution = (await db.DetectionTaskExecutions.ToListAsync()).Single();
            execution.Status.Should().Be("completed");
            execution.FinishedAt.Should().NotBeNull();
            execution.Summary.Should().Contain("成功 2").And.Contain("失败 1").And.NotContain("sentinel-secret");
            handler.Hosts.Should().HaveCount(3).And.Contain(new Uri((await db.Sites.ToListAsync()).Single(x => x.Id == order[2].SiteId).BaseUrl).Host);
            logs.Entries.Should().HaveCount(3);
            logs.Entries.Single(x => x.TargetSiteId == failedSite.Id).ErrorMessage.Should().NotBeNullOrWhiteSpace().And.NotContain("sentinel-secret");
            logs.Entries.Should().OnlyContain(x => !x.ErrorMessage.Contains("sentinel-secret"));
            var health = new ModelHealthRequestService(db, forward, logs, new SiteKeySelector(db));
            var result = await health.ProbeMappingAsync(order[1].Id, "manual", default);
            result.Status.Should().Be("fail");
            result.ErrorMessage.Should().NotBeNullOrWhiteSpace().And.NotContain("sentinel-secret");
        }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(path + suffix); } catch (IOException) { }
    }

    [Fact]
    public async Task Actual_caller_cancellation_is_propagated_through_real_forwarder()
    {
        using var fixture = new Database();
        var site = fixture.AddSite();
        var model = new ModelLibraryItem { Id = Guid.NewGuid(), ModelName = "cancel-model" };
        var mapping = new SiteModelMapping { Id = Guid.NewGuid(), SiteId = site.Id, ModelLibraryItemId = model.Id, RemoteModelName = "remote" };
        fixture.Db.ModelLibraryItems.Add(model);
        fixture.Db.SiteModelMappings.Add(mapping);
        using var cancellation = new CancellationTokenSource();
        using var client = new HttpClient(new CancelCallerHandler(cancellation));
        var forward = new ProxyForwardService(client, NullLogger<ProxyForwardService>.Instance,
            networkPolicyResolver: new DefaultNetworkResolver(), credentialResolver: new RecordingCredentials());
        var logs = new SchedulerUsageSink();
        var health = new ModelHealthRequestService(fixture.Db, forward, logs, new SiteKeySelector(fixture.Db));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => health.ProbeMappingAsync(mapping.Id, "manual", cancellation.Token));
        logs.Entries.Should().BeEmpty();
    }

    private static ISiteQuotaProvider Provider(string key, HttpClient client, IForwardNetworkPolicyResolver policies, INetworkTransport transport)
        => key switch
        {
            "custom-balance" => new CustomBalanceSiteQuotaProvider(client, NullLogger<CustomBalanceSiteQuotaProvider>.Instance, null, policies, transport),
            "deepseek" => new DeepSeekSiteQuotaProvider(client, NullLogger<DeepSeekSiteQuotaProvider>.Instance, null, policies, transport),
            _ => new ZhipuSiteQuotaProvider(client, NullLogger<ZhipuSiteQuotaProvider>.Instance, null, policies, transport)
        };

    private sealed class DefaultNetworkResolver : IForwardNetworkPolicyResolver
    {
        public Task<ForwardNetworkPolicyResolution> ResolveAsync(ProxyForwardRequest request, CancellationToken ct = default)
            => Task.FromResult(new ForwardNetworkPolicyResolution(ResolvedNetworkConfiguration.Legacy, "test", true));
    }

    private sealed class InterruptedForwardHandler(string kind) : HttpMessageHandler
    {
        public string BlockedHost { get; set; } = string.Empty;
        public System.Collections.Concurrent.ConcurrentBag<string> Hosts { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var host = request.RequestUri!.Host;
            Hosts.Add(host);
            if (host == BlockedHost)
            {
                if (kind == "io") throw new IOException("sentinel-secret");
                throw new InvalidOperationException("sentinel-secret");
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}") });
        }
    }

    private sealed class CancelCallerHandler(CancellationTokenSource source) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            source.Cancel();
            throw new OperationCanceledException(ct);
        }
    }

    private sealed class NeverSend : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("Unexpected default client send.");
    }

    private sealed class CapturingPolicies : IForwardNetworkPolicyResolver
    {
        public bool Reject { get; init; }
        public List<ProxyForwardRequest> Requests { get; } = [];
        public Task<ForwardNetworkPolicyResolution> ResolveAsync(ProxyForwardRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (Reject) throw new NetworkPolicyException("proxy_profile_not_found", "Profile unavailable.");
            var configuration = request.EgressProxyUrl == "direct" ? ResolvedNetworkConfiguration.Direct
                : new ResolvedNetworkConfiguration { Mode = NetworkMode.ProxyProfile, ProxyProfileId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    ProfileRevision = 9, ProxyEndpoint = new Uri("http://proxy.invalid:8080") };
            return Task.FromResult(new ForwardNetworkPolicyResolution(configuration, "captured"));
        }
    }

    private sealed class RecordingCredentials : IForwardCredentialResolver
    {
        public List<ProxyForwardRequest> Requests { get; } = [];

        public Task<ForwardCredentialLease> ResolveAsync(ProxyForwardRequest request, CancellationToken ct = default)
        {
            request.SiteId.Should().NotBeNull();
            Requests.Add(request);
            return Task.FromResult(new ForwardCredentialLease(request.TargetApiKey));
        }
    }

    private sealed class RecordingResolver(IForwardNetworkPolicyResolver inner) : IForwardNetworkPolicyResolver
    {
        public List<ProxyForwardRequest> Requests { get; } = [];
        public Task<ForwardNetworkPolicyResolution> ResolveAsync(ProxyForwardRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return inner.ResolveAsync(request, ct);
        }
    }

    private sealed class AccountPolicies : IAccountNetworkPolicyResolver
    {
        public List<AccountIdentity> Accounts { get; } = [];
        public Task<ResolvedNetworkConfiguration> ResolveForAccountAsync(AccountIdentity account, CancellationToken ct = default)
        { Accounts.Add(account); return Task.FromResult(ResolvedNetworkConfiguration.Direct); }
        public Task<ResolvedNetworkConfiguration> ResolveAsync(NetworkOperationContext operation, CancellationToken ct = default)
            => throw new InvalidOperationException("Expected account resolution.");
    }

    private sealed class CapturingTransport(string body) : INetworkTransport, IDisposable
    {
        public List<(ResolvedNetworkConfiguration Configuration, string Scope, string Purpose)> Calls { get; } = [];
        public List<string> Authorizations { get; } = [];
        public int Active { get; private set; }
        public bool ReadWhileLeased { get; private set; }
        private HttpClient? _client;
        public ValueTask<INetworkTransportLease> AcquireAsync(ResolvedNetworkConfiguration configuration, string isolationScope, string purpose, CancellationToken ct = default)
        {
            Calls.Add((configuration, isolationScope, purpose));
            Active++;
            _client ??= new HttpClient(new Handler(this, body));
            return ValueTask.FromResult<INetworkTransportLease>(new Lease(this, _client));
        }
        public void Dispose() => _client?.Dispose();
        private sealed class Lease(CapturingTransport owner, HttpClient client) : INetworkTransportLease
        {
            public HttpClient Client => client;
            public ValueTask DisposeAsync() { owner.Active--; return ValueTask.CompletedTask; }
        }
        private sealed class Handler(CapturingTransport owner, string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                owner.Authorizations.Add(string.Join("", request.Headers.GetValues("Authorization")));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new Body(owner, body) });
            }
        }
        private sealed class Body(CapturingTransport owner, string body) : HttpContent
        {
            protected override bool TryComputeLength(out long length) { length = 0; return false; }
            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            {
                owner.ReadWhileLeased = owner.Active > 0;
                owner.ReadWhileLeased.Should().BeTrue();
                await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(body));
            }
        }
    }

    private sealed class RecordingForwardTransport : INetworkTransport, IDisposable
    {
        public sealed record RecordedRequest(string Url, string Body, Dictionary<string, string> Headers);
        public List<RecordedRequest> Requests { get; } = [];
        public List<ResolvedNetworkConfiguration> Calls { get; } = [];
        public List<string> Scopes { get; } = [];
        public bool FailFirstResponse { get; init; }
        public bool EmptyFirstResponse { get; init; }
        private HttpClient? _client;
        public ValueTask<INetworkTransportLease> AcquireAsync(ResolvedNetworkConfiguration configuration, string isolationScope, string purpose, CancellationToken ct = default)
        {
            Calls.Add(configuration);
            Scopes.Add(isolationScope);
            _client ??= new HttpClient(new Handler(this));
            return ValueTask.FromResult<INetworkTransportLease>(new Lease(_client));
        }
        public void Dispose() => _client?.Dispose();
        private sealed class Lease(HttpClient client) : INetworkTransportLease
        {
            public HttpClient Client => client;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
        private sealed class Handler(RecordingForwardTransport owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
                owner.Requests.Add(new RecordedRequest(request.RequestUri!.ToString(), body, headers));
                if (owner.FailFirstResponse && owner.Requests.Count == 1)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("temporary") };
                var response = owner.EmptyFirstResponse && owner.Requests.Count == 1
                    ? "{\"output\":[{\"type\":\"message\",\"content\":[]}]}"
                    : "{\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"ok\"}]}],\"choices\":[{\"message\":{\"content\":\"ok\"}}],\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]}}]}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
            }
        }
    }

    private sealed class HeaderCatalog : IHeaderProfileCatalogService
    {
        private readonly HeaderProfile _profile = new() { Key = "custom-profile", HeadersJson = "{\"Session-Id\":\"profile-session\",\"X-Profile\":\"profile\"}" };
        public Task<HeaderProfile?> GetByKeyAsync(string key, CancellationToken ct = default) => Task.FromResult<HeaderProfile?>(_profile);
        public Task<IReadOnlyList<HeaderProfile>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<HeaderProfile>>([_profile]);
        public Task<HeaderProfile?> GetByIdAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, string>> GetActiveProfilesDictionaryAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HeaderProfile> CreateAsync(HeaderProfile profile, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<HeaderProfile?> UpdateAsync(Guid id, Action<HeaderProfile> action, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<HeaderProfile>> ResetBuiltInsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class SchedulerForwardCapture : IProxyForwardService
    {
        public System.Collections.Concurrent.ConcurrentBag<Guid> Sites { get; } = [];
        public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken ct = default)
        {
            Sites.Add(request.SiteId!.Value);
            return Task.FromResult(new ProxyForwardResult { Success = true, StatusCode = 200 });
        }
        public Task<ProxyForwardResult> ForwardStreamingAsync(ProxyForwardRequest request, Func<string, CancellationToken, Task> callback, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class SchedulerUsageSink : IUsageLogService
    {
        public System.Collections.Concurrent.ConcurrentBag<UsageLogEntry> Entries { get; } = [];
        public Task LogAsync(UsageLogEntry entry, CancellationToken ct = default)
        { Entries.Add(entry); return Task.CompletedTask; }
    }

    private sealed class ThrowingPreparation(bool cancel) : IAuxiliarySessionPreparation
    {
        public Task<AuxiliaryPreparedSession> PrepareAsync(string trustedScope, AccountIdentity account,
            string protocolType, bool isAntigravity, string originalRequestBody, string preparedRequestBody,
            IReadOnlyDictionary<string, string> mergedHeaders, CancellationToken cancellationToken = default)
        {
            if (cancel) throw new OperationCanceledException();
            throw new InvalidOperationException("secret-never-log");
        }
    }

    private sealed class ThrowingOwnerStore(bool cancel) : IAccountNetworkPolicyStore
    {
        public Task<IReadOnlyDictionary<Guid, AccountIdentity>> GetLinkedSiteOwnersAsync(IReadOnlyCollection<Guid> sites, CancellationToken ct = default)
        {
            if (cancel) throw new OperationCanceledException();
            throw new InvalidOperationException("secret-never-log");
        }
        public Task<AccountNetworkPolicyState> GetAsync(AccountIdentity a, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AccountNetworkPolicyState> SetAsync(AccountIdentity a, AccountNetworkSelection s, string? revision = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(AccountIdentity a, string? revision = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class OwnerStore(Guid siteId, AccountIdentity account) : IAccountNetworkPolicyStore
    {
        public AccountIdentity Account { get; set; } = account;
        public Task<IReadOnlyDictionary<Guid, AccountIdentity>> GetLinkedSiteOwnersAsync(IReadOnlyCollection<Guid> sites, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, AccountIdentity>>(new Dictionary<Guid, AccountIdentity> { [siteId] = Account });
        public Task<AccountNetworkPolicyState> GetAsync(AccountIdentity a, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AccountNetworkPolicyState> SetAsync(AccountIdentity a, AccountNetworkSelection s, string? revision = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(AccountIdentity a, string? revision = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class ContextProvider : ISiteQuotaProvider
    {
        public List<(SiteQuotaSiteContext Site, string Key)> Contexts { get; } = [];
        public string ProviderKey => "capture";
        public string ProviderLabel => "capture";
        public bool MatchesBaseUrl(string url) => true;
        public Task<SiteQuotaQueryResult> QueryAsync(string url, string key, CancellationToken ct) => throw new NotSupportedException();
        public Task<SiteQuotaQueryResult> QueryAsync(SiteQuotaSiteContext site, string key, CancellationToken ct)
        { Contexts.Add((site, key)); return Task.FromResult(new SiteQuotaQueryResult { Success = true }); }
        public SiteQuotaQueryResult? ParseCached(string raw) => null;
    }

    private sealed class ForwardCapture : IProxyForwardService
    {
        public ProxyForwardRequest? Request { get; private set; }
        public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken ct = default)
        { Request = request; return Task.FromResult(new ProxyForwardResult { Success = true, StatusCode = 200 }); }
        public Task<ProxyForwardResult> ForwardStreamingAsync(ProxyForwardRequest request, Func<string, CancellationToken, Task> callback, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class UsageSink : IUsageLogService
    {
        public Task LogAsync(UsageLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class Database : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"auxiliary-network-{Guid.NewGuid():N}.db");
        private readonly ServiceProvider _services;
        private readonly EphemeralDataProtectionProvider _protection = new();
        public AppDbContext Db { get; }
        public ServiceProvider Services => _services;
        public Database(IAccountNetworkPolicyStore? ownerStore = null)
        {
            var services = new ServiceCollection();
            services.AddSqlSugar($"Data Source={_path}");
            if (ownerStore is not null) services.AddSingleton(ownerStore);
            _services = services.BuildServiceProvider();
            Db = _services.GetRequiredService<AppDbContext>();
            Db.Client.CodeFirst.InitTables(typeof(Site), typeof(SiteKey), typeof(ModelLibraryItem), typeof(SiteModelMapping), typeof(SystemRuntimeSettings), typeof(AITool.Domain.Google.GoogleAccount), typeof(SessionIsolationKey), typeof(SessionIsolationKeyMetadata));
        }
        public IAuxiliarySessionPreparation SessionPreparation() => new AuxiliarySessionPreparation(
            new AccountSessionIsolationService(new SessionIsolationDeriver(new SessionIsolationKeyStore(
                _services.GetRequiredService<IServiceScopeFactory>(), _protection))));
        public Site AddSite()
        {
            var site = new Site { Id = Guid.NewGuid(), Name = "test", BaseUrl = "https://site.invalid", ApiKey = "fallback", EgressProxyUrl = "site-profile", IsEnabled = true, SupportsOpenAi = true };
            Db.Sites.Add(site);
            Db.SaveChangesAsync().GetAwaiter().GetResult();
            return site;
        }
        public void Dispose()
        {
            _services.Dispose();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(_path + suffix); } catch (IOException) { }
        }
    }
}
