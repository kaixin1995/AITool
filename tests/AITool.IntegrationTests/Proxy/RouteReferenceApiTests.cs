using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AITool.Domain.Proxy;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.IntegrationTests.Proxy;

public sealed class RouteReferenceApiTests
{
    private static readonly Guid SiteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static object Direct() => new { siteId = SiteId, siteModelName = "direct", upstreamModelName = "direct" };
    private static object Reference(Guid id, bool enabled = true) => new { targetKind = "RouteEntry", targetRouteEntryId = id, isEnabled = enabled };
    private static Task<HttpResponseMessage> Save(HttpClient client, string name, object[] rules, bool supported = true) =>
        client.PostAsJsonAsync("/api/admin/route-rules/save", new { externalModelName = name, rules, supportsRouteReferences = supported });
    private static async Task<Guid> Entry(ProxyFallbackWebApplicationFactory factory, string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProxyRouteEntries.FirstAsync(x => x.EntryName == name)).Id;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Adding_an_active_target_preserves_the_old_entry_and_parent_until_release(bool existing, bool reference)
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();
        var key = new SiteKey { SiteId = SiteId, KeyValue = "active-key" };
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().InsertAsync(key);

        const string entry = "edited";
        if (existing)
            (await Save(client, entry, [Direct()])).StatusCode.Should().Be(HttpStatusCode.OK);
        else
            (await client.PostAsJsonAsync("/api/admin/route-rules/entries", new { entryName = entry })).StatusCode.Should().Be(HttpStatusCode.OK);
        var entryId = await Entry(factory, entry);
        if (!reference)
            (await Save(client, "dependent", [Reference(entryId)])).StatusCode.Should().Be(HttpStatusCode.OK);

