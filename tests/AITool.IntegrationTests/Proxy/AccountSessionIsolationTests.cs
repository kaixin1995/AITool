using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AITool.Application.Networking;
using AITool.Application.Proxy;
using AITool.Domain.Operations;
using AITool.Domain.OAuth;
using AITool.Domain.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;

namespace AITool.IntegrationTests.Proxy;

public sealed class AccountSessionIsolationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"aitool-account-session-{Guid.NewGuid():N}");
    private readonly ServiceProvider _provider;
    private readonly AccountSessionIsolationService _service;
    private readonly AccountIdentity _account = new("codex", Guid.Parse("ab100000-0000-0000-0000-000000000001"));

    public AccountSessionIsolationTests()
    {
        Directory.CreateDirectory(_directory);
        var services = new ServiceCollection();
        services.AddSqlSugar($"Data Source={Path.Combine(_directory, "sessions.db")};Pooling=False");
        services.AddDataProtection().SetApplicationName("AccountSessionIsolationTests")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(_directory, "keyring")));
        _provider = services.BuildServiceProvider();
        _provider.GetRequiredService<ISqlSugarClient>().CodeFirst.InitTables(typeof(SessionIsolationKey), typeof(SessionIsolationKeyMetadata), typeof(DeviceOAuthAccount));
        _service = new AccountSessionIsolationService(new SessionIsolationDeriver(new SessionIsolationKeyStore(
            _provider.GetRequiredService<IServiceScopeFactory>(), _provider.GetRequiredService<IDataProtectionProvider>())),
            _provider.GetRequiredService<IServiceScopeFactory>());
    }

    [Fact]
    public async Task Original_body_priority_and_post_merge_rewrite_are_stable_and_account_client_scoped()
    {
        var headers = new HeaderDictionary { ["Session-Id"] = "client-session", ["Thread-Id"] = "same", ["Conversation_id"] = "same" };
        var context = AccountSessionIsolationService.Capture("client-a", headers, "{\"prompt_cache_key\":\"original-body\"}");
        headers["Session-Id"] = "mutated-after-capture";
        var merged = new Dictionary<string, string> { ["Session-Id"] = "emulated-random", ["Thread-Id"] = "extra-thread", ["Conversation_id"] = "extra-conversation", ["User-Agent"] = "keep-device" };
        var first = await _service.ApplyAsync(context, _account, "Responses", false, "{\"model\":\"one\",\"prompt_cache_key\":\"converted\"}", merged);
        var second = await _service.ApplyAsync(context, _account, "Responses", false, "{\"model\":\"two\"}", new Dictionary<string, string> { ["Session-Id"] = "another-generated" });
        var expected = JsonNode.Parse(first.PreparedRequestBody)!["prompt_cache_key"]!.ToString();
        expected.Should().MatchRegex("^[0-9a-f]{64}$");
        first.ForwardHeaders["Session-Id"].Should().Be(expected);
        second.ForwardHeaders["Session-Id"].Should().Be(expected);
        first.ForwardHeaders["Thread-Id"].Should().NotBe(first.ForwardHeaders["Conversation_id"]);
        first.ForwardHeaders["User-Agent"].Should().Be("keep-device");
        merged["Session-Id"].Should().Be("emulated-random");
        var otherAccount = await _service.ApplyAsync(context, _account with { AccountId = Guid.NewGuid() }, "Responses", false, "{}", merged);
        otherAccount.ForwardHeaders["Session-Id"].Should().NotBe(expected);
        var otherClient = AccountSessionIsolationService.Capture("client-b", new HeaderDictionary(), "{\"prompt_cache_key\":\"original-body\"}");
        (await _service.ApplyAsync(otherClient, _account, "Responses", false, "{}", merged)).ForwardHeaders["Session-Id"].Should().NotBe(expected);
    }

    [Theory]
    [InlineData("X-Session-Id")]
    [InlineData("X-Conversation-Id")]
    [InlineData("Conversation_id")]
    [InlineData("Thread-Id")]
    [InlineData("x-session-affinity")]
    public async Task Client_alias_seeds_are_stable_across_requests_without_inventing_forwarded_aliases(string name)
    {
        var first = AccountSessionIsolationService.Capture("client", new HeaderDictionary { [name] = "stable-client" }, "{}");
        var next = AccountSessionIsolationService.Capture("client", new HeaderDictionary { [name] = "stable-client" }, "{}");
        var a = await _service.ApplyAsync(first, _account, "Responses", false, "{}", null);
        var b = await _service.ApplyAsync(next, _account, "Responses", false, "{}", null);
        var other = await _service.ApplyAsync(next, _account with { AccountId = Guid.NewGuid() }, "Responses", false, "{}", null);
        a.ForwardHeaders["Session-Id"].Should().Be(b.ForwardHeaders["Session-Id"]).And.NotBe(other.ForwardHeaders["Session-Id"]);
        JsonNode.Parse(a.PreparedRequestBody)!["prompt_cache_key"]!.ToString().Should().Be(a.ForwardHeaders["Session-Id"]);
        JsonNode.Parse(b.PreparedRequestBody)!["prompt_cache_key"]!.ToString().Should().Be(a.ForwardHeaders["Session-Id"]);
        a.ForwardHeaders.Should().NotContainKey(name);
    }

    [Theory]
    [InlineData("OpenAI", "X-Session-ID")]
    [InlineData("Responses", "x-session-id")]
    [InlineData("Anthropic", "X-SESSION-ID")]
    [InlineData("Gemini", "X-Session-Id")]
    [InlineData("OpenAI", "X-Conversation-ID")]
    [InlineData("Responses", "x-conversation-id")]
    [InlineData("Anthropic", "X-CONVERSATION-ID")]
    [InlineData("Gemini", "X-Conversation-Id")]
    [InlineData("OpenAI", "x-session-affinity")]
    [InlineData("Responses", "X-SESSION-AFFINITY")]
    [InlineData("Anthropic", "x-session-affinity")]
    [InlineData("Gemini", "X-Session-Affinity")]
    public async Task Post_merge_aliases_are_isolated_for_all_protocols_without_inventing_headers(string protocol, string name)
    {
        var context = AccountSessionIsolationService.Capture("client", new HeaderDictionary { [name] = "original-client" }, "{}");
        var merged = new Dictionary<string, string> { [name] = "post-merge-value" };
        var first = await _service.ApplyAsync(context, _account, protocol, false, "{}", merged);
        var again = await _service.ApplyAsync(context, _account, protocol, false, "{}", merged);
        var other = await _service.ApplyAsync(context, _account with { AccountId = Guid.NewGuid() }, protocol, false, "{}", merged);
        first.ForwardHeaders[name].Should().NotBe("original-client").And.NotBe("post-merge-value")
            .And.Be(again.ForwardHeaders[name]).And.NotBe(other.ForwardHeaders[name]);
        merged[name].Should().Be("post-merge-value");
        var absent = await _service.ApplyAsync(context, _account, protocol, false, "{}", null);
        absent.ForwardHeaders.Should().NotContainKey(name);
    }

    [Theory]
    [InlineData("OpenAI")]
    [InlineData("Responses")]
    [InlineData("Anthropic")]
    [InlineData("Gemini")]
    public async Task Generated_affinity_uses_original_stable_seed_not_each_merged_random_value(string protocol)
    {
        var firstContext = AccountSessionIsolationService.Capture("client", new HeaderDictionary { ["Session-Id"] = "stable-session" }, "{}");
        var secondContext = AccountSessionIsolationService.Capture("client", new HeaderDictionary { ["session-id"] = "stable-session" }, "{}");
        var first = await _service.ApplyAsync(firstContext, _account, protocol, false, "{}", new Dictionary<string, string> { ["x-session-affinity"] = "ses_generated_one" });
        var next = await _service.ApplyAsync(secondContext, _account, protocol, false, "{}", new Dictionary<string, string> { ["X-Session-Affinity"] = "ses_generated_two" });
        var other = await _service.ApplyAsync(secondContext, _account with { AccountId = Guid.NewGuid() }, protocol, false, "{}", new Dictionary<string, string> { ["X-Session-Affinity"] = "ses_generated_two" });
        first.ForwardHeaders["x-session-affinity"].Should().Be(next.ForwardHeaders["x-session-affinity"])
            .And.NotBe(other.ForwardHeaders["x-session-affinity"]).And.NotBe("ses_generated_one");
    }

    [Theory]
    [InlineData("OpenAI", "Conversation_id")]
    [InlineData("Anthropic", "Thread-Id")]
    [InlineData("Gemini", "X-Claude-Code-Session-Id")]
    [InlineData("Anthropic", "Session-Id")]
    public async Task Other_known_session_headers_are_rewritten_after_merge_for_every_protocol(string protocol, string name)
    {
        var context = AccountSessionIsolationService.Capture("client", new HeaderDictionary { [name] = "client" }, "{}");
        var merged = new Dictionary<string, string> { [name] = "untrusted-merged" };
        var first = await _service.ApplyAsync(context, _account, protocol, false, "{}", merged);
        var other = await _service.ApplyAsync(context, _account with { AccountId = Guid.NewGuid() }, protocol, false, "{}", merged);
        first.ForwardHeaders[name].Should().NotBe("untrusted-merged").And.NotBe(other.ForwardHeaders[name]);
        (await _service.ApplyAsync(context, _account, protocol, false, "{}", null)).ForwardHeaders.Should().NotContainKey(name);
    }

    [Theory]
    [InlineData("workbuddy-cn")]
    [InlineData("workbuddy-global")]
    public async Task Workbuddy_vendor_identity_preserves_device_header_regardless_of_casing(string vendor)
    {
        var account = await CreateDeviceAccountAsync(vendor);
        var context = AccountSessionIsolationService.Capture("client", new HeaderDictionary { ["x-session-id"] = "client" }, "{}");
        foreach (var protocol in new[] { "OpenAI", "Responses", "Anthropic", "Gemini" })
        {
            var result = await _service.ApplyAsync(context, account, protocol, false, "{}", new Dictionary<string, string>
                { ["x-session-id"] = "device-fingerprint", ["X-Conversation-ID"] = "conversation" });
            result.ForwardHeaders["X-Session-ID"].Should().Be("device-fingerprint");
            result.ForwardHeaders["x-conversation-id"].Should().NotBe("conversation");
        }
    }

    [Fact]
    public async Task Other_device_vendor_and_spoofed_device_headers_do_not_bypass_isolation()
    {
        var account = await CreateDeviceAccountAsync("other-vendor");
        var context = AccountSessionIsolationService.Capture("client", new HeaderDictionary { ["X-Session-ID"] = "client" }, "{}");
        var result = await _service.ApplyAsync(context, account, "OpenAI", false, "{}", new Dictionary<string, string>
            { ["X-Session-ID"] = "client", ["User-Agent"] = "WorkBuddy/1", ["X-Machine-ID"] = "device" });
        result.ForwardHeaders["x-session-id"].Should().NotBe("client");
        var missing = new AccountIdentity("device-oauth", Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ApplyAsync(context, missing, "OpenAI", false, "{}",
            new Dictionary<string, string> { ["X-Session-ID"] = "client" }));
    }

    private async Task<AccountIdentity> CreateDeviceAccountAsync(string vendor)
    {
        var account = new DeviceOAuthAccount { VendorKey = vendor, DisplayName = "test", LinkedSiteId = Guid.NewGuid() };
        await _provider.GetRequiredService<ISqlSugarClient>().Insertable(account).ExecuteCommandAsync();
        return new AccountIdentity("device-oauth", account.Id);
    }

    [Fact]
    public async Task Metadata_only_codex_and_claude_sessions_survive_recapture_without_rewriting_device_or_opaque_identity()
    {
        const string codex = "{\"session_id\":\"metadata-session\",\"device_id\":\"device\"}";
        var first = AccountSessionIsolationService.Capture("client", new HeaderDictionary { ["X-Codex-Turn-Metadata"] = codex }, "{}");
        var next = AccountSessionIsolationService.Capture("client", new HeaderDictionary { ["X-Codex-Turn-Metadata"] = codex }, "{}");
        var a = await _service.ApplyAsync(first, _account, "Responses", false, "{}", null);
        var b = await _service.ApplyAsync(next, _account, "Responses", false, "{}", null);
        a.ForwardHeaders["Session-Id"].Should().Be(b.ForwardHeaders["Session-Id"]);
        var claudeBody = JsonSerializer.Serialize(new { metadata = new { user_id = "{\"session_id\":\"claude-session\",\"device_id\":\"device\"}" } });
        var c1 = AccountSessionIsolationService.Capture("client", new HeaderDictionary(), claudeBody);
        var c2 = AccountSessionIsolationService.Capture("client", new HeaderDictionary(), claudeBody);
        var ca = await _service.ApplyAsync(c1, _account, "Anthropic", false, claudeBody, null);
        var cb = await _service.ApplyAsync(c2, _account, "Anthropic", false, claudeBody, null);
        ca.PreparedRequestBody.Should().Be(cb.PreparedRequestBody);
        var structured = JsonNode.Parse(JsonNode.Parse(ca.PreparedRequestBody)!["metadata"]!["user_id"]!.ToString())!;
        structured["session_id"]!.ToString().Should().MatchRegex("^[0-9a-f]{64}$");
        structured["device_id"]!.ToString().Should().Be("device");
        var other = await _service.ApplyAsync(c1, _account with { AccountId = Guid.NewGuid() }, "Anthropic", false, claudeBody, null);
        other.PreparedRequestBody.Should().NotBe(ca.PreparedRequestBody);
        const string opaque = "{\"metadata\":{\"user_id\":\"opaque-user-identity\"}}";
        var preserved = await _service.ApplyAsync(c1, _account, "Anthropic", false, opaque, null);
        JsonNode.Parse(preserved.PreparedRequestBody)!["metadata"]!["user_id"]!.ToString().Should().Be("opaque-user-identity");
    }

    [Fact]
    public async Task Missing_http_seed_is_request_local_while_websocket_seed_is_connection_local()
    {
        var one = AccountSessionIsolationService.Capture("client", new HeaderDictionary(), "{}");
        var two = AccountSessionIsolationService.Capture("client", new HeaderDictionary(), "{}");
        async Task<string> Session(AccountSessionIsolationContext context) => (await _service.ApplyAsync(context, _account, "Responses", false, "{}", null)).ForwardHeaders["Session-Id"];
        (await Session(one)).Should().Be(await Session(one)).And.NotBe(await Session(two));
        var ws1 = AccountSessionIsolationService.Capture("client", new HeaderDictionary(), "{}", "connection-seed");
        var ws2 = AccountSessionIsolationService.Capture("client", new HeaderDictionary(), "{}", "connection-seed");
        (await Session(ws1)).Should().Be(await Session(ws2));
    }

    [Fact]
    public async Task Antigravity_overwrites_bridge_first_user_hash_with_positive_int63_from_original_client_seed()
    {
        var original = "{\"request\":{\"sessionId\":\"client-seed\"}}";
        var context = AccountSessionIsolationService.Capture("client", new HeaderDictionary(), original);
        var first = await _service.ApplyAsync(context, _account, "Gemini", true,
            "{\"request\":{\"sessionId\":\"first-user-hash\",\"labels\":{\"trajectory_id\":\"first-user-hash\",\"other\":\"keep\"}},\"userAgent\":\"unchanged\"}", null);
        var root = JsonNode.Parse(first.PreparedRequestBody)!;
        var session = root["request"]!["sessionId"]!.ToString();
        long.Parse(session).Should().BeInRange(1, long.MaxValue);
        root["request"]!["labels"]!["trajectory_id"]!.ToString().Should().Be(session);
        root["request"]!["labels"]!["other"]!.ToString().Should().Be("keep");
        root["userAgent"]!.ToString().Should().Be("unchanged");
        var second = await _service.ApplyAsync(context, _account, "Gemini", true, "{\"request\":{\"sessionId\":\"different-hash\"}}", null);
        JsonNode.Parse(second.PreparedRequestBody)!["request"]!["sessionId"]!.ToString().Should().Be(session);
    }

    [Fact]
    public async Task Structured_metadata_only_rewrites_known_fields_and_preserves_workbuddy_identity()
    {
        const string metadata = "{\"session_id\":\"client\",\"thread_id\":\"thread\",\"device_id\":\"device\",\"nested\":{\"session_id\":\"untouched\"}}";
        var context = AccountSessionIsolationService.Capture("client", new HeaderDictionary { ["X-Codex-Turn-Metadata"] = metadata }, "{}");
        var workbuddy = await CreateDeviceAccountAsync("workbuddy-cn");
        var result = await _service.ApplyAsync(context, workbuddy, "Responses", false, "{}", new Dictionary<string, string>
        { ["X-Codex-Turn-Metadata"] = metadata, ["X-Session-ID"] = "workbuddy-account-device", ["X-Machine-ID"] = "workbuddy-machine", ["XSessionID"] = "legacy-device", ["User-Agent"] = "vendor-agent" });
        var parsed = JsonNode.Parse(result.ForwardHeaders["X-Codex-Turn-Metadata"])!;
        parsed["session_id"]!.ToString().Should().Be(result.ForwardHeaders["Session-Id"]);
        parsed["thread_id"]!.ToString().Should().NotBe("thread");
        parsed["device_id"]!.ToString().Should().Be("device");
        parsed["nested"]!["session_id"]!.ToString().Should().Be("untouched");
        result.ForwardHeaders["X-Session-ID"].Should().Be("workbuddy-account-device");
        result.ForwardHeaders["X-Machine-ID"].Should().Be("workbuddy-machine");
        result.ForwardHeaders["XSessionID"].Should().Be("legacy-device");
        result.ForwardHeaders["User-Agent"].Should().Be("vendor-agent");
    }

    [Fact]
    public async Task Ordinary_api_sites_are_unchanged_and_affinity_is_client_scoped()
    {
        var context = AccountSessionIsolationService.Capture("client", new HeaderDictionary(), "{}");
        var result = await _service.ApplyAsync(context, null, "Responses", false, "{\"prompt_cache_key\":\"original\"}", new Dictionary<string, string> { ["Session-Id"] = "original" });
        result.ForwardHeaders["Session-Id"].Should().Be("original");
        result.PreparedRequestBody.Should().Be("{\"prompt_cache_key\":\"original\"}");
        AccountSessionIsolationService.ScopeAffinity("key-a", "same-conversation").Should().NotBe(AccountSessionIsolationService.ScopeAffinity("key-b", "same-conversation"));
    }

    [Theory]
    [InlineData("{\"model\":\"auto\",\"previous_response_id\":\"unknown-response\",\"input\":[]}")]
    [InlineData("{\"model\":\"auto\",\"conversation\":\"unknown-conversation\",\"input\":[]}")]
    [InlineData("{\"model\":\"auto\",\"input\":[{\"type\":\"item_reference\",\"id\":\"unknown-item\"}]}")]
    public async Task Unknown_references_fail_before_any_account_is_probed(string body)
    {
        var forwarder = new ResponsesFakeProxyForwardService { ManagedSource = "Codex" };
        await using var factory = new ResponsesWebApplicationFactory(forwarder);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        var response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("unknown_reference");
        forwarder.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Http_native_response_is_owned_before_exposure_and_known_owner_pins_followup()
    {
        var forwarder = new ResponsesFakeProxyForwardService();
        await using var factory = new ResponsesWebApplicationFactory(forwarder);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        var response = await client.PostAsync("/v1/responses", new StringContent("{\"model\":\"auto\",\"input\":[]}", Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var first = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = first.RootElement.GetProperty("id").GetString();
        var followup = await client.PostAsync("/v1/responses", new StringContent(JsonSerializer.Serialize(new { model = "auto", previous_response_id = id, input = Array.Empty<object>() }), Encoding.UTF8, "application/json"));
        followup.StatusCode.Should().Be(HttpStatusCode.OK, await followup.Content.ReadAsStringAsync());
        forwarder.Requests.Should().HaveCount(2);
        forwarder.Requests.Select(r => r.TargetBaseUrl).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Ordinary_site_with_codex_url_rejects_known_previous_response_before_normalizer_can_strip_it()
    {
        var forwarder = new ResponsesFakeProxyForwardService { BaseUrl = "https://chatgpt.com/backend-api/codex" };
        await using var factory = new ResponsesWebApplicationFactory(forwarder);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        var first = await client.PostAsync("/v1/responses", new StringContent("{\"model\":\"auto\",\"input\":[]}", Encoding.UTF8, "application/json"));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var next = await client.PostAsync("/v1/responses", new StringContent("{\"model\":\"auto\",\"previous_response_id\":\"resp_test123\",\"input\":[]}", Encoding.UTF8, "application/json"));
        next.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await next.Content.ReadAsStringAsync()).Should().Contain("reference_replay_required");
        forwarder.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Interrupted_response_created_is_owned_before_exposure_and_continuation_never_probes_fallback_account()
    {
        var forwarder = new ResponsesFakeProxyForwardService { StreamingLines =
        [
            "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_interrupted\",\"output\":[]}}", ""
        ] };
        await using var factory = new ResponsesWebApplicationFactory(forwarder);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        var stream = await client.PostAsync("/v1/responses", new StringContent("{\"model\":\"auto\",\"input\":[],\"stream\":true}", Encoding.UTF8, "application/json"));
        (await stream.Content.ReadAsStringAsync()).Should().Contain("resp_interrupted");
        await using var scope = factory.Services.CreateAsyncScope();
        var guard = scope.ServiceProvider.GetRequiredService<IUpstreamReferenceGuard>();
        var owner = await guard.ResolveRequestAsync("c3c3c3c3-c3c3-c3c3-c3c3-c3c3c3c3c3c3", "{\"previous_response_id\":\"resp_interrupted\"}");
        owner.Should().NotBeNull();
        owner!.Owner.ProviderKey.Should().Be("site");
        forwarder.Requests.Should().ContainSingle();
        var followup = await client.PostAsync("/v1/responses", new StringContent("{\"model\":\"auto\",\"previous_response_id\":\"resp_interrupted\",\"input\":[]}", Encoding.UTF8, "application/json"));
        followup.StatusCode.Should().Be(HttpStatusCode.OK);
        forwarder.Requests.Should().HaveCount(2);
        forwarder.Requests.Select(r => r.TargetBaseUrl).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Commit_failure_exposes_no_id_and_does_not_try_another_account()
    {
        var forwarder = new ResponsesFakeProxyForwardService();
        var guard = new FailingCommitGuard();
        await using var factory = new ResponsesWebApplicationFactory(forwarder, guard);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        var response = await client.PostAsync("/v1/responses", new StringContent("{\"model\":\"auto\",\"input\":[]}", Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("resp_test123").And.Contain("reference_store_unavailable");
        guard.Commits.Should().Be(1);
        forwarder.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Stream_commit_failure_after_first_chunk_settles_developer_trace_instead_of_staying_pending()
    {
        // 回归：引用守卫在流中拒绝输出（客户端已收到首个 chunk）时，追踪与用量必须照常收口，
        // 而不是异常穿透收口段让调用记录永远停在「等待」（2026-11-03 实例 1.0.1.45 实测症状）。
        var forwarder = new ResponsesFakeProxyForwardService { StreamingLines =
        [
            "data: {\"id\":\"resp_commit_fault\",\"object\":\"response\",\"created\":1,\"model\":\"auto\",\"status\":\"in_progress\",\"output\":[],\"type\":\"response.created\"}",
            string.Empty,
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\",\"output_index\":0,\"content_index\":0}",
            string.Empty,
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_commit_fault\",\"object\":\"response\",\"status\":\"completed\",\"output\":[]}}",
            string.Empty
        ] };
        var guard = new SecondCommitFailingGuard();
        await using var factory = new ResponsesWebApplicationFactory(forwarder, guard);
        using var client = factory.CreateClient();
        // CreateClient 触发 SeedAsync（建表+种子并清库），开关必须在 seed 之后打开并失效运行时设置缓存。
        await EnableDeveloperTraceAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");

        var stream = await client.PostAsync("/v1/responses",
            new StringContent("{\"model\":\"auto\",\"input\":[],\"stream\":true}", Encoding.UTF8, "application/json"));
        // 首 chunk 已写给客户端：它能看到响应头与首块内容，与真实症状一致。
        (await stream.Content.ReadAsStringAsync()).Should().Contain("resp_commit_fault");

        var store = factory.Services.GetRequiredService<DeveloperInvocationTraceStore>();
        var entry = store.List().Should().ContainSingle().Subject;
        entry.Attempts.Should().NotBeEmpty();
        entry.Attempts.Should().OnlyContain(a => !string.Equals(a.Status, "pending", StringComparison.OrdinalIgnoreCase),
            "守卫拒绝后收口为 fail，绝不许停在等待");
        entry.Attempts.Last().Status.Should().Be("fail");
        entry.Attempts.Last().ErrorMessage.Should().Contain("reference_store_unavailable");
    }

    private static async Task EnableDeveloperTraceAsync(WebApplicationFactory<Program> factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await db.Client.Queryable<SystemRuntimeSettings>().FirstAsync(s => s.Id == 1);
        if (settings is null)
        {
            settings = new SystemRuntimeSettings { Id = 1 };
            await db.Client.Insertable(settings).ExecuteCommandAsync();
        }
        settings.DeveloperFeaturesEnabled = true;
        settings.DeveloperTraceEnabled = true;
        await db.Client.Updateable(settings)
            .UpdateColumns(s => new { s.DeveloperFeaturesEnabled, s.DeveloperTraceEnabled })
            .ExecuteCommandAsync();
        // 宿主启动阶段可能已预热运行时设置缓存（TTL 300s），显式失效让本次请求读到新开关。
        factory.Services.GetRequiredService<ProxyRequestMetadataCache>().InvalidateRuntimeSettings();
    }

    [Fact]
    public async Task Websocket_exact_completed_reference_replays_full_local_history_but_mismatch_does_not_probe()
    {
        var forwarder = new ResponsesFakeProxyForwardService { ManagedSource = "Codex", StreamingLinesByCall =
        [
            ["data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_ws_exact\",\"output\":[{\"type\":\"message\",\"id\":\"assistant-1\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"first-answer\"}]}]}}", ""],
            ["data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_ws_next\",\"output\":[]}}", ""]
        ] };
        await using var factory = new ResponsesWebApplicationFactory(forwarder);
        using var client = factory.CreateClient();
        var wsClient = factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request => request.Headers["Authorization"] = "Bearer responses-test-key";
        using var socket = await wsClient.ConnectAsync(new Uri("ws://localhost/v1/responses"), CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        async Task Send(string body) => await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(body)), WebSocketMessageType.Text, true, timeout.Token);
        async Task<string> Receive()
        {
            var buffer = new byte[16384];
            var builder = new StringBuilder();
            WebSocketReceiveResult frame;
            do { frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token); builder.Append(Encoding.UTF8.GetString(buffer, 0, frame.Count)); }
            while (!frame.EndOfMessage);
            return builder.ToString();
        }
        await Send("{\"type\":\"response.create\",\"model\":\"auto\",\"input\":[{\"type\":\"message\",\"role\":\"user\",\"content\":\"first-question\"}]}");
        (await Receive()).Should().Contain("resp_ws_exact");
        await Send("{\"type\":\"response.append\",\"previous_response_id\":\"resp_ws_exact\",\"input\":[{\"type\":\"message\",\"role\":\"user\",\"content\":\"next-question\"}]}");
        (await Receive()).Should().Contain("resp_ws_next");
        forwarder.Requests.Should().HaveCount(2);
        forwarder.Requests[1].PreparedRequestBody.Should().Contain("first-question").And.Contain("first-answer").And.Contain("next-question").And.NotContain("previous_response_id");
        forwarder.Requests[0].ForwardHeaders["Session-Id"].Should().Be(forwarder.Requests[1].ForwardHeaders["Session-Id"]);
        await Send("{\"type\":\"response.append\",\"previous_response_id\":\"not-the-completed-response\",\"input\":[]}");
        (await Receive()).Should().Contain("unknown_reference");
        forwarder.Requests.Should().HaveCount(2);
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bridged_stream_synthetic_id_is_committed_before_exposure_and_rejected_for_native_replay(bool anthropic)
    {
        var forwarder = new ResponsesFakeProxyForwardService
        {
            IsAnthropicOnly = anthropic,
            IsOpenAiChatOnly = !anthropic,
            StreamingLines = anthropic
                ? ["event: message_start", "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_bridge_source\",\"role\":\"assistant\",\"content\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}}", "",
                   "event: content_block_start", "data: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}", "",
                   "event: content_block_delta", "data: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"bridge-content\"}}", "",
                   "event: message_stop", "data: {\"type\":\"message_stop\"}", ""]
                : ["data: {\"id\":\"chatcmpl_bridge_source\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"bridge-content\"},\"finish_reason\":null}]}", "",
                   "data: {\"id\":\"chatcmpl_bridge_source\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}", "", "data: [DONE]", ""]
        };
        var gate = new GatedCommitGuard();
        await using var factory = new ResponsesWebApplicationFactory(forwarder, gate);
        using var client = factory.CreateClient();
        gate.Inner = new UpstreamReferenceGuard(factory.Services.GetRequiredService<IUpstreamReferenceOwnerStore>());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        using var streamRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new StringContent("{\"model\":\"auto\",\"input\":[],\"stream\":true}", Encoding.UTF8, "application/json")
        };
        var pending = client.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead);
        try
        {
            await gate.Committed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            pending.IsCompleted.Should().BeFalse("the ID-bearing event is blocked at the pre-exposure ownership gate");
            gate.Native.Should().BeFalse();
            await using var scope = factory.Services.CreateAsyncScope();
            var rows = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Client.Queryable<UpstreamReferenceOwner>().ToListAsync();
            rows.Should().Contain(row => !row.IsNative && row.Kind == UpstreamReferenceKind.Response);
        }
        finally { gate.Release.TrySetResult(true); }
        var response = await pending;
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        body.Should().Contain(gate.ResponseId!);
        var next = await client.PostAsync("/v1/responses", new StringContent(JsonSerializer.Serialize(new { model = "auto", previous_response_id = gate.ResponseId, input = Array.Empty<object>() }), Encoding.UTF8, "application/json"));
        next.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await next.Content.ReadAsStringAsync()).Should().Contain("reference_replay_required");
        forwarder.Requests.Should().ContainSingle("a synthetic bridge ID must never probe a native upstream");
    }

    [Fact]
    public async Task Head_buffer_release_commit_failure_exposes_no_unowned_id_and_never_falls_back()
    {
        var guard = new FailingCommitGuard();
        var forwarder = new ResponsesFakeProxyForwardService { StreamHeadBufferEnabled = true, StreamingLines =
        [
            "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_buffered_unowned\",\"output\":[]}}", "",
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"release-buffer\"}", ""
        ] };
        forwarder.AfterStreamingLineAsync = (index, _) =>
        {
            if (index == 1) guard.Commits.Should().Be(0, "the warmup event must remain held until content releases the buffer");
            return Task.CompletedTask;
        };
        await using var factory = new ResponsesWebApplicationFactory(forwarder, guard);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        var response = await client.PostAsync("/v1/responses", new StringContent("{\"model\":\"auto\",\"input\":[],\"stream\":true}", Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        (await response.Content.ReadAsStringAsync()).Should().NotContain("resp_buffered_unowned").And.NotContain("release-buffer").And.Contain("reference_store_unavailable");
        guard.Commits.Should().Be(1);
        forwarder.Requests.Should().ContainSingle("commit failure must not release a held unowned ID or try the fallback site");
    }

    [Fact]
    public async Task Completed_event_ownership_survives_cancellation_after_stream_callback()
    {
        var forwarder = new ResponsesFakeProxyForwardService { CancelAfterStreamingCallbacks = true, StreamingLines =
        [
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_completed_then_canceled\",\"output\":[{\"id\":\"item_completed_then_canceled\",\"type\":\"message\",\"role\":\"assistant\",\"content\":[]}]}}", ""
        ] };
        await using var factory = new ResponsesWebApplicationFactory(forwarder);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        var response = await client.PostAsync("/v1/responses", new StringContent("{\"model\":\"auto\",\"input\":[],\"stream\":true}", Encoding.UTF8, "application/json"));
        (await response.Content.ReadAsStringAsync()).Should().Contain("resp_completed_then_canceled");
        await using var scope = factory.Services.CreateAsyncScope();
        var guard = scope.ServiceProvider.GetRequiredService<IUpstreamReferenceGuard>();
        var owner = await guard.ResolveRequestAsync("c3c3c3c3-c3c3-c3c3-c3c3-c3c3c3c3c3c3", "{\"previous_response_id\":\"resp_completed_then_canceled\",\"input\":[{\"type\":\"item_reference\",\"id\":\"item_completed_then_canceled\"}]}");
        owner.Should().NotBeNull();
        forwarder.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Websocket_reconnect_known_reference_has_no_replay_history_and_pipelined_stale_reference_is_rejected()
    {
        var forwarder = new ResponsesFakeProxyForwardService { StreamingLinesByCall =
        [
            ["data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_rapid_first\",\"output\":[]}}", ""],
            ["data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_rapid_second\",\"output\":[]}}", ""]
        ] };
        await using var factory = new ResponsesWebApplicationFactory(forwarder);
        using var client = factory.CreateClient();
        var wsClient = factory.Server.CreateWebSocketClient();
        wsClient.ConfigureRequest = request => request.Headers["Authorization"] = "Bearer responses-test-key";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var first = await wsClient.ConnectAsync(new Uri("ws://localhost/v1/responses"), timeout.Token);
        await SendSocketAsync(first, "{\"type\":\"response.create\",\"model\":\"auto\",\"input\":[]}", timeout.Token);
        (await ReceiveSocketAsync(first, timeout.Token)).Should().Contain("resp_rapid_first");
        // Queue both turns before reading either result. The second must observe the newly
        // completed ID, not the prior completion that was current when these frames were sent.
        await SendSocketAsync(first, "{\"type\":\"response.append\",\"previous_response_id\":\"resp_rapid_first\",\"input\":[]}", timeout.Token);
        await SendSocketAsync(first, "{\"type\":\"response.append\",\"previous_response_id\":\"resp_rapid_first\",\"input\":[]}", timeout.Token);
        (await ReceiveSocketAsync(first, timeout.Token)).Should().Contain("resp_rapid_second");
        (await ReceiveSocketAsync(first, timeout.Token)).Should().Contain("reference_replay_required");
        forwarder.Requests.Should().HaveCount(2);
        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "reconnect", timeout.Token);
        using var reconnected = await wsClient.ConnectAsync(new Uri("ws://localhost/v1/responses"), timeout.Token);
        await SendSocketAsync(reconnected, "{\"type\":\"response.create\",\"model\":\"auto\",\"previous_response_id\":\"resp_rapid_second\",\"input\":[]}", timeout.Token);
        (await ReceiveSocketAsync(reconnected, timeout.Token)).Should().Contain("reference_replay_required");
        forwarder.Requests.Should().HaveCount(2, "durable ownership does not grant a new connection complete local replay history");
        await reconnected.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
    }

    private static Task SendSocketAsync(WebSocket socket, string json, CancellationToken ct)
        => socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, ct);

    private static async Task<string> ReceiveSocketAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16384];
        var builder = new StringBuilder();
        WebSocketReceiveResult frame;
        do
        {
            frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (frame.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("Unexpected WebSocket close.");
            builder.Append(Encoding.UTF8.GetString(buffer, 0, frame.Count));
        } while (!frame.EndOfMessage);
        return builder.ToString();
    }

    [Theory]
    [InlineData("/v1/responses")]
    [InlineData("/v1/chat/completions")]
    [InlineData("/v1/messages")]
    public async Task Exhausted_streaming_network_failure_returns_explicit_json_instead_of_406(string path)
    {
        var forwarder = new ResponsesFakeProxyForwardService { FailStreamingBeforeCallbacks = true };
        await using var factory = new ResponsesWebApplicationFactory(forwarder);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        var response = await client.PostAsync(path, new StringContent("{\"model\":\"auto\",\"input\":[],\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"max_tokens\":16,\"stream\":true}", Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        (await response.Content.ReadAsStringAsync()).Should().Contain("network_policy_unavailable");
        forwarder.Requests.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Credential_reference_owner_is_restart_stable_secret_sensitive_and_managed_rotation_stable()
    {
        var site = Guid.NewGuid();
        var key = Guid.NewGuid();
        var first = await _service.DeriveReferenceOwnerAsync(null, site, key, "credential-a");
        var restarted = new AccountSessionIsolationService(new SessionIsolationDeriver(new SessionIsolationKeyStore(
            _provider.GetRequiredService<IServiceScopeFactory>(), _provider.GetRequiredService<IDataProtectionProvider>())));
        (await restarted.DeriveReferenceOwnerAsync(null, site, key, "credential-a")).Should().Be(first);
        (await restarted.DeriveReferenceOwnerAsync(null, site, key, "credential-b")).Should().NotBe(first);
        (await restarted.DeriveReferenceOwnerAsync(null, site, null, "credential-a")).Should().NotBe(first);
        (await restarted.DeriveReferenceOwnerAsync(_account, site, key, "token-a")).Should().Be(_account);
        (await restarted.DeriveReferenceOwnerAsync(_account, site, key, "token-b")).Should().Be(_account);
        var custom = await _service.DeriveReferenceOwnerAsync(null, site, key, "credential-a", credentialHeaders:
            new Dictionary<string, string> { ["X-Auth-Token"] = "secret-a", ["User-Agent"] = "agent" });
        (await restarted.DeriveReferenceOwnerAsync(null, site, key, "credential-a", credentialHeaders:
            new Dictionary<string, string> { ["user-agent"] = "dynamic-${guid}", ["x-auth-token"] = "secret-a", ["Session-Id"] = "${guid}" })).Should().Be(custom);
        (await restarted.DeriveReferenceOwnerAsync(null, site, key, "credential-a", credentialHeaders:
            new Dictionary<string, string> { ["X-Auth-Token"] = "secret-b", ["User-Agent"] = "agent" })).Should().NotBe(custom);
        var dynamicCustom = () => _service.DeriveReferenceOwnerAsync(null, site, key, "credential-a", credentialHeaders:
            new Dictionary<string, string> { ["X-Auth-Token"] = "${guid}" });
        (await Assert.ThrowsAsync<UpstreamReferenceException>(dynamicCustom)).Code.Should().Be("invalid_reference_context");
    }

    [Theory]
    [InlineData("x-opencode-request", "msg_${nanoid:12}")]
    [InlineData("X-Client-Request-Id", "${guid}")]
    [InlineData("X-Codex-Window-Id", "${guid}:0")]
    [InlineData("x-query-id", "${guid}")]
    [InlineData("x-request-id", "${guid}")]
    [InlineData("x-zcode-trace-id", "${guid}")]
    public async Task Known_builtin_dynamic_request_templates_do_not_change_owner_but_overrides_fail_closed(string name, string template)
    {
        var site = Guid.NewGuid();
        var baseline = await _service.DeriveReferenceOwnerAsync(null, site, null, "credential");
        var withTemplate = await _service.DeriveReferenceOwnerAsync(null, site, null, "credential", credentialHeaders:
            new Dictionary<string, string> { [name.ToUpperInvariant()] = template });
        withTemplate.Should().Be(baseline);
        AccountSessionIsolationService.IsReferenceIdentityHeader(name, template).Should().BeFalse();
        AccountSessionIsolationService.IsReferenceIdentityHeader(name, template + "-override").Should().BeTrue();
        var unknownDynamic = () => _service.DeriveReferenceOwnerAsync(null, site, null, "credential", credentialHeaders:
            new Dictionary<string, string> { [name] = template + "-override" });
        (await Assert.ThrowsAsync<UpstreamReferenceException>(unknownDynamic)).Code.Should().Be("invalid_reference_context");
        var custom = await _service.DeriveReferenceOwnerAsync(null, site, null, "credential", credentialHeaders:
            new Dictionary<string, string> { [name] = "custom-token" });
        custom.Should().NotBe(baseline);
    }

    [Theory]
    [InlineData("OpenCode", "x-opencode-request")]
    [InlineData("CodexCli", "X-Client-Request-Id")]
    [InlineData("CodexVsCode", "X-Codex-Window-Id")]
    [InlineData("ZCode", "x-zcode-trace-id")]
    public async Task Ordinary_responses_accepts_merged_builtin_header_profile_with_dynamic_templates(string profile, string dynamicHeader)
    {
        var forwarder = new ResponsesFakeProxyForwardService();
        await using var factory = new ResponsesWebApplicationFactory(forwarder);
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var siteId = Guid.Parse("a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1");
        await db.Client.Updateable<Site>().SetColumns(s => s.ClientEmulation == profile).Where(s => s.Id == siteId).ExecuteCommandAsync();
        factory.Services.GetRequiredService<ProxyRequestMetadataCache>().InvalidateRouteTargets();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        var response = await client.PostAsync("/v1/responses", new StringContent("{\"model\":\"auto\",\"input\":[]}", Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        forwarder.Requests.Should().ContainSingle();
        forwarder.Requests[0].ForwardHeaders.Should().ContainKey(dynamicHeader);
        forwarder.Requests[0].ForwardHeaders[dynamicHeader].Should().NotContain("${");
    }

    [Theory]
    [InlineData("site-key")]
    [InlineData("legacy-api-key")]
    [InlineData("Authorization")]
    [InlineData("Chatgpt-Account-Id")]
    [InlineData("X-Auth-Token")]
    public async Task Replacing_ordinary_credential_material_invalidates_old_native_reference_without_forwarding(string change)
    {
        var forwarder = new ResponsesFakeProxyForwardService();
        await using var factory = new ResponsesWebApplicationFactory(forwarder);
        using var client = factory.CreateClient();
        var siteId = Guid.Parse("a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1");
        var keyId = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = factory.Services.GetRequiredService<ProxyRequestMetadataCache>();
        var originalHeaders = JsonSerializer.Serialize(new Dictionary<string, string> { [change] = "account-a" });
        var replacementHeaders = JsonSerializer.Serialize(new Dictionary<string, string> { [change] = "account-b" });
        if (change == "site-key")
            await db.Client.Insertable(new SiteKey { Id = keyId, SiteId = siteId, KeyValue = "credential-a" }).ExecuteCommandAsync();
        else if (change != "legacy-api-key")
            await db.Client.Updateable<Site>().SetColumns(s => s.ExtraHeadersJson == originalHeaders).Where(s => s.Id == siteId).ExecuteCommandAsync();
        cache.InvalidateRouteTargets();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        var first = await client.PostAsync("/v1/responses", new StringContent("{\"model\":\"auto\",\"input\":[]}", Encoding.UTF8, "application/json"));
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        forwarder.Requests.Should().ContainSingle();
        if (change == "site-key")
            await db.Client.Updateable<SiteKey>().SetColumns(k => k.KeyValue == "credential-b").Where(k => k.Id == keyId).ExecuteCommandAsync();
        else if (change == "legacy-api-key")
            await db.Client.Updateable<Site>().SetColumns(s => s.ApiKey == "credential-b").Where(s => s.Id == siteId).ExecuteCommandAsync();
        else
            await db.Client.Updateable<Site>().SetColumns(s => s.ExtraHeadersJson == replacementHeaders).Where(s => s.Id == siteId).ExecuteCommandAsync();
        cache.InvalidateRouteTargets();
        var followup = await client.PostAsync("/v1/responses", new StringContent("{\"model\":\"auto\",\"previous_response_id\":\"resp_test123\",\"input\":[]}", Encoding.UTF8, "application/json"));
        followup.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await followup.Content.ReadAsStringAsync()).Should().Contain("reference_owner_mismatch");
        forwarder.Requests.Should().ContainSingle("a key row ID is not a credential instance after its secret or account selector is replaced");
    }

    [Fact]
    public async Task Dynamic_unknown_credential_header_fails_before_upstream_can_expose_an_unowned_reference()
    {
        var forwarder = new ResponsesFakeProxyForwardService();
        await using var factory = new ResponsesWebApplicationFactory(forwarder);
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var siteId = Guid.Parse("a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1");
        var headers = JsonSerializer.Serialize(new Dictionary<string, string> { ["X-Auth-Token"] = "${guid}" });
        await db.Client.Updateable<Site>().SetColumns(s => s.ExtraHeadersJson == headers).Where(s => s.Id == siteId).ExecuteCommandAsync();
        factory.Services.GetRequiredService<ProxyRequestMetadataCache>().InvalidateRouteTargets();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        var response = await client.PostAsync("/v1/responses", new StringContent("{\"model\":\"auto\",\"input\":[]}", Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("invalid_reference_context").And.NotContain("resp_test123");
        forwarder.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Managed_token_replacement_keeps_native_reference_owner_and_dynamic_ordinary_auth_fails_closed()
    {
        var forwarder = new ResponsesFakeProxyForwardService { ManagedSource = "Codex" };
        await using var factory = new ResponsesWebApplicationFactory(forwarder);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        var first = await client.PostAsync("/v1/responses", new StringContent("{\"model\":\"auto\",\"input\":[]}", Encoding.UTF8, "application/json"));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var siteId = Guid.Parse("a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1");
        await db.Client.Updateable<Site>().SetColumns(s => s.ApiKey == "refreshed-managed-token").Where(s => s.Id == siteId).ExecuteCommandAsync();
        factory.Services.GetRequiredService<ProxyRequestMetadataCache>().InvalidateRouteTargets();
        var restriction = await factory.Services.GetRequiredService<IUpstreamReferenceGuard>().ResolveRequestAsync("c3c3c3c3-c3c3-c3c3-c3c3-c3c3c3c3c3c3", "{\"previous_response_id\":\"resp_test123\"}");
        restriction!.Owner.Should().Be(forwarder.Requests[0].AccountIdentity);
        // Codex itself rejects previous_response_id; prove owner continuity independently of
        // that protocol restriction, and prove dynamic ordinary auth cannot be fingerprinted.
        var act = () => _service.DeriveReferenceOwnerAsync(null, siteId, null, "base-key", credentialHeaders:
            new Dictionary<string, string> { ["Authorization"] = "Bearer ${guid}" });
        (await Assert.ThrowsAsync<UpstreamReferenceException>(act)).Code.Should().Be("invalid_reference_context");
    }

    private sealed class GatedCommitGuard : IUpstreamReferenceGuard
    {
        public IUpstreamReferenceGuard Inner { get; set; } = null!;
        public TaskCompletionSource<bool> Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Native { get; private set; }
        public string? ResponseId { get; private set; }
        public Task<UpstreamReferenceRestriction?> ResolveRequestAsync(string clientScope, string originalRawJson, CancellationToken ct = default)
            => Inner.ResolveRequestAsync(clientScope, originalRawJson, ct);
        public async Task CommitOutputAsync(UpstreamReferenceContext context, string bodyOrEventJson, bool native, CancellationToken ct = default)
        {
            await Inner.CommitOutputAsync(context, bodyOrEventJson, native, ct);
            var root = JsonNode.Parse(bodyOrEventJson);
            var id = root?["response"]?["id"]?.ToString();
            if (ResponseId is null && id is not null)
            {
                ResponseId = id;
                Native = native;
                Committed.TrySetResult(true);
                await Release.Task.WaitAsync(ct);
            }
        }
    }

    private sealed class FailingCommitGuard : IUpstreamReferenceGuard
    {
        public int Commits { get; private set; }
        public Task<UpstreamReferenceRestriction?> ResolveRequestAsync(string clientScope, string originalRawJson, CancellationToken ct = default)
            => Task.FromResult<UpstreamReferenceRestriction?>(null);
        public Task CommitOutputAsync(UpstreamReferenceContext context, string bodyOrEventJson, bool native, CancellationToken ct = default)
        {
            Commits++;
            throw new UpstreamReferenceException("reference_store_unavailable", "Reference ownership storage is unavailable.");
        }
    }

    /// <summary>首个 chunk 的提交放行（客户端能看到内容），从第二次提交起拒绝——模拟守卫在流中段拦截。</summary>
    private sealed class SecondCommitFailingGuard : IUpstreamReferenceGuard
    {
        private int _commits;
        public Task<UpstreamReferenceRestriction?> ResolveRequestAsync(string clientScope, string originalRawJson, CancellationToken ct = default)
            => Task.FromResult<UpstreamReferenceRestriction?>(null);
        public Task CommitOutputAsync(UpstreamReferenceContext context, string bodyOrEventJson, bool native, CancellationToken ct = default)
            => Interlocked.Increment(ref _commits) >= 2
                ? throw new UpstreamReferenceException("reference_store_unavailable", "Reference ownership storage is unavailable.")
                : Task.CompletedTask;
    }

    public void Dispose()
    {
        _provider.Dispose();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
