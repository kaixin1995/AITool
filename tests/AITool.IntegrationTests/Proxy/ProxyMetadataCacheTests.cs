using System.Security.Cryptography;
using System.Text;
using AITool.Domain.Models;
using AITool.Domain.Operations;
using AITool.Domain.Proxy;
using AITool.Domain.SiteCatalog;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Protocol;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.IntegrationTests.Proxy;

/// <summary>
/// 代理元数据缓存测试，验证失效后能及时读取到最新配置。
/// </summary>
public sealed class ProxyMetadataCacheTests : IAsyncDisposable
{
    /// <summary>
    /// 保存测试使用的服务提供器。
    /// </summary>
    private readonly ServiceProvider _serviceProvider;
    /// <summary>
    /// 保存当前测试使用的临时数据库文件路径。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-proxy-metadata-cache-{Guid.NewGuid():N}.db");

    /// <summary>
    /// 初始化代理元数据缓存测试所需的服务容器。
    /// </summary>
    public ProxyMetadataCacheTests()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddSqlSugar($"Data Source={_databasePath}");
        services.AddSingleton<ProxyRequestMetadataCache>();
        services.AddSingleton<ModelConcurrencyLimiter>();
        _serviceProvider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task Route_entry_reference_expands_physical_keys_without_interleaving_outer_groups()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        SqlSugarSetup.InitializeDatabase(db.Client);
        var source = new ProxyRouteEntry { EntryName = "source", ManagedByPoolId = Guid.NewGuid() };
        var site = new Site
        {
            Name = "physical", BaseUrl = "https://never-called.invalid", ApiKey = "legacy",
            ProtocolType = "Anthropic", SupportsOpenAi = false, SupportsAnthropic = true,
            ExtraHeadersJson = "{\"X-Leaf\":\"preserved\"}", ClientEmulation = "None"
        };
        var firstKey = new SiteKey { SiteId = site.Id, KeyValue = "first", Priority = 0 };
        var secondKey = new SiteKey { SiteId = site.Id, KeyValue = "second", Priority = 1 };
        var model = new ModelLibraryItem { ModelName = "upstream", DisplayName = "upstream", OverrideReasoningEffort = "high" };
        var firstLeaf = new ProxyRouteRule { ExternalModelName = source.EntryName, SiteId = site.Id, UpstreamModelName = model.ModelName, SiteModelName = "leaf-a", ModelPriority = 100 };
        var secondLeaf = new ProxyRouteRule { ExternalModelName = source.EntryName, SiteId = site.Id, UpstreamModelName = model.ModelName, SiteModelName = "leaf-b", ModelPriority = 200 };
        var reference = new ProxyRouteRule
        {
            ExternalModelName = "parent", TargetKind = "RouteEntry", TargetRouteEntryId = source.Id,
            Priority = 0, ModelPriority = 999, AvailabilityMode = "AvailableOnly", TimeRangesJson = "[{\"start\":\"09:00\",\"end\":\"17:00\"}]"
        };
        var direct = new ProxyRouteRule { ExternalModelName = "parent", SiteId = site.Id, UpstreamModelName = model.ModelName, SiteModelName = "direct", Priority = 1, ModelPriority = -100 };
        db.ProxyRouteEntries.Add(source);
        db.Sites.Add(site);
        db.SiteKeys.AddRange(firstKey, secondKey);
        db.ModelLibraryItems.Add(model);
        db.ProxyRouteRules.AddRange(firstLeaf, secondLeaf, reference, direct);
        await db.SaveChangesAsync();

        var sourceTargets = await cache.CaptureEffectiveRouteTargetsAsync("source", CancellationToken.None);
        sourceTargets.Should().HaveCount(4).And.OnlyContain(x => x.FallbackGroupId == null && x.SourceRouteEntryId == null);
        var parentTargets = await cache.CaptureEffectiveRouteTargetsAsync("parent", CancellationToken.None);
        parentTargets.Select(x => x.SiteModelName).Should().Equal("leaf-a", "leaf-a", "leaf-b", "leaf-b", "direct", "direct");
        parentTargets.Take(4).Should().OnlyContain(x => x.FallbackGroupId == reference.Id && x.SourceRouteEntryId == source.Id && x.SourcePoolId == source.ManagedByPoolId && x.ExternalModelName == "parent");
        parentTargets.Skip(4).Should().OnlyContain(x => x.FallbackGroupId == direct.Id && x.SourceRouteEntryId == null);
        parentTargets[0].RouteId.Should().Be(firstLeaf.Id);
        parentTargets[0].CircuitKey.Should().Be(sourceTargets[0].CircuitKey);
        parentTargets[0].SiteKeyId.Should().Be(firstKey.Id);
        parentTargets[0].ApiKey.Should().Be("first");
        parentTargets[0].ProtocolType.Should().Be("Anthropic");
        parentTargets[0].ExtraHeaders["X-Leaf"].Should().Be("preserved");
        parentTargets[0].OverrideReasoningEffort.Should().Be("high");
        parentTargets[0].IsAvailableAt(new TimeOnly(12, 0)).Should().BeTrue();
        parentTargets[0].IsAvailableAt(new TimeOnly(20, 0)).Should().BeFalse();
        parentTargets[0].ExtraHeaders["X-Leaf"] = "mutated";
        sourceTargets[0].ExtraHeaders["X-Leaf"].Should().Be("preserved");
        (await cache.CaptureEffectiveRouteTargetsAsync("parent", CancellationToken.None))[0].ExtraHeaders["X-Leaf"].Should().Be("preserved");