        var cache = factory.Services.GetRequiredService<ProxyRequestMetadataCache>();
        var limiter = factory.Services.GetRequiredService<ModelConcurrencyLimiter>();
        var before = await cache.CaptureEffectiveRouteTargetsAsync(entry, CancellationToken.None);
        using var active = await limiter.AcquireAsync(factory.Services, key.Id, "gpt-5.5-a",
            ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(1), CancellationToken.None);
        active.Acquired.Should().BeTrue();
        object added = reference ? Reference(await Entry(factory, "chat-prod"))
            : new { siteId = SiteId, siteModelName = "gpt-5.5-a", upstreamModelName = "gpt-5.5" };
        var response = await Save(client, entry, existing ? [Direct(), added] : [added]);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Contain("当前请求结束后生效");
        (await cache.CaptureEffectiveRouteTargetsAsync(entry, CancellationToken.None)).Select(x => x.SiteModelName)
            .Should().Equal(before.Select(x => x.SiteModelName));
        if (!reference)
            (await cache.CaptureEffectiveRouteTargetsAsync("dependent", CancellationToken.None)).Select(x => x.SiteModelName)
                .Should().Equal(before.Select(x => x.SiteModelName));
        active.Dispose();
        (await cache.CaptureEffectiveRouteTargetsAsync(entry, CancellationToken.None)).Should().Contain(x => x.SiteModelName == "gpt-5.5-a");
        if (!reference)
            (await cache.CaptureEffectiveRouteTargetsAsync("dependent", CancellationToken.None)).Should().Contain(x => x.SiteModelName == "gpt-5.5-a");
    }

    [Fact]
    public async Task Mixed_save_lists_typed_targets_and_does_not_lose_direct_fields()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();
        var target = await Entry(factory, "chat-prod");
        var saved = await Save(client, "mixed", [Reference(target), Direct()]);
        saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        var rules = await client.GetFromJsonAsync<JsonElement>("/api/admin/route-rules/list?modelName=mixed");
        rules.GetArrayLength().Should().Be(2);
        var reference = rules.EnumerateArray().Single(x => x.GetProperty("targetKind").GetString() == "RouteEntry");
        reference.GetProperty("targetRouteEntryId").GetGuid().Should().Be(target);
        reference.GetProperty("targetRouteEntryName").GetString().Should().Be("chat-prod");
        reference.GetProperty("referenceStatus").GetString().Should().Be("Valid");
        reference.GetProperty("siteId").GetGuid().Should().Be(Guid.Empty);
        var entries = await client.GetFromJsonAsync<JsonElement>("/api/admin/route-rules/entries");
        var mixed = entries.EnumerateArray().Single(x => x.GetProperty("entryName").GetString() == "mixed");
        mixed.GetProperty("entryId").GetGuid().Should().NotBe(Guid.Empty);
        mixed.GetProperty("hasRouteReferences").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Legacy_direct_payload_works_but_cannot_replace_reference_bearing_entry()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();
        (await Save(client, "legacy", [Direct()], false)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Save(client, "parent", [Reference(await Entry(factory, "chat-prod"))])).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Save(client, "parent", [Direct()], false)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await using var scope = factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<AppDbContext>().ProxyRouteRules.FirstAsync(x => x.ExternalModelName == "parent"))
            .TargetKind.Should().Be("RouteEntry");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Self_and_nested_references_are_rejected_including_disabled_edges(bool enabled)
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();
        var target = await Entry(factory, "chat-prod");
        (await Save(client, "chat-prod", [Reference(target, enabled)])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Save(client, "parent", [Reference(target, enabled)])).StatusCode.Should().Be(HttpStatusCode.OK);
        var parent = await Entry(factory, "parent");
        (await Save(client, "grandparent", [Reference(parent, enabled)])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Save(client, "chat-prod", [Reference(parent, enabled)])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // Reverse validation: even an empty direct-only child cannot acquire references while referenced.
        (await client.PostAsJsonAsync("/api/admin/route-rules/entries", new { entryName = "leaf" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Save(client, "chat-prod", [Reference(await Entry(factory, "leaf"), enabled)])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_requires_confirmation_and_recreated_name_never_rebinds_old_identity()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();
        var oldId = await Entry(factory, "chat-prod");
        (await Save(client, "parent", [Reference(oldId)])).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/admin/route-rules/entries/delete", new { entryName = "chat-prod" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync("/api/admin/route-rules/entries/delete", new { entryName = "chat-prod", confirmReferenced = true })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/admin/route-rules/entries", new { entryName = "chat-prod" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Entry(factory, "chat-prod")).Should().NotBe(oldId);
        var rules = await client.GetFromJsonAsync<JsonElement>("/api/admin/route-rules/list?modelName=parent");
        rules[0].GetProperty("targetRouteEntryId").GetGuid().Should().Be(oldId);
        rules[0].GetProperty("referenceStatus").GetString().Should().Be("Missing");
        // Saving an unchanged broken edge is permitted, without accidentally resolving its name.
        (await Save(client, "parent", [Reference(oldId)])).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("toggle")]
    [InlineData("delete")]
    [InlineData("entry")]
    public async Task Pool_managed_entries_reject_all_manual_mutations(string operation)
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();
        Guid ruleId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var entry = await db.ProxyRouteEntries.FirstAsync(x => x.EntryName == "chat-prod");
            entry.ManagedByPoolId = Guid.NewGuid();
            await db.UpdateAsync(entry);
            ruleId = (await db.ProxyRouteRules.FirstAsync(x => x.ExternalModelName == "chat-prod")).Id;
        }
        var response = operation switch
        {
            "save" => await Save(client, "chat-prod", [Direct()]),
            "entry" => await client.PostAsJsonAsync("/api/admin/route-rules/entries/delete", new { entryName = "chat-prod", confirmReferenced = true }),
            _ => await client.PostAsync($"/api/admin/route-rules/{operation}/{ruleId}", null)
        };
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Discovery_explains_self_nested_and_reverse_ineligibility_but_allows_pool_targets()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();
        var target = await Entry(factory, "chat-prod");
        (await Save(client, "parent", [Reference(target, false)])).StatusCode.Should().Be(HttpStatusCode.OK);
        var targets = await client.GetFromJsonAsync<JsonElement>("/api/admin/route-rules/reference-targets?parentEntryName=parent");
        var self = targets.EnumerateArray().Single(x => x.GetProperty("entryName").GetString() == "parent");
        self.GetProperty("canSelect").GetBoolean().Should().BeFalse();
        self.GetProperty("reason").GetString().Should().NotBeNullOrWhiteSpace();
        var reverse = await client.GetFromJsonAsync<JsonElement>("/api/admin/route-rules/reference-targets?parentEntryName=chat-prod");
        reverse.EnumerateArray().Should().OnlyContain(x => !x.GetProperty("canSelect").GetBoolean());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = await db.ProxyRouteEntries.InSingleAsync(target);
        entry.ManagedByPoolId = Guid.NewGuid();
        await db.UpdateAsync(entry);
        targets = await client.GetFromJsonAsync<JsonElement>("/api/admin/route-rules/reference-targets?parentEntryName=fresh");
        var pool = targets.EnumerateArray().Single(x => x.GetProperty("entryId").GetGuid() == target);
        pool.GetProperty("canSelect").GetBoolean().Should().BeTrue();
        pool.GetProperty("managedByPoolId").GetGuid().Should().Be(entry.ManagedByPoolId!.Value);
    }

    [Fact]
    public async Task Invalid_mixed_payload_is_atomic_and_missing_unknown_target_is_rejected()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();
        var target = await Entry(factory, "chat-prod");
        (await Save(client, "chat-prod", [Direct(), new { targetKind = "RouteEntry", targetRouteEntryId = target, siteId = SiteId }]))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Save(client, "new", [Reference(Guid.NewGuid())])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Save(client, "new", [new { targetKind = "Unknown", siteId = SiteId }])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.ProxyRouteRules.CountAsync(x => x.ExternalModelName == "chat-prod")).Should().Be(2);
        (await db.ProxyRouteEntries.AnyAsync(x => x.EntryName == "new")).Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_reverse_saves_cannot_create_nested_graph()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/admin/route-rules/entries", new { entryName = "other" })).EnsureSuccessStatusCode();
        var first = await Entry(factory, "chat-prod");
        var second = await Entry(factory, "other");
        var results = await Task.WhenAll(Save(client, "chat-prod", [Reference(second)]), Save(client, "other", [Reference(first)]));
        results.Count(x => x.StatusCode == HttpStatusCode.OK).Should().Be(1);
        results.Count(x => x.StatusCode == HttpStatusCode.BadRequest).Should().Be(1);
    }

    [Fact]
    public async Task Independent_sqlite_writer_returns_conflict_without_partial_replacement()
    {
        await using var factory = new ProxyFallbackWebApplicationFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var writer = new Microsoft.Data.Sqlite.SqliteConnection(db.Client.CurrentConnectionConfig.ConnectionString);
        await writer.OpenAsync();
        await using (var transaction = writer.BeginTransaction())
        {
            using var command = writer.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE ProxyRouteRules SET Priority = Priority WHERE ExternalModelName = 'chat-prod';";
            await command.ExecuteNonQueryAsync();
            var response = await Save(client, "chat-prod", [Direct()]);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
            await transaction.RollbackAsync();
        }
        (await db.ProxyRouteRules.CountAsync(x => x.ExternalModelName == "chat-prod")).Should().Be(2);
    }

    [Fact]
    public async Task Graph_gate_cancellation_and_disposal_do_not_leak_or_overrelease()
    {
        var lease = await RouteGraphMutationGate.AcquireAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var wait = async () => { using var next = await RouteGraphMutationGate.AcquireAsync(cancellation.Token); };
        await wait.Should().ThrowAsync<OperationCanceledException>();
        lease.Dispose();
        lease.Dispose();
        using var finalLease = await RouteGraphMutationGate.AcquireAsync();
    }
}
