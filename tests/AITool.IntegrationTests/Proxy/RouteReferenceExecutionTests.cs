using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Domain.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AITool.IntegrationTests.Proxy;

public sealed class RouteReferenceExecutionTests
{
    private const string Parent = "reference-parent";
    private const string Source = "reference-source";
    private const string AllDayRange = "[{\"start\":\"00:00\",\"end\":\"23:59\"}]";

    [Theory]
    [InlineData("/v1/chat/completions", false)]
    [InlineData("/v1/chat/completions", true)]
    [InlineData("/v1/messages", false)]
    [InlineData("/v1/messages", true)]
    [InlineData("/v1/responses", false)]
    [InlineData("/v1/responses", true)]
    public async Task Alias_leaves_execute_in_outer_order_deduplicate_and_preserve_trace_provenance(string endpoint, bool streaming)
    {
        var fake = Forwarder("remote-e");
        await using var baseFactory = new ProxyFallbackWebApplicationFactory(fake);
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IProxyForwardService>();
            services.AddSingleton<IProxyForwardService>(new IsolatedStreamingForwarder(fake));
        }));
        using var client = factory.CreateClient();
        var graph = await SeedAsync(factory);

        using var response = await client.SendAsync(Request(endpoint, Parent, streaming));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        fake.Requests.Select(r => r.TargetModelName).Should().Equal(new[] { "remote-a", "remote-b", "remote-c", "remote-e" },
            "A -> [source A,B,C] -> E must skip equivalent A but retain every distinct source alias in order");
        foreach (var request in fake.Requests)
        {
            using var prepared = JsonDocument.Parse(request.PreparedRequestBody!);
            prepared.RootElement.GetProperty("model").GetString().Should().Be(request.TargetModelName);
        }

        var store = factory.Services.GetRequiredService<DeveloperInvocationTraceStore>();
        var trace = store.List().Single(t => t.RequestModel == Parent);
        trace.Attempts.Select(a => a.AttemptedModel).Should().Equal("alias-a", "alias-b", "alias-c", "alias-e");
        trace.Attempts.Select(a => a.FallbackGroupId).Should().Equal(graph.Direct.Id, graph.Reference.Id, graph.Reference.Id, graph.Last.Id);
        foreach (var attempt in trace.Attempts.Skip(1).Take(2))
        {
            attempt.SourceRouteEntryId.Should().Be(graph.Source.Id);
            attempt.SourceRouteEntryName.Should().Be(Source);
            attempt.SourcePoolId.Should().Be(graph.Pool.Id);
        }
        trace.Attempts[0].SourceRouteEntryId.Should().BeNull();
        trace.Attempts[^1].SourcePoolId.Should().BeNull();
        JsonSerializer.Serialize(trace).Should().NotContain("upstream-secret-");

        // List/Get return independent attempt snapshots; changing diagnostics cannot corrupt stored provenance.
        trace.Attempts[1].SourceRouteEntryName = "mutated";
        trace.Attempts[1].SourcePoolId = null;
        trace.Attempts[1].FallbackGroupId = null;
        var fetched = store.Get(trace.TraceId)!;
        fetched.Attempts[1].SourceRouteEntryName.Should().Be(Source);
        fetched.Attempts[1].SourcePoolId.Should().Be(graph.Pool.Id);
        fetched.Attempts[1].FallbackGroupId.Should().Be(graph.Reference.Id);
        fetched.Attempts[1].SourceRouteEntryId = null;
        store.List().Single(t => t.TraceId == trace.TraceId).Attempts[1].SourceRouteEntryId.Should().Be(graph.Source.Id);
    }

    [Theory]
    [InlineData("/v1/chat/completions")]
    [InlineData("/v1/messages")]
    [InlineData("/v1/responses")]
    public async Task Parent_acl_allows_referenced_leaves_but_not_direct_access_to_source(string endpoint)
    {
        var fake = Forwarder("remote-b");
        await using var factory = new ProxyFallbackWebApplicationFactory(fake);
        using var client = factory.CreateClient();
        await SeedAsync(factory, allowedRoute: Parent);

        using var allowed = await client.SendAsync(Request(endpoint, Parent));
        allowed.StatusCode.Should().Be(HttpStatusCode.OK, await allowed.Content.ReadAsStringAsync());
        fake.Requests.Select(r => r.TargetModelName).Should().Equal("remote-a", "remote-b");
        fake.Requests.Clear();

        using var denied = await client.SendAsync(Request(endpoint, Source));
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        fake.Requests.Should().BeEmpty("permission to call a parent does not grant independent source access");
    }

    [Theory]
    [InlineData("/v1/chat/completions")]
    [InlineData("/v1/messages")]
    [InlineData("/v1/responses")]
    public async Task Source_acl_does_not_grant_parent_access(string endpoint)
    {
        var fake = Forwarder("remote-a");
        await using var factory = new ProxyFallbackWebApplicationFactory(fake);
        using var client = factory.CreateClient();
        await SeedAsync(factory, allowedRoute: Source);

        using var response = await client.SendAsync(Request(endpoint, Parent));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        fake.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/v1/chat/completions")]
    [InlineData("/v1/messages")]
    [InlineData("/v1/responses")]
    public async Task Unavailable_earlier_reference_does_not_claim_available_equivalent_sibling(string endpoint)
    {
        var fake = Forwarder("remote-a");
        await using var factory = new ProxyFallbackWebApplicationFactory(fake);
        using var client = factory.CreateClient();
        var graph = await SeedAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var unavailable = new ProxyRouteRule
            {
                ExternalModelName = Parent, TargetKind = "RouteEntry", TargetRouteEntryId = graph.Source.Id,
                TargetRouteEntryName = Source, Priority = -1, AvailabilityMode = "Unavailable", TimeRangesJson = AllDayRange
            };
            await db.Client.Insertable(unavailable).ExecuteCommandAsync();
            scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>().InvalidateRuntimeRouteTargets();
        }

        using var response = await client.SendAsync(Request(endpoint, Parent));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Requests.Select(r => r.TargetModelName).Should().Equal("remote-a");
        var trace = factory.Services.GetRequiredService<DeveloperInvocationTraceStore>().List().Single(t => t.RequestModel == Parent);
        trace.Attempts.Should().ContainSingle().Which.FallbackGroupId.Should().Be(graph.Direct.Id);
    }

    [Theory]
    [InlineData("/v1/chat/completions", false)]
    [InlineData("/v1/messages", false)]
    [InlineData("/v1/responses", false)]
    [InlineData("/v1/chat/completions", true)]
    public async Task Parent_and_leaf_availability_are_both_enforced_before_forwarding(string endpoint, bool leafSchedule)
    {
        var fake = Forwarder("remote-e");
        await using var factory = new ProxyFallbackWebApplicationFactory(fake);
        using var client = factory.CreateClient();
        var graph = await SeedAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var rules = leafSchedule ? graph.Leaves : [graph.Reference];
            foreach (var rule in rules)
            {
                rule.AvailabilityMode = "Unavailable";
                rule.TimeRangesJson = AllDayRange;
                await db.Client.Updateable(rule).ExecuteCommandAsync();
            }
            scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>().InvalidateRuntimeRouteTargets();
        }

        using var response = await client.SendAsync(Request(endpoint, Parent));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Requests.Select(r => r.TargetModelName).Should().Equal("remote-a", "remote-e");
    }

    private static FakeProxyForwardService Forwarder(string successfulModel)
    {
        ProxyForwardResult Result(ProxyForwardRequest request) => new()
        {
            Success = request.TargetModelName == successfulModel,
            StatusCode = request.TargetModelName == successfulModel ? 200 : 502,
            ResponseBody = request.TargetModelName == successfulModel
                ? "{\"id\":\"test\",\"choices\":[{\"message\":{\"content\":\"ok\"}}],\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"output\":[]}"
                : "{\"error\":{\"message\":\"fake upstream unavailable\"}}"
        };
        return new FakeProxyForwardService
        {
            ForwardResultFactory = Result,
            // Fail before any stream bytes are emitted, so another route can safely be attempted.
            StreamingLines = [],
            StreamingResultFactory = Result
        };
    }

    private static HttpRequestMessage Request(string endpoint, string model, bool streaming = false)
    {
        var body = endpoint == "/v1/responses"
            ? JsonSerializer.Serialize(new { model, input = "hi", stream = streaming })
            : JsonSerializer.Serialize(new { model, max_tokens = 16, messages = new[] { new { role = "user", content = "hi" } }, stream = streaming });
        return new(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", "test-key") }
        };
    }

    private static async Task<Graph> SeedAsync(WebApplicationFactory<Program> factory, string? allowedRoute = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pool = new AccountPool { Name = "reference-test-pool", VendorKey = "codex" };
        var source = new ProxyRouteEntry { EntryName = Source, ManagedByPoolId = pool.Id };
        var sites = new[] { "a", "b", "c", "e" }.Select(name => new Site
        {
            Name = "reference-" + name, BaseUrl = "https://reference-" + name + ".invalid", ApiKey = "upstream-secret-" + name,
            IsEnabled = true, ProtocolType = "OpenAI", SupportsOpenAi = true, SupportsAnthropic = true, SupportsResponses = true
        }).ToArray();
        ProxyRouteRule Leaf(string entry, int index, int priority) => new()
        {
            ExternalModelName = entry, SiteId = sites[index].Id, UpstreamModelName = "alias-" + new[] { "a", "b", "c", "e" }[index],
            SiteModelName = "remote-" + new[] { "a", "b", "c", "e" }[index], Priority = priority,
            ModelPriority = priority, InstancePriority = priority, TargetKind = "Site"
        };
        var direct = Leaf(Parent, 0, 0);
        var reference = new ProxyRouteRule
        {
            ExternalModelName = Parent, TargetKind = "RouteEntry", TargetRouteEntryId = source.Id,
            TargetRouteEntryName = Source, Priority = 1, ModelPriority = 1
        };
        var last = Leaf(Parent, 3, 2);
        var leaves = new[] { Leaf(Source, 0, 0), Leaf(Source, 1, 1), Leaf(Source, 2, 2) };
        await db.Client.Insertable(sites).ExecuteCommandAsync();
        await db.Client.Insertable(pool).ExecuteCommandAsync();
        await db.Client.Insertable(new[] { source, new ProxyRouteEntry { EntryName = Parent } }).ExecuteCommandAsync();
        await db.Client.Insertable(new[] { direct, reference, last }.Concat(leaves).ToArray()).ExecuteCommandAsync();
        await db.Client.Updateable<AITool.Domain.Operations.SystemRuntimeSettings>()
            .SetColumns(s => s.DeveloperFeaturesEnabled == true)
            .SetColumns(s => s.DeveloperTraceEnabled == true)
            .SetColumns(s => s.DeveloperFailureDumpEnabled == false)
            .Where(s => s.Id == 1).ExecuteCommandAsync();
        if (allowedRoute is not null)
        {
            var allowedJson = JsonSerializer.Serialize(new[] { allowedRoute });
            await db.Client.Updateable<ProxyAccessKey>()
                .SetColumns(k => k.AllowedRouteNames == allowedJson)
                .Where(k => k.KeyName == "integration")
                .ExecuteCommandAsync();
        }
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        cache.InvalidateRuntimeRouteTargets();
        cache.InvalidateRuntimeSettings();
        cache.InvalidateAccessKeys();
        return new(source, pool, direct, reference, last, leaves);
    }

    private sealed class IsolatedStreamingForwarder(FakeProxyForwardService fake) : IProxyForwardService
    {
        public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
            => fake.ForwardAsync(request, cancellationToken);

        public async Task<ProxyForwardResult> ForwardStreamingAsync(ProxyForwardRequest request,
            Func<string, CancellationToken, Task> onSseDataAsync, CancellationToken cancellationToken = default)
        {
            var result = await fake.ForwardAsync(request, cancellationToken);
            result.IsStreaming = true;
            if (!result.Success) return result; // A failed HTTP response must never emit downstream SSE.
            var lines = request.ProtocolType switch
            {
                "Anthropic" => new[]
                {
                    "event: message_start", "data: {\"type\":\"message_start\",\"message\":{\"id\":\"test\",\"role\":\"assistant\",\"content\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}", "",
                    "event: message_stop", "data: {\"type\":\"message_stop\"}", ""
                },
                "Responses" => new[]
                {
                    "event: response.completed", "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"test\",\"status\":\"completed\",\"output\":[],\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}", ""
                },
                _ => new[] { "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}", "", "data: [DONE]", "" }
            };
            foreach (var line in lines) await onSseDataAsync(line, cancellationToken);
            return result;
        }
    }

    private sealed record Graph(ProxyRouteEntry Source, AccountPool Pool, ProxyRouteRule Direct,
        ProxyRouteRule Reference, ProxyRouteRule Last, ProxyRouteRule[] Leaves);
}