        firstLeaf.SiteModelName = "updated-live-leaf";
        await db.UpdateAsync(firstLeaf);
        cache.InvalidateRouteTargets();
        (await cache.CaptureEffectiveRouteTargetsAsync("parent", CancellationToken.None))[0].SiteModelName.Should().Be("updated-live-leaf");
        parentTargets[0].SiteModelName.Should().Be("leaf-a", "an in-flight request keeps its detached snapshot");
        var entry = (await cache.GetRouteEntriesAsync(CancellationToken.None)).Single(x => x.EntryName == "source");
        entry.EntryId.Should().Be(source.Id);
        entry.ManagedByPoolId.Should().Be(source.ManagedByPoolId);
        (await cache.GetRouteEntriesAsync(CancellationToken.None)).Single(x => x.EntryName == "parent").HasRouteReferences.Should().BeTrue();
    }

    [Fact]
    public async Task Invalid_disabled_missing_and_nested_references_produce_no_leaves_but_remain_diagnostic()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        SqlSugarSetup.InitializeDatabase(db.Client);
        var source = new ProxyRouteEntry { EntryName = "source" };
        var nested = new ProxyRouteEntry { EntryName = "nested" };
        var site = new Site { Name = "physical", BaseUrl = "https://never-called.invalid", ApiKey = "key", SupportsOpenAi = true };
        db.Sites.Add(site);
        db.ProxyRouteEntries.AddRange(source, nested);
        db.ProxyRouteRules.AddRange(
            new ProxyRouteRule { ExternalModelName = "source", SiteId = site.Id, SiteModelName = "leaf" },
            new ProxyRouteRule { ExternalModelName = "nested", SiteId = site.Id, SiteModelName = "must-not-leak" },
            new ProxyRouteRule { ExternalModelName = "nested", TargetKind = "RouteEntry", TargetRouteEntryId = source.Id, IsEnabled = false },
            new ProxyRouteRule { ExternalModelName = "disabled", TargetKind = "RouteEntry", TargetRouteEntryId = source.Id, IsEnabled = false },
            new ProxyRouteRule { ExternalModelName = "missing", TargetKind = "RouteEntry", TargetRouteEntryId = Guid.NewGuid(), TargetRouteEntryName = "deleted diagnostic" },
            new ProxyRouteRule { ExternalModelName = "self", TargetKind = "RouteEntry", TargetRouteEntryId = nested.Id },
            new ProxyRouteRule { ExternalModelName = "invalid", TargetKind = "Other", SiteId = site.Id, SiteModelName = "must-not-leak" });
        await db.SaveChangesAsync();
        foreach (var name in new[] { "disabled", "missing", "self", "invalid" })
        {
            (await cache.CaptureEffectiveRouteTargetsAsync(name, CancellationToken.None)).Should().BeEmpty();
            (await cache.GetRouteRulesAsync(name, CancellationToken.None)).Should().ContainSingle();
        }
        var missing = (await cache.GetRouteRulesAsync("missing", CancellationToken.None)).Single();
        missing.ReferenceStatus.Should().Be("Missing");
        missing.TargetRouteEntryName.Should().Be("deleted diagnostic");
        (await cache.GetRouteRulesAsync("self", CancellationToken.None)).Single().ReferenceStatus.Should().Be("Nested");
    }

    [Fact]
    public async Task Source_deferred_snapshot_protects_dependent_parent_and_only_exact_key_releases_it()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        var limiter = scope.ServiceProvider.GetRequiredService<ModelConcurrencyLimiter>();
        SqlSugarSetup.InitializeDatabase(db.Client);
        var source = new ProxyRouteEntry { EntryName = "source" };
        var site = new Site { Name = "physical", BaseUrl = "https://never-called.invalid", ApiKey = "legacy", SupportsOpenAi = true };
        var key = new SiteKey { SiteId = site.Id, KeyValue = "physical-key" };
        var leaf = new ProxyRouteRule { ExternalModelName = "source", SiteId = site.Id, SiteModelName = "leaf" };
        db.ProxyRouteEntries.Add(source);
        db.Sites.Add(site);
        db.SiteKeys.Add(key);
        db.ProxyRouteRules.AddRange(leaf, new ProxyRouteRule { ExternalModelName = "parent", TargetKind = "RouteEntry", TargetRouteEntryId = source.Id });
        await db.SaveChangesAsync();
        var beforeSource = await cache.CaptureEffectiveRouteTargetsAsync("source", CancellationToken.None);
        var beforeParent = await cache.CaptureEffectiveRouteTargetsAsync("parent", CancellationToken.None);
        using var active = await limiter.AcquireAsync(_serviceProvider, key.Id, "leaf", ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(1), CancellationToken.None, site.Id);
        var slot = limiter.ListActiveSlotIds(key.Id, "leaf").Single();
        leaf.SiteModelName = "new-leaf";
        await db.UpdateAsync(leaf);
        limiter.TryDeferRuntimeRouteTargetsRefresh("source", new[] { new RouteTargetIdentity(key.Id, "leaf") }, beforeSource).Should().BeTrue();
        cache.CompleteDeferredRuntimeRouteTarget(Guid.NewGuid(), "leaf", slot);
        (await cache.CaptureEffectiveRouteTargetsAsync("source", CancellationToken.None)).Single().SiteModelName.Should().Be("leaf");
        (await cache.CaptureEffectiveRouteTargetsAsync("parent", CancellationToken.None)).Single().SiteModelName.Should().Be("leaf");
        active.Dispose();
        (await cache.CaptureEffectiveRouteTargetsAsync("source", CancellationToken.None)).Single().SiteModelName.Should().Be("new-leaf");
        (await cache.CaptureEffectiveRouteTargetsAsync("parent", CancellationToken.None)).Single().SiteModelName.Should().Be("new-leaf");
        beforeParent.Single().SiteModelName.Should().Be("leaf");
    }

    [Fact]
    public async Task Source_deletion_clears_deferred_overrides_but_does_not_mutate_active_request_snapshot()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        SqlSugarSetup.InitializeDatabase(db.Client);
        var source = new ProxyRouteEntry { EntryName = "source" };
        var site = new Site { Name = "physical", BaseUrl = "https://never-called.invalid", ApiKey = "secret", SupportsOpenAi = true };
        var leaf = new ProxyRouteRule { ExternalModelName = "source", SiteId = site.Id, SiteModelName = "leaf" };
        db.ProxyRouteEntries.Add(source);
        db.Sites.Add(site);
        db.ProxyRouteRules.AddRange(leaf, new ProxyRouteRule { ExternalModelName = "parent", TargetKind = "RouteEntry", TargetRouteEntryId = source.Id });
        await db.SaveChangesAsync();
        var beforeSource = await cache.CaptureEffectiveRouteTargetsAsync("source", CancellationToken.None);
        var activeParent = await cache.CaptureEffectiveRouteTargetsAsync("parent", CancellationToken.None);
        cache.DeferRuntimeRouteTargetsRefresh("source", new[] { new ActiveRouteTargetSnapshot(new RouteTargetIdentity(site.Id, "leaf"), new long[] { 1 }) }, beforeSource);
        await db.DeleteAsync(source);
        await db.DeleteAsync(leaf);
        cache.InvalidateDeletedRouteEntry("source");
        (await cache.CaptureEffectiveRouteTargetsAsync("source", CancellationToken.None)).Should().BeEmpty();
        (await cache.CaptureEffectiveRouteTargetsAsync("parent", CancellationToken.None)).Should().BeEmpty();
        activeParent.Single().ApiKey.Should().Be("secret");
    }

    [Fact]
    public async Task New_parent_skips_effective_grouped_source_until_deferred_reference_snapshot_expires()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        SqlSugarSetup.InitializeDatabase(db.Client);
        var terminal = new ProxyRouteEntry { EntryName = "T" };
        var source = new ProxyRouteEntry { EntryName = "S" };
        var site = new Site { Name = "physical", BaseUrl = "https://never-called.invalid", ApiKey = "key", SupportsOpenAi = true };
        var oldReference = new ProxyRouteRule
        {
            ExternalModelName = "S", TargetKind = "RouteEntry", TargetRouteEntryId = terminal.Id,
            AvailabilityMode = "AvailableOnly", TimeRangesJson = "[{\"start\":\"09:00\",\"end\":\"17:00\"}]"
        };
        db.ProxyRouteEntries.AddRange(terminal, source);
        db.Sites.Add(site);
        db.ProxyRouteRules.AddRange(oldReference,
            new ProxyRouteRule { ExternalModelName = "T", SiteId = site.Id, SiteModelName = "terminal-leaf" });
        await db.SaveChangesAsync();
        var activeSource = await cache.CaptureEffectiveRouteTargetsAsync("S", CancellationToken.None);
        activeSource.Single().SourceRouteEntryId.Should().Be(terminal.Id);
        activeSource.Single().ParentAvailabilityMode.Should().Be("AvailableOnly");

        // Persisted S is now a valid direct-only reference target, but its effective
        // snapshot is still the older expanded S -> T while the captured slot lives.
        await db.DeleteAsync(oldReference);
        db.ProxyRouteRules.Add(new ProxyRouteRule { ExternalModelName = "S", SiteId = site.Id, SiteModelName = "new-direct-leaf" });
        await db.SaveChangesAsync();
        cache.DeferRuntimeRouteTargetsRefresh("S",
            new[] { new ActiveRouteTargetSnapshot(new RouteTargetIdentity(site.Id, "terminal-leaf"), new long[] { 123 }) }, activeSource);
        db.ProxyRouteRules.AddRange(
            new ProxyRouteRule { ExternalModelName = "P", TargetKind = "RouteEntry", TargetRouteEntryId = source.Id, Priority = 0 },
            new ProxyRouteRule { ExternalModelName = "P", SiteId = site.Id, SiteModelName = "parent-direct-sibling", Priority = 1 });
        await db.SaveChangesAsync();
        cache.InvalidateRouteTargets();

        (await cache.GetRouteRulesAsync("P", CancellationToken.None)).First().ReferenceStatus.Should().Be("Valid", "the persisted graph is single-layer");
        var protectedSource = (await cache.CaptureEffectiveRouteTargetsAsync("S", CancellationToken.None)).Single();
        protectedSource.SiteModelName.Should().Be("terminal-leaf");
        protectedSource.SourceRouteEntryId.Should().Be(terminal.Id);
        protectedSource.IsAvailableAt(new TimeOnly(20, 0)).Should().BeFalse();
        var incomingParent = await cache.CaptureEffectiveRouteTargetsAsync("P", CancellationToken.None);
        incomingParent.Select(x => x.SiteModelName).Should().Equal(new[] { "parent-direct-sibling" }, "only the unsafe reference group is skipped");
        incomingParent.Should().NotContain(x => x.SourceRouteEntryId == source.Id || x.SiteModelName == "terminal-leaf");

        cache.CompleteDeferredRuntimeRouteTarget(site.Id, "terminal-leaf", 123);
        var freshParent = await cache.CaptureEffectiveRouteTargetsAsync("P", CancellationToken.None);
        freshParent.Select(x => x.SiteModelName).Should().Equal("new-direct-leaf", "parent-direct-sibling");
        freshParent.First().SourceRouteEntryId.Should().Be(source.Id);
        freshParent.First().ParentAvailabilityMode.Should().Be("AllDay");
        activeSource.Single().SourceRouteEntryId.Should().Be(terminal.Id);
        activeSource.Single().ParentAvailabilityMode.Should().Be("AvailableOnly");
    }

    [Fact]
    public async Task First_parent_resolution_uses_effective_deferred_source_instead_of_new_raw_rows()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        SqlSugarSetup.InitializeDatabase(db.Client);
        var source = new ProxyRouteEntry { EntryName = "source" };
        var site = new Site { Name = "physical", BaseUrl = "https://never-called.invalid", ApiKey = "key", SupportsOpenAi = true };
        var leaf = new ProxyRouteRule { ExternalModelName = "source", SiteId = site.Id, SiteModelName = "old" };
        db.ProxyRouteEntries.Add(source);
        db.Sites.Add(site);
        db.ProxyRouteRules.Add(leaf);
        await db.SaveChangesAsync();
        var before = await cache.CaptureEffectiveRouteTargetsAsync("source", CancellationToken.None);
        leaf.SiteModelName = "new";
        await db.UpdateAsync(leaf);
        cache.DeferRuntimeRouteTargetsRefresh("source", new[] { new ActiveRouteTargetSnapshot(new RouteTargetIdentity(site.Id, "old"), new long[] { 1 }) }, before);
        db.ProxyRouteRules.Add(new ProxyRouteRule { ExternalModelName = "new-parent", TargetKind = "RouteEntry", TargetRouteEntryId = source.Id });
        await db.SaveChangesAsync();
        cache.InvalidateRuntimeRouteTargets();
        (await cache.CaptureEffectiveRouteTargetsAsync("new-parent", CancellationToken.None)).Single().SiteModelName.Should().Be("old");
        cache.CompleteDeferredRuntimeRouteTarget(site.Id, "old", 1);
        (await cache.CaptureEffectiveRouteTargetsAsync("new-parent", CancellationToken.None)).Single().SiteModelName.Should().Be("new");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mutation_capture_preserves_structural_parent_of_empty_source_until_active_direct_sibling_releases(bool disabledLeaf)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        var limiter = scope.ServiceProvider.GetRequiredService<ModelConcurrencyLimiter>();
        SqlSugarSetup.InitializeDatabase(db.Client);
        var source = new ProxyRouteEntry { EntryName = "empty-source" };
        var site = new Site { Name = "physical", BaseUrl = "https://never-called.invalid", ApiKey = "key", SupportsOpenAi = true };
        db.ProxyRouteEntries.Add(source);
        db.Sites.Add(site);
        var reference = new ProxyRouteRule { ExternalModelName = "parent", TargetKind = "RouteEntry", TargetRouteEntryId = source.Id, Priority = 0 };
        db.ProxyRouteRules.AddRange(reference,
            new ProxyRouteRule { ExternalModelName = "parent", SiteId = site.Id, SiteModelName = "direct-sibling", Priority = 1 });
        if (disabledLeaf)
        {
            db.ProxyRouteRules.Add(new ProxyRouteRule { ExternalModelName = source.EntryName, SiteId = site.Id, SiteModelName = "disabled", IsEnabled = false });
        }
        await db.SaveChangesAsync();
        var activeParent = await cache.CaptureEffectiveRouteTargetsAsync("parent", CancellationToken.None);
        activeParent.Should().ContainSingle().Which.SiteModelName.Should().Be("direct-sibling");
        activeParent.Should().NotContain(x => x.SourceRouteEntryName == source.EntryName);
        using var active = await limiter.AcquireAsync(_serviceProvider, site.Id, "direct-sibling", ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(1), CancellationToken.None);
        var capture = await cache.CaptureMutationSnapshotAsync(source.EntryName, CancellationToken.None);
        capture.PreviousRoutes.Should().BeEmpty();

        db.ProxyRouteRules.Add(new ProxyRouteRule { ExternalModelName = source.EntryName, SiteId = site.Id, SiteModelName = "first-enabled-leaf" });
        await db.SaveChangesAsync();
        cache.InvalidateRuntimeRouteTargets(); // structural dependency must survive eviction too
        limiter.TryDeferRuntimeRouteTargetsRefresh(capture).Should().BeTrue();
        (await cache.CaptureEffectiveRouteTargetsAsync(source.EntryName, CancellationToken.None)).Should().BeEmpty();
        (await cache.CaptureEffectiveRouteTargetsAsync("parent", CancellationToken.None)).Select(x => x.SiteModelName).Should().Equal("direct-sibling");
        active.Dispose();
        var refreshed = await cache.CaptureEffectiveRouteTargetsAsync("parent", CancellationToken.None);
        refreshed.Select(x => x.SiteModelName).Should().Equal("first-enabled-leaf", "direct-sibling");
        refreshed.First().SourceRouteEntryId.Should().Be(source.Id);
        refreshed.First().FallbackGroupId.Should().Be(reference.Id);
        activeParent.Should().ContainSingle().Which.SiteModelName.Should().Be("direct-sibling");
    }

    [Fact]
    public async Task Mutation_capture_preserves_dependent_parent_when_background_invalidation_evicts_cache_before_deferral()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        var limiter = scope.ServiceProvider.GetRequiredService<ModelConcurrencyLimiter>();
        SqlSugarSetup.InitializeDatabase(db.Client);
        var source = new ProxyRouteEntry { EntryName = "source" };
        var site = new Site { Name = "physical", BaseUrl = "https://never-called.invalid", ApiKey = "key", SupportsOpenAi = true };
        var leaf = new ProxyRouteRule { ExternalModelName = "source", SiteId = site.Id, SiteModelName = "old" };
        db.ProxyRouteEntries.Add(source);
        db.Sites.Add(site);
        db.ProxyRouteRules.AddRange(leaf,
            new ProxyRouteRule { ExternalModelName = "parent", TargetKind = "RouteEntry", TargetRouteEntryId = source.Id },
            new ProxyRouteRule { ExternalModelName = "parent", SiteId = site.Id, SiteModelName = "direct-sibling", Priority = 1 });
        await db.SaveChangesAsync();
        var activeParent = await cache.CaptureEffectiveRouteTargetsAsync("parent", CancellationToken.None);
        using var active = await limiter.AcquireAsync(_serviceProvider, site.Id, "direct-sibling", ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(1), CancellationToken.None);
        var mutationSnapshot = await cache.CaptureMutationSnapshotAsync("source", CancellationToken.None);
        mutationSnapshot.PreviousRoutes.Single().SiteModelName.Should().Be("old");
        leaf.SiteModelName = "latest";
        await db.UpdateAsync(leaf);
        cache.InvalidateRuntimeRouteTargets(); // background/credential invalidation after capture
        limiter.ListActiveSlotIds(site.Id, "old").Should().BeEmpty();
        limiter.TryDeferRuntimeRouteTargetsRefresh(mutationSnapshot).Should().BeTrue("the direct sibling dependency survives cache eviction");
        (await cache.CaptureEffectiveRouteTargetsAsync("parent", CancellationToken.None)).First().SiteModelName.Should().Be("old");
        (await cache.CaptureEffectiveRouteTargetsAsync("source", CancellationToken.None)).Single().SiteModelName.Should().Be("old");
        active.Dispose();
        (await cache.CaptureEffectiveRouteTargetsAsync("parent", CancellationToken.None)).First().SiteModelName.Should().Be("latest");
        activeParent.First().SiteModelName.Should().Be("old");
    }

    [Fact]
    public async Task Source_edit_waits_for_active_direct_sibling_in_dependent_parent_and_merges_repeated_saves()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        var limiter = scope.ServiceProvider.GetRequiredService<ModelConcurrencyLimiter>();
        SqlSugarSetup.InitializeDatabase(db.Client);
        var source = new ProxyRouteEntry { EntryName = "source" };
        var site = new Site { Name = "physical", BaseUrl = "https://never-called.invalid", ApiKey = "key", SupportsOpenAi = true };
        var leaf = new ProxyRouteRule { ExternalModelName = "source", SiteId = site.Id, SiteModelName = "old" };
        db.ProxyRouteEntries.Add(source);
        db.Sites.Add(site);
        db.ProxyRouteRules.AddRange(leaf,
            new ProxyRouteRule { ExternalModelName = "parent", TargetKind = "RouteEntry", TargetRouteEntryId = source.Id },
            new ProxyRouteRule { ExternalModelName = "parent", SiteId = site.Id, SiteModelName = "direct-sibling", Priority = 1 });
        await db.SaveChangesAsync();
        var before = await cache.CaptureEffectiveRouteTargetsAsync("source", CancellationToken.None);
        await cache.CaptureEffectiveRouteTargetsAsync("parent", CancellationToken.None);
        using var first = await limiter.AcquireAsync(_serviceProvider, site.Id, "direct-sibling", ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(1), CancellationToken.None);
        leaf.SiteModelName = "intermediate";
        await db.UpdateAsync(leaf);
        var identities = new[] { new RouteTargetIdentity(site.Id, "old") };
        limiter.TryDeferRuntimeRouteTargetsRefresh("source", identities, before).Should().BeTrue();
        var duringFirst = await cache.CaptureEffectiveRouteTargetsAsync("parent", CancellationToken.None);
        duringFirst.First().SiteModelName.Should().Be("old");
        using var second = await limiter.AcquireAsync(_serviceProvider, site.Id, "direct-sibling", ConcurrencyAcquireMode.SkipOnFull, TimeSpan.FromSeconds(1), CancellationToken.None);
        leaf.SiteModelName = "latest";
        await db.UpdateAsync(leaf);
        // Rebuild the published cache while protection is active, then save again.
        await cache.GetAllRouteTargetsAsync(CancellationToken.None);
        limiter.TryDeferRuntimeRouteTargetsRefresh("source", identities, await cache.CaptureEffectiveRouteTargetsAsync("source", CancellationToken.None)).Should().BeTrue();
        first.Dispose();
        (await cache.CaptureEffectiveRouteTargetsAsync("parent", CancellationToken.None)).First().SiteModelName.Should().Be("old");
        second.Dispose();
        (await cache.CaptureEffectiveRouteTargetsAsync("parent", CancellationToken.None)).First().SiteModelName.Should().Be("latest");
        duringFirst.First().SiteModelName.Should().Be("old");
    }

    [Fact]
    public void Route_target_clone_detaches_all_mutable_metadata_and_intersects_parent_schedule()
    {
        var target = new CachedProxyRouteTarget
        {
            FallbackGroupId = Guid.NewGuid(), FallbackGroupOrder = 4,
            SourceRouteEntryId = Guid.NewGuid(), SourceRouteEntryName = "source", SourcePoolId = Guid.NewGuid(),
            ParentAvailabilityMode = "AvailableOnly", ParentTimeRangesJson = "[{\"start\":\"09:00\",\"end\":\"17:00\"}]",
            AvailabilityMode = "AvailableOnly", TimeRangesJson = "[{\"start\":\"12:00\",\"end\":\"20:00\"}]",
            ExtraHeaders = new() { ["X-Test"] = "one" },
            CompatibilityRules = new[] { new CompatibilityRule { Op = "default", Key = "k", Value = "v", Scope = "bridge" } }
        };
        var clone = target.Clone();
        clone.Should().BeEquivalentTo(target);
        clone.ExtraHeaders["X-Test"] = "two";
        clone.CompatibilityRules[0].Value = "changed";
        target.ExtraHeaders["X-Test"].Should().Be("one");
        target.CompatibilityRules[0].Value.Should().Be("v");
        target.IsAvailableAt(new TimeOnly(10, 0)).Should().BeFalse();
        target.IsAvailableAt(new TimeOnly(13, 0)).Should().BeTrue();
        target.IsAvailableAt(new TimeOnly(19, 0)).Should().BeFalse();
    }

    /// <summary>
    /// 验证访问密钥缓存失效后会重新读取最新启用状态。
    /// </summary>
    [Fact]
    public async Task InvalidateAccessKeys_refreshes_validation_result()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();

        SqlSugarSetup.InitializeDatabase(db.Client);

        var rawKey = "cache-key";
        db.ProxyAccessKeys.Add(new ProxyAccessKey
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            KeyName = "cache",
            PlainKey = rawKey,
            AccessKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))),
            MaskedValue = "sk-***",
            IsEnabled = true
        });
        await db.SaveChangesAsync();

        (await cache.ValidateAccessKeyAsync(rawKey, CancellationToken.None)).Should().NotBeNull();

        var accessKey = await db.ProxyAccessKeys.SingleAsync();
        accessKey.IsEnabled = false;
        await db.UpdateAsync(accessKey);

        // 缓存未失效前仍会命中旧快照，这里先确认失效动作确实有意义。
        (await cache.ValidateAccessKeyAsync(rawKey, CancellationToken.None)).Should().NotBeNull();

        cache.InvalidateAccessKeys();

        (await cache.ValidateAccessKeyAsync(rawKey, CancellationToken.None)).Should().BeNull();
    }

    /// <summary>
    /// 验证运行时设置缓存失效后会刷新超时和重试配置。
    /// </summary>
    [Fact]
    public async Task InvalidateRuntimeSettings_refreshes_timeout_and_retry_values()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();

        SqlSugarSetup.InitializeDatabase(db.Client);

        // 单例表 Id=1 可能已被启动逻辑创建，先删除避免唯一约束冲突。


        db.Client.Deleteable<SystemRuntimeSettings>().Where(x => x.Id == 1).ExecuteCommand();


        db.SystemRuntimeSettings.Add(new SystemRuntimeSettings
        {
            Id = 1,
            ProxyRequestTimeoutSeconds = 8,
            ProxyRetryCount = 1,
            DetectionRequestTimeoutSeconds = 10,
            DetectionRetryCount = 0,
            DetectionConcurrency = 1,
            CircuitBreakerFailureThreshold = 5,
            CircuitBreakerRecoveryMinutes = 2,
            UsageLogRetentionDays = 7,
            UsageLogAutoCleanupEnabled = true
        });
        await db.SaveChangesAsync();

        var before = await cache.GetRuntimeSettingsAsync(CancellationToken.None);
        before.ProxyRequestTimeoutSeconds.Should().Be(8);
        before.ProxyRetryCount.Should().Be(1);
        before.DetectionRequestTimeoutSeconds.Should().Be(10);
        before.CircuitBreakerFailureThreshold.Should().Be(5);

        var settings = await db.SystemRuntimeSettings.SingleAsync();
        settings.ProxyRequestTimeoutSeconds = 15;
        settings.ProxyRetryCount = 3;
        settings.DetectionRequestTimeoutSeconds = 22;
        settings.DetectionRetryCount = 2;
        settings.DetectionConcurrency = 4;
        settings.CircuitBreakerFailureThreshold = 7;
        settings.CircuitBreakerRecoveryMinutes = 9;
        settings.UsageLogAutoCleanupEnabled = false;
        await db.UpdateAsync(settings);

        cache.InvalidateRuntimeSettings();

        var after = await cache.GetRuntimeSettingsAsync(CancellationToken.None);
        after.ProxyRequestTimeoutSeconds.Should().Be(15);
        after.ProxyRetryCount.Should().Be(3);
        after.DetectionRequestTimeoutSeconds.Should().Be(22);
        after.DetectionRetryCount.Should().Be(2);
        after.DetectionConcurrency.Should().Be(4);
        after.CircuitBreakerFailureThreshold.Should().Be(7);
        after.CircuitBreakerRecoveryMinutes.Should().Be(9);
        after.UsageLogAutoCleanupEnabled.Should().BeFalse();
    }

    /// <summary>
    /// 验证路由缓存失效后会反映最新的启用状态。
    /// </summary>
    [Fact]
    public async Task InvalidateRouteTargets_refreshes_enabled_route_snapshot()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();

        SqlSugarSetup.InitializeDatabase(db.Client);

        var siteId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        db.Sites.Add(new Site
        {
            Id = siteId,
            Name = "Route Cache Site",
            BaseUrl = "https://route-cache.example.com",
            ApiKey = "route-key",
            ProtocolType = "OpenAI",
            SupportsOpenAi = true,
            SupportsAnthropic = true,
            IsEnabled = true
        });
        db.ProxyRouteRules.Add(new ProxyRouteRule
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            ExternalModelName = "cache-route-model",
            UpstreamModelName = "cache-upstream",
            SiteId = siteId,
            SiteModelName = "cache-site-model",
            Priority = 0,
            ModelPriority = 0,
            InstancePriority = 0,
            IsEnabled = true
        });
        await db.SaveChangesAsync();

        (await cache.GetRouteTargetsForModelAsync("OpenAI", "cache-route-model", CancellationToken.None)).Should().HaveCount(1);

        var route = await db.ProxyRouteRules.SingleAsync();
        route.IsEnabled = false;
        await db.UpdateAsync(route);

        cache.InvalidateRouteTargets();

        (await cache.GetRouteTargetsForModelAsync("OpenAI", "cache-route-model", CancellationToken.None)).Should().BeEmpty();
    }

    /// <summary>
    /// 验证路由缓存失效后会刷新启用站点名称。
    /// </summary>
    [Fact]
    public async Task InvalidateRouteTargets_refreshes_enabled_site_names()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();

        SqlSugarSetup.InitializeDatabase(db.Client);

        db.Sites.Add(new Site
        {
            Id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Name = "Old Site Name",
            BaseUrl = "https://site-name-cache.example.com",
            ApiKey = "site-key",
            ProtocolType = "OpenAI",
            SupportsOpenAi = true,
            SupportsAnthropic = false,
            IsEnabled = true
        });
        await db.SaveChangesAsync();

        var before = await cache.GetEnabledSiteNamesAsync(CancellationToken.None);
        before.Should().ContainKey(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        before[Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")].Should().Be("Old Site Name");

        var site = await db.Sites.SingleAsync();
        site.Name = "New Site Name";
        await db.UpdateAsync(site);

        cache.InvalidateRouteTargets();

        var after = await cache.GetEnabledSiteNamesAsync(CancellationToken.None);
        after[Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")].Should().Be("New Site Name");
    }

    /// <summary>
    /// 验证活跃调用结束前继续使用旧路由顺序，释放后再读取新顺序。
    /// </summary>
    [Fact]
    public async Task DeferRuntimeRouteTargetsRefresh_keeps_previous_order_until_active_slot_released()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
        var limiter = scope.ServiceProvider.GetRequiredService<ModelConcurrencyLimiter>();

        SqlSugarSetup.InitializeDatabase(db.Client);

        var firstSiteId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var secondSiteId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        db.Sites.AddRange(
            new Site
            {
                Id = firstSiteId,
                Name = "First Site",
                BaseUrl = "https://first.example.com",
                ApiKey = "first-key",
                ProtocolType = "OpenAI",
                SupportsOpenAi = true,
                SupportsAnthropic = false,
                IsEnabled = true
            },
            new Site
            {
                Id = secondSiteId,
                Name = "Second Site",
                BaseUrl = "https://second.example.com",
                ApiKey = "second-key",
                ProtocolType = "OpenAI",
                SupportsOpenAi = true,
                SupportsAnthropic = false,
                IsEnabled = true
            });
        db.ProxyRouteRules.AddRange(
            new ProxyRouteRule
            {
                Id = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                ExternalModelName = "deferred-route-model",
                UpstreamModelName = "first-upstream",
                SiteId = firstSiteId,
                SiteModelName = "first-model",
                Priority = 0,
                ModelPriority = 0,
                InstancePriority = 0,
                IsEnabled = true
            },
            new ProxyRouteRule
            {
                Id = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
                ExternalModelName = "deferred-route-model",
                UpstreamModelName = "second-upstream",
                SiteId = secondSiteId,
                SiteModelName = "second-model",
                Priority = 1,
                ModelPriority = 1,
                InstancePriority = 0,
                IsEnabled = true
            });
        await db.SaveChangesAsync();

        var before = await cache.GetRouteTargetsForModelAsync("OpenAI", "deferred-route-model", CancellationToken.None);
        before.Select(x => x.SiteModelName).Should().Equal("first-model", "second-model");

        using var activeHandle = await limiter.AcquireAsync(
            _serviceProvider,
            firstSiteId,
            "first-model",
            ConcurrencyAcquireMode.WaitForSlot,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        activeHandle.Acquired.Should().BeTrue();

        var activeSnapshot = new[]
        {
            new ActiveRouteTargetSnapshot(
                new RouteTargetIdentity(firstSiteId, "first-model"),
                limiter.ListActiveSlotIds(firstSiteId, "first-model"))
        };
        var previousRoutes = before.ToList();

        var firstRule = await db.ProxyRouteRules.SingleAsync(x => x.SiteModelName == "first-model");
        var secondRule = await db.ProxyRouteRules.SingleAsync(x => x.SiteModelName == "second-model");
        firstRule.Priority = 1;
        firstRule.ModelPriority = 1;
        secondRule.Priority = 0;
        secondRule.ModelPriority = 0;
        await db.UpdateAsync(firstRule);
        await db.UpdateAsync(secondRule);

        cache.DeferRuntimeRouteTargetsRefresh("deferred-route-model", activeSnapshot, previousRoutes);

        var duringActive = await cache.GetRouteTargetsForModelAsync("OpenAI", "deferred-route-model", CancellationToken.None);
        duringActive.Select(x => x.SiteModelName).Should().Equal("first-model", "second-model");

        activeHandle.Dispose();

        var afterRelease = await cache.GetRouteTargetsForModelAsync("OpenAI", "deferred-route-model", CancellationToken.None);
        afterRelease.Select(x => x.SiteModelName).Should().Equal("second-model", "first-model");
    }

    /// <summary>
    /// 验证候选路由会保持后台配置顺序，并把协议兼容处理留给控制器。
    /// </summary>
    [Fact]
    public async Task Get_route_targets_for_model_prioritizes_matching_protocol_before_bridge_targets()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();

        SqlSugarSetup.InitializeDatabase(db.Client);

        var openAiSiteId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var anthropicSiteId = Guid.Parse("55555555-5555-5555-5555-555555555555");

        db.Sites.AddRange(
            new Site
            {
                Id = openAiSiteId,
                Name = "OpenAI Only",
                BaseUrl = "https://openai-only.example.com",
                ApiKey = "openai-key",
                ProtocolType = "OpenAI",
                SupportsOpenAi = true,
                SupportsAnthropic = false,
                IsEnabled = true
            },
            new Site
            {
                Id = anthropicSiteId,
                Name = "Anthropic Native",
                BaseUrl = "https://anthropic-native.example.com",
                ApiKey = "anthropic-key",
                ProtocolType = "Anthropic",
                SupportsOpenAi = false,
                SupportsAnthropic = true,
                IsEnabled = true
            });

        db.ProxyRouteRules.AddRange(
            new ProxyRouteRule
            {
                Id = Guid.Parse("66666666-6666-6666-6666-666666666666"),
                ExternalModelName = "dual-route-model",
                UpstreamModelName = "openai-upstream",
                SiteId = openAiSiteId,
                SiteModelName = "openai-model",
                Priority = 0,
                ModelPriority = 0,
                InstancePriority = 0,
                IsEnabled = true
            },
            new ProxyRouteRule
            {
                Id = Guid.Parse("77777777-7777-7777-7777-777777777777"),
                ExternalModelName = "dual-route-model",
                UpstreamModelName = "anthropic-upstream",
                SiteId = anthropicSiteId,
                SiteModelName = "anthropic-model",
                Priority = 1,
                ModelPriority = 1,
                InstancePriority = 1,
                IsEnabled = true
            });

        await db.SaveChangesAsync();

        var anthropicRoutes = await cache.GetRouteTargetsForModelAsync("Anthropic", "dual-route-model", CancellationToken.None);
        anthropicRoutes.Should().HaveCount(2);
        // 当前缓存按后台配置顺序返回候选路由，协议不匹配时交给控制器走兼容转发。
        anthropicRoutes[0].ResolveProtocolForClient("Anthropic").Should().Be("OpenAI");
        anthropicRoutes[0].SiteId.Should().Be(openAiSiteId);
        anthropicRoutes[1].ResolveProtocolForClient("Anthropic").Should().Be("Anthropic");

        var openAiRoutes = await cache.GetRouteTargetsForModelAsync("OpenAI", "dual-route-model", CancellationToken.None);
        openAiRoutes.Should().HaveCount(2);
        openAiRoutes[0].ResolveProtocolForClient("OpenAI").Should().Be("OpenAI");
        openAiRoutes[0].SiteId.Should().Be(openAiSiteId);
        openAiRoutes[1].ResolveProtocolForClient("OpenAI").Should().Be("Anthropic");
    }

    /// <summary>
    /// 仅支持 Responses 的缓存路由应为 OpenAI / Anthropic 客户端都返回 Responses 协议。
    /// </summary>
    [Fact]
    public void ResolveProtocolForClient_prefers_responses_for_openai_and_anthropic_clients()
    {
        var route = new CachedProxyRouteTarget
        {
            ProtocolType = "Responses",
            SupportsOpenAi = false,
            SupportsAnthropic = false
        };

        route.ResolveProtocolForClient("OpenAI").Should().Be("Responses");
        route.ResolveProtocolForClient("Anthropic").Should().Be("Responses");
    }

    /// <summary>
    /// 旧站点只保存 ProtocolType=Responses 时，缓存仍应识别为原生 Responses 站点。
    /// </summary>
    [Fact]
    public async Task Get_route_targets_supports_legacy_responses_protocol_type()
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();

        SqlSugarSetup.InitializeDatabase(db.Client);

        var siteId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        db.Sites.Add(new Site
        {
            Id = siteId,
            Name = "Legacy Responses Site",
            BaseUrl = "https://legacy-responses.example.com",
            ApiKey = "legacy-key",
            ProtocolType = "Responses",
            SupportsOpenAi = true,
            SupportsAnthropic = false,
            SupportsResponses = false,
            IsEnabled = true
        });
        db.ProxyRouteRules.Add(new ProxyRouteRule
        {
            Id = Guid.Parse("99999999-9999-9999-9999-999999999999"),
            ExternalModelName = "legacy-responses-model",
            UpstreamModelName = "legacy-upstream",
            SiteId = siteId,
            SiteModelName = "legacy-site-model",
            IsEnabled = true
        });
        await db.SaveChangesAsync();

        var routes = await cache.GetRouteTargetsForModelAsync(
            "Responses",
            "legacy-responses-model",
            CancellationToken.None);

        routes.Should().ContainSingle();
        routes[0].SupportsResponses.Should().BeTrue();
        routes[0].ResolveProtocolForClient("Responses").Should().Be("Responses");
        // 旧数据同时声明了 OpenAI 能力时，OpenAI 客户端仍应保留原生 OpenAI 路径。
        routes[0].ResolveProtocolForClient("OpenAI").Should().Be("OpenAI");
    }

    [Fact]
    public async Task GetRouteTargetsForModelAsync_Resolves_ClientEmulation_From_Mapping_And_Model()
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var cache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();

        SqlSugarSetup.InitializeDatabase(db.Client);

        var siteId = Guid.NewGuid();
        var modelId = Guid.NewGuid();

        db.Sites.Add(new Site
        {
            Id = siteId,
            Name = "GeminiPro1",
            BaseUrl = "https://daily-cloudcode-pa.googleapis.com",
            ApiKey = "g-key",
            ProtocolType = "Gemini",
            SupportsOpenAi = false,
            SupportsAnthropic = false,
            SupportsResponses = false,
            ClientEmulation = "None",
            IsEnabled = true
        });

        db.ModelLibraryItems.Add(new ModelLibraryItem
        {
            Id = modelId,
            ModelName = "gemini-3.7-flash-high",
            DisplayName = "Gemini 3.7 Flash High",
            ClientEmulation = "Antigravity",
            IsEnabled = true
        });

        db.SiteModelMappings.Add(new SiteModelMapping
        {
            Id = Guid.NewGuid(),
            SiteId = siteId,
            ModelLibraryItemId = modelId,
            RemoteModelName = "gemini-3.7-flash-high",
            ClientEmulation = "Antigravity",
            IsEnabled = true
        });

        db.ProxyRouteRules.Add(new ProxyRouteRule
        {
            Id = Guid.NewGuid(),
            ExternalModelName = "1M",
            UpstreamModelName = "gemini-3.7-flash-high",
            SiteId = siteId,
            SiteModelName = "gemini-3.7-flash-high",
            IsEnabled = true
        });

        await db.SaveChangesAsync();

        var routes = await cache.GetRouteTargetsForModelAsync(
            "OpenAI",
            "1M",
            CancellationToken.None);

        routes.Should().ContainSingle();
        var route = routes[0];
        route.ClientEmulation.Should().Be("Antigravity");
        route.ResolveProtocolForClient("OpenAI").Should().Be("Gemini");

        var isAntigravity = ProxyProtocolBridge.IsAntigravityTarget(route.BaseUrl);
        isAntigravity.Should().BeTrue();

        var headers = ClientEmulationEngine.ResolveHeaders(
            route.ClientEmulation,
            route.ExtraHeaders,
            route.SiteModelName,
            route.GoogleProjectId,
            isAntigravity);

        headers.Should().ContainKey("User-Agent");
        headers["User-Agent"].Should().Contain("antigravity");
    }

    /// <summary>
    /// 释放测试过程中创建的资源。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}
