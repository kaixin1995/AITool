using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Web.Controllers.Proxy;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AITool.IntegrationTests.Proxy;

public sealed class OrdinaryReferenceHeaderRegressionTests
{
    // SqlSugar evaluates this member via reflection when translating Where expressions.
    public static readonly Guid SiteId = Guid.Parse("a1a1a1a1-a1a1-a1a1-a1a1-a1a1a1a1a1a1");

    [Fact]
    public async Task Responses_endpoint_uses_same_ordinary_owner_for_output_and_route_filter_and_never_sends_after_header_rotation()
    {
        var forwarder = new ResponsesFakeProxyForwardService();
        await using var factory = new ResponsesWebApplicationFactory(forwarder);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = factory.Services.GetRequiredService<ProxyRequestMetadataCache>();
        await db.Client.Updateable<Site>().SetColumns(s => s.ExtraHeadersJson == "{\"X-Auth-Token\":\"A\"}")
            .Where(s => s.Id == SiteId).ExecuteCommandAsync();
        cache.InvalidateRouteTargets();

        var initial = await Post(client, "{\"model\":\"auto\",\"input\":[]}");
        initial.StatusCode.Should().Be(HttpStatusCode.OK, await initial.Content.ReadAsStringAsync());
        forwarder.Requests.Should().ContainSingle();
        forwarder.Requests[0].ForwardHeaders!["X-Auth-Token"].Should().Be("A");

        // Header spelling changes do not change identity, and the same owner must match
        // the output registration when a reference is used.
        await db.Client.Updateable<Site>().SetColumns(s => s.ExtraHeadersJson == "{\"x-auth-token\":\"A\"}")
            .Where(s => s.Id == SiteId).ExecuteCommandAsync();
        cache.InvalidateRouteTargets();
        var same = await Post(client, "{\"model\":\"auto\",\"previous_response_id\":\"resp_test123\",\"input\":[]}");
        same.StatusCode.Should().Be(HttpStatusCode.OK, await same.Content.ReadAsStringAsync());
        forwarder.Requests.Should().HaveCount(2);

        await db.Client.Updateable<Site>().SetColumns(s => s.ExtraHeadersJson == "{\"x-auth-token\":\"B\"}")
            .Where(s => s.Id == SiteId).ExecuteCommandAsync();
        cache.InvalidateRouteTargets();
        var different = await Post(client, "{\"model\":\"auto\",\"previous_response_id\":\"resp_test123\",\"input\":[]}");
        different.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await different.Content.ReadAsStringAsync()).Should().Contain("reference_owner_mismatch");
        forwarder.Requests.Should().HaveCount(2, "a rejected continuation must not cause another send");
    }

    [Fact]
    public async Task Ordinary_responses_without_reference_accept_builtin_dynamic_emulation_headers()
    {
        var forwarder = new ResponsesFakeProxyForwardService();
        await using var factory = new ResponsesWebApplicationFactory(forwarder);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var profileJson = HeaderProfileCatalogService.GetDefaultBuiltInProfiles().Single(p => p.Key == "OpenCode").HeadersJson;
        await db.Client.Updateable<Site>().SetColumns(s => new Site { ClientEmulation = "OpenCode", ExtraHeadersJson = profileJson })
            .Where(s => s.Id == SiteId).ExecuteCommandAsync();
        factory.Services.GetRequiredService<ProxyRequestMetadataCache>().InvalidateRouteTargets();
        var result = await Post(client, "{\"model\":\"auto\",\"input\":[]}");
        result.StatusCode.Should().Be(HttpStatusCode.OK, await result.Content.ReadAsStringAsync());
        forwarder.Requests.Should().ContainSingle();
        forwarder.Requests[0].ForwardHeaders!.Should().ContainKey("User-Agent").And.ContainKey("X-Session-Id");
    }

    [Theory]
    [InlineData("OpenCode", "x-opencode-request")]
    [InlineData("ZCode", "x-query-id")]
    [InlineData("ZCode", "x-request-id")]
    [InlineData("ZCode", "x-zcode-trace-id")]
    [InlineData("CodexCli", "X-Client-Request-Id")]
    [InlineData("CodexCli", "X-Codex-Window-Id")]
    [InlineData("CodexVsCode", "X-Client-Request-Id")]
    [InlineData("CodexVsCode", "X-Codex-Window-Id")]
    public async Task Responses_endpoint_accepts_real_builtin_profile_templates_and_continues_with_same_owner(string preset, string dynamicHeader)
    {
        // Pin the real built-in profile at the site layer so this test does not depend on
        // a mutable local catalog; metadata cache still merges and evaluates it normally.
        var profile = HeaderProfileCatalogService.GetDefaultBuiltInProfiles().Single(p => p.Key == preset);
        var profileJson = profile.HeadersJson;
        var forwarder = new ResponsesFakeProxyForwardService();
        await using var factory = new ResponsesWebApplicationFactory(forwarder);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Client.Updateable<Site>().SetColumns(s => new Site { ClientEmulation = preset, ExtraHeadersJson = profileJson })
            .Where(s => s.Id == SiteId).ExecuteCommandAsync();
        factory.Services.GetRequiredService<ProxyRequestMetadataCache>().InvalidateRouteTargets();
        var initial = await Post(client, "{\"model\":\"auto\",\"input\":[]}");
        initial.StatusCode.Should().Be(HttpStatusCode.OK, await initial.Content.ReadAsStringAsync());
        forwarder.Requests.Should().ContainSingle();
        var evaluated = forwarder.Requests[0].ForwardHeaders[dynamicHeader];
        evaluated.Should().NotContain("${");
        var next = await Post(client, "{\"model\":\"auto\",\"previous_response_id\":\"resp_test123\",\"input\":[]}");
        next.StatusCode.Should().Be(HttpStatusCode.OK, await next.Content.ReadAsStringAsync());
        forwarder.Requests.Should().HaveCount(2);
        forwarder.Requests[1].ForwardHeaders[dynamicHeader].Should().NotBe(evaluated);
    }

    [Theory]
    [InlineData("OpenCode", "x-opencode-request", "disabled")]
    [InlineData("ZCode", "x-query-id", "disabled")]
    [InlineData("CodexCli", "X-Codex-Window-Id", "disabled")]
    [InlineData("CodexVsCode", "X-Client-Request-Id", "disabled")]
    [InlineData("OpenCode", "x-opencode-request", "removed")]
    [InlineData("ZCode", "x-query-id", "removed")]
    [InlineData("CodexCli", "X-Codex-Window-Id", "removed")]
    [InlineData("CodexVsCode", "X-Client-Request-Id", "removed")]
    public async Task Source_default_templates_work_when_profile_disabled_or_template_removed(string preset, string dynamicHeader, string mode)
    {
        var profiles = HeaderProfileCatalogService.GetDefaultBuiltInProfiles();
        var profile = profiles.Single(p => p.Key == preset);
        if (mode == "disabled") profile.IsEnabled = false;
        else
        {
            var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(profile.HeadersJson!)!;
            headers.Remove(dynamicHeader);
            profile.HeadersJson = JsonSerializer.Serialize(headers);
        }
        var catalog = new TestProfileCatalog(profiles);
        var forwarder = new ResponsesFakeProxyForwardService();
        await using var baseFactory = new ResponsesWebApplicationFactory(forwarder);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHeaderProfileCatalogService>();
            services.AddSingleton<IHeaderProfileCatalogService>(catalog);
        }));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Client.Updateable<Site>().SetColumns(s => s.ClientEmulation == preset)
            .Where(s => s.Id == SiteId).ExecuteCommandAsync();
        factory.Services.GetRequiredService<ProxyRequestMetadataCache>().InvalidateRouteTargets();
        var initial = await Post(client, "{\"model\":\"auto\",\"input\":[]}");
        initial.StatusCode.Should().Be(HttpStatusCode.OK, await initial.Content.ReadAsStringAsync());
        forwarder.Requests.Should().ContainSingle();
        var firstValue = forwarder.Requests[0].ForwardHeaders[dynamicHeader];
        firstValue.Should().NotContain("${");
        var followup = await Post(client, "{\"model\":\"auto\",\"previous_response_id\":\"resp_test123\",\"input\":[]}");
        followup.StatusCode.Should().Be(HttpStatusCode.OK, await followup.Content.ReadAsStringAsync());
        forwarder.Requests.Should().HaveCount(2);
        forwarder.Requests[1].ForwardHeaders[dynamicHeader].Should().NotBe(firstValue);
    }

    [Theory]
    [InlineData("OpenCode", "x-opencode-request")]
    [InlineData("ZCode", "x-query-id")]
    [InlineData("CodexCli", "X-Codex-Window-Id")]
    [InlineData("CodexVsCode", "X-Client-Request-Id")]
    public async Task Unsafe_override_of_builtin_dynamic_template_is_rejected_without_send(string preset, string header)
    {
        var forwarder = new ResponsesFakeProxyForwardService();
        await using var factory = new ResponsesWebApplicationFactory(forwarder);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var headersJson = JsonSerializer.Serialize(new Dictionary<string, string> { [header] = "Bearer ${guid}" });
        await db.Client.Updateable<Site>().SetColumns(s => new Site { ClientEmulation = preset, ExtraHeadersJson = headersJson })
            .Where(s => s.Id == SiteId).ExecuteCommandAsync();
        factory.Services.GetRequiredService<ProxyRequestMetadataCache>().InvalidateRouteTargets();
        var result = await Post(client, "{\"model\":\"auto\",\"input\":[]}");
        result.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await result.Content.ReadAsStringAsync()).Should().Contain("invalid_reference_context");
        forwarder.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Ordinary_unknown_dynamic_configured_header_is_rejected_before_send_even_without_reference()
    {
        var forwarder = new ResponsesFakeProxyForwardService();
        await using var factory = new ResponsesWebApplicationFactory(forwarder);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "responses-test-key");
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Client.Updateable<Site>().SetColumns(s => s.ExtraHeadersJson == "{\"X-Custom-Identity\":\"${guid}\"}")
            .Where(s => s.Id == SiteId).ExecuteCommandAsync();
        factory.Services.GetRequiredService<ProxyRequestMetadataCache>().InvalidateRouteTargets();
        var result = await Post(client, "{\"model\":\"auto\",\"input\":[]}");
        result.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await result.Content.ReadAsStringAsync()).Should().Contain("invalid_reference_context");
        forwarder.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Merged_header_snapshot_requires_case_insensitive_names_exact_values_and_rejects_unknown_additions()
    {
        AccountSessionIsolationService.IsReferenceIdentityHeader("X-Auth-Token").Should().BeTrue();
        AccountSessionIsolationService.IsReferenceIdentityHeader("session-id").Should().BeFalse();
        AccountSessionIsolationService.IsReferenceIdentityHeader("user-agent").Should().BeFalse();
        AccountSessionIsolationService.IsReferenceIdentityHeader("X-Client-Request-Id", "${guid}").Should().BeFalse();
        AccountSessionIsolationService.IsReferenceIdentityHeader("X-Client-Request-Id", "credential-A").Should().BeTrue();
        Validate(new() { ["X-Client-Request-Id"] = "${guid}" }, new() { ["x-client-request-id"] = "evaluated-guid" });
        Reject(new() { ["X-Client-Request-Id"] = "credential-A" }, new() { ["x-client-request-id"] = "credential-B" });
        var configured = new Dictionary<string, string> { ["X-Auth-Token"] = "A" };
        Validate(configured, new() { ["x-auth-token"] = "A", ["Session-Id"] = "dynamic", ["USER-AGENT"] = "dynamic" });
        Reject(configured, new() { ["x-auth-token"] = "B" });
        Reject(configured, new() { ["x-auth-token"] = "a" });
        Reject(configured, new());
        Reject(configured, new() { ["X-Auth-Token"] = "A", ["X-Custom-Identity"] = "secret" });
        Reject(configured, new() { ["X-Auth-Token"] = "A", ["x-request-id"] = "unexpected-secret" });
        // These configured fields are explicitly excluded from the owner as session/client metadata.
        Validate(new() { ["User-Agent"] = "client-${guid}", ["Session-Id"] = "${guid}" },
            new() { ["User-Agent"] = "client-123", ["session-id"] = "456" });
    }

    private static readonly MethodInfo Validator = typeof(OpenAiProxyController).GetMethod("ValidateOrdinaryReferenceHeaders", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void Validate(Dictionary<string, string> configured, Dictionary<string, string> final)
        => Validator.Invoke(null, new object[] { configured, final });

    private static void Reject(Dictionary<string, string> configured, Dictionary<string, string> final)
    {
        var error = Assert.Throws<TargetInvocationException>(() => Validate(configured, final));
        error.InnerException.Should().BeOfType<UpstreamReferenceException>()
            .Which.Code.Should().Be("invalid_reference_context");
    }

    private sealed class TestProfileCatalog(IReadOnlyList<HeaderProfile> profiles) : IHeaderProfileCatalogService
    {
        public Task<IReadOnlyList<HeaderProfile>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(profiles);
        public Task<HeaderProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(profiles.FirstOrDefault(p => p.Id == id));
        public Task<HeaderProfile?> GetByKeyAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(profiles.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)));
        public Task<IReadOnlyDictionary<string, string>> GetActiveProfilesDictionaryAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(profiles.Where(p => p.IsEnabled && p.HeadersJson is not null).ToDictionary(p => p.Key, p => p.HeadersJson!, StringComparer.OrdinalIgnoreCase));
        public Task<HeaderProfile> CreateAsync(HeaderProfile profile, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<HeaderProfile?> UpdateAsync(Guid id, Action<HeaderProfile> updateAction, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<HeaderProfile>> ResetBuiltInsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string json)
        => client.PostAsync("/v1/responses", new StringContent(json, Encoding.UTF8, "application/json"));
}
