using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.IntegrationTests.Proxy;

public sealed class PoolQueueEditingTests
{
    private static object Item(ProxyRouteRule rule, bool enabled) => new {
        ruleId = rule.Id, expectedPriority = rule.Priority, expectedModelPriority = rule.ModelPriority,
        expectedInstancePriority = rule.InstancePriority, expectedIsEnabled = rule.IsEnabled, isEnabled = enabled
    };

    private static async Task<(Guid EntryId, ProxyRouteRule A, ProxyRouteRule B)> Seed(ProxyFallbackWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var source = (await db.ProxyRouteRules.Where(x => x.ExternalModelName == "chat-prod").ToListAsync()).First();
        var entry = new ProxyRouteEntry { EntryName = "pool-test", ManagedByPoolId = Guid.NewGuid() };
        await db.InsertAsync(entry);
        var a = new ProxyRouteRule { ExternalModelName = entry.EntryName, SiteId = source.SiteId, SiteModelName = source.SiteModelName, UpstreamModelName = source.UpstreamModelName, Priority = 0, ModelPriority = 0, InstancePriority = 0 };
        await db.InsertAsync(a);
        var b = new ProxyRouteRule {
            ExternalModelName = a.ExternalModelName, SiteId = a.SiteId, SiteModelName = a.SiteModelName,
            UpstreamModelName = a.UpstreamModelName, Priority = 1, ModelPriority = 0, InstancePriority = 1,
            AvailabilityMode = "Unavailable", TimeRangesJson = "[{\"start\":\"10:00\",\"end\":\"11:00\"}]"
        };
        await db.InsertAsync(b);
        return (entry.Id, a, b);
    }

    [Fact]
    public async Task Enabling_an_active_pool_candidate_waits_before_publishing()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();
        var (entryId, a, b) = await Seed(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            b.SiteModelName = "new-active";
            b.IsEnabled = false;
            await db.UpdateAsync(b);
        }
        var cache = factory.Services.GetRequiredService<ProxyRequestMetadataCache>();
        var limiter = factory.Services.GetRequiredService<ModelConcurrencyLimiter>();
        (await cache.CaptureEffectiveRouteTargetsAsync("pool-test", CancellationToken.None)).Select(x => x.SiteModelName)
            .Should().Equal(a.SiteModelName);
        using var active = await limiter.AcquireAsync(factory.Services, b.SiteId, b.SiteModelName,
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(1), CancellationToken.None);
        active.Acquired.Should().BeTrue();
        var response = await client.PostAsJsonAsync("/api/admin/route-rules/pool-queue", new {
            entryId, rules = new[] { Item(a, true), Item(b, true) }
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Contain("调用中的模型");
        (await cache.CaptureEffectiveRouteTargetsAsync("pool-test", CancellationToken.None)).Select(x => x.SiteModelName)
            .Should().Equal(a.SiteModelName);
        active.Dispose();
        (await cache.CaptureEffectiveRouteTargetsAsync("pool-test", CancellationToken.None)).Select(x => x.SiteModelName)
            .Should().Equal(a.SiteModelName, b.SiteModelName);
    }

    [Fact]
    public async Task Pool_queue_edits_in_place_and_invalidates_referencing_route()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();
        var (entryId, a, b) = await Seed(factory);
        var reference = await client.PostAsJsonAsync("/api/admin/route-rules/save", new {
            externalModelName = "parent-pool", supportsRouteReferences = true,
            rules = new[] { new { targetKind = "RouteEntry", targetRouteEntryId = entryId } }
        });
        reference.StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await client.PostAsJsonAsync("/api/admin/route-rules/pool-queue", new {
            entryId, rules = new[] { Item(b, false), Item(a, true) }
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var listed = await client.GetFromJsonAsync<JsonElement>("/api/admin/route-rules/list?modelName=pool-test");
        listed[0].GetProperty("ruleId").GetGuid().Should().Be(b.Id);
        listed[0].GetProperty("isEnabled").GetBoolean().Should().BeFalse();
        listed[1].GetProperty("ruleId").GetGuid().Should().Be(a.Id);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var saved = await db.ProxyRouteRules.InSingleAsync(b.Id);
        saved.AvailabilityMode.Should().Be("Unavailable");
        saved.TimeRangesJson.Should().Be(b.TimeRangesJson);
        (await db.ProxyRouteEntries.InSingleAsync(entryId)).ManagedByPoolId.Should().NotBeNull();
        (await db.ProxyRouteRules.FirstAsync(r => r.ExternalModelName == "parent-pool")).TargetRouteEntryId.Should().Be(entryId);
    }

    [Fact]
    public async Task Pool_queue_rejects_stale_or_incomplete_submissions_without_mutation()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();
        var (entryId, a, b) = await Seed(factory);
        var initial = new[] { Item(b, false), Item(a, true) };
        (await client.PostAsJsonAsync("/api/admin/route-rules/pool-queue", new { entryId, rules = initial })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/admin/route-rules/pool-queue", new { entryId, rules = initial })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync("/api/admin/route-rules/pool-queue", new { entryId, rules = new[] { Item(a, true) } })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync("/api/admin/route-rules/pool-queue", new { entryId, rules = new[] { Item(a, true), Item(a, false) } })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync("/api/admin/route-rules/pool-queue", new { entryId, rules = new[] { new { ruleId = a.Id }, new { ruleId = b.Id } } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var saved = await db.ProxyRouteRules.InSingleAsync(b.Id);
        saved.IsEnabled.Should().BeFalse();
        saved.Priority.Should().Be(0);
    }
    [Fact]
    public async Task Concurrent_edit_and_new_candidate_cannot_be_overwritten_by_stale_queue()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();
        var (entryId, a, b) = await Seed(factory);
        var request = new { entryId, rules = new[] { Item(b, false), Item(a, true) } };
        var replies = await Task.WhenAll(
            client.PostAsJsonAsync("/api/admin/route-rules/pool-queue", request),
            client.PostAsJsonAsync("/api/admin/route-rules/pool-queue", request));
        replies.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        replies.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.InsertAsync(new ProxyRouteRule {
                ExternalModelName = "pool-test", SiteId = a.SiteId, SiteModelName = a.SiteModelName,
                UpstreamModelName = a.UpstreamModelName, Priority = 2, InstancePriority = 2
            });
        }
        (await client.PostAsJsonAsync("/api/admin/route-rules/pool-queue", request)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await using var verify = factory.Services.CreateAsyncScope();
        var rows = await verify.ServiceProvider.GetRequiredService<AppDbContext>().ProxyRouteRules
            .Where(r => r.ExternalModelName == "pool-test").ToListAsync();
        rows.Should().HaveCount(3);
        rows.Single(r => r.Id == b.Id).IsEnabled.Should().BeFalse();
    }
}