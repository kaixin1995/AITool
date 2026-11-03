using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AITool.IntegrationTests.Proxy;

/// <summary>
/// 号池端到端转发链路：真实同步服务生成规则 → 真实控制器循环选候选 → 假转发记录
/// 实际命中。验证号池的核心价值主张——对外照常按模型名调用，池在背后扇出到成员
/// （池内静态顺序决定首选成员，失败无感回退到下一成员）。
/// </summary>
public sealed class AccountPoolForwardE2eTests
{
    private static readonly Guid SiteA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SiteB = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private const string KeyA = "pool-at-a";
    private const string KeyB = "pool-at-b";
    private const string Model = "pool-e2e-m1";

    [Fact]
    public async Task Pool_rules_serve_model_with_member_fanout_end_to_end()
    {
        var fake = new FakeProxyForwardService
        {
            ForwardResultFactory = _ => new AITool.Application.Proxy.ProxyForwardResult
            {
                StatusCode = 200,
                ResponseBody = """{"choices":[{"message":{"content":"ok"}}]}""",
                Success = true,
            },
        };
        await using var factory = new ProxyFallbackWebApplicationFactory(fake);
        using var client = factory.CreateClient();

        await SeedPoolAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var sync = scope.ServiceProvider.GetRequiredService<AccountPoolSyncService>();
            var result = await sync.SyncPoolAsync((await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .AccountPools.FirstAsync()).Id, CancellationToken.None);
            result.RuleCount.Should().Be(2, "两成员各持 m1 → 两条扇出规则");
        }

        // 请求模型名（不是池名）：新规则按账号名称排序，成员 A 首选。
        fake.Requests.Clear();
        var response = await client.SendAsync(ChatRequest(Model));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Requests.Should().HaveCount(1);
        fake.Requests[0].TargetApiKey.Should().Be(KeyA, "新规则按账号名称排序，A 首选");

        // A 持续失败：无感回退到 B（同模型扇出，不是跨模型回退）。
        fake.ForwardResultFactory = r => r.TargetApiKey == KeyA
            ? new AITool.Application.Proxy.ProxyForwardResult { StatusCode = 502, Success = false }
            : new AITool.Application.Proxy.ProxyForwardResult
            {
                StatusCode = 200,
                ResponseBody = """{"choices":[{"message":{"content":"ok-a"}}]}""",
                Success = true,
            };
        fake.Requests.Clear();
        var fallbackResponse = await client.SendAsync(ChatRequest(Model));
        fallbackResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Requests.Select(r => r.TargetApiKey).Should().BeEquivalentTo(
            [KeyA, KeyB], options => options.WithStrictOrdering(),
            "成员 A 失败后同模型无感回退到成员 B——号池账号扇出的核心语义");
    }

    [Fact]
    public async Task Pool_queue_edit_changes_real_forwarding_and_survives_sync_and_parent_reference()
    {
        var fake = new FakeProxyForwardService { ForwardResultFactory = _ => new AITool.Application.Proxy.ProxyForwardResult {
            StatusCode = 200, ResponseBody = """{"choices":[{"message":{"content":"ok"}}]}""", Success = true
        }};
        await using var factory = new ProxyFallbackWebApplicationFactory(fake);
        using var client = factory.CreateClient();
        await SeedPoolAsync(factory);
        Guid entryId, poolId;
        List<ProxyRouteRule> rules;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            poolId = (await db.AccountPools.FirstAsync()).Id;
            await scope.ServiceProvider.GetRequiredService<AccountPoolSyncService>().SyncPoolAsync(poolId, default);
            entryId = (await db.ProxyRouteEntries.FirstAsync(x => x.EntryName == Model)).Id;
            rules = (await db.ProxyRouteRules.Where(x => x.ExternalModelName == Model).ToListAsync()).OrderBy(x => x.Priority).ToList();
        }
        (await client.PostAsJsonAsync("/api/admin/route-rules/save", new {
            externalModelName = "pool-parent", supportsRouteReferences = true,
            rules = new[] { new { targetKind = "RouteEntry", targetRouteEntryId = entryId } }
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        // Warm both cached plans before editing.
        (await client.SendAsync(ChatRequest(Model))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(ChatRequest("pool-parent"))).StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Requests.Clear();
        var response = await client.PostAsJsonAsync("/api/admin/route-rules/pool-queue", new {
            entryId, rules = rules.AsEnumerable().Reverse().Select(r => new {
                ruleId = r.Id, expectedPriority = r.Priority, expectedModelPriority = r.ModelPriority,
                expectedInstancePriority = r.InstancePriority, expectedIsEnabled = r.IsEnabled, isEnabled = r.SiteId != SiteA
            }).ToArray()
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<AccountPoolSyncService>().SyncPoolAsync(poolId, default);
        (await client.SendAsync(ChatRequest(Model))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(ChatRequest("pool-parent"))).StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Requests.Select(r => r.TargetApiKey).Should().Equal(KeyB, KeyB);
        await using var verify = factory.Services.CreateAsyncScope();
        var db2 = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var after = (await db2.ProxyRouteRules.Where(x => x.ExternalModelName == Model).ToListAsync()).OrderBy(x => x.Priority).ToList();
        after.Select(x => x.Id).Should().Equal(rules[1].Id, rules[0].Id);
        after.Single(x => x.SiteId == SiteA).IsEnabled.Should().BeFalse();
        (await db2.ProxyRouteEntries.FirstAsync(x => x.EntryName == Model)).Id.Should().Be(entryId);
    }
    [Fact]
    public async Task Pool_reorder_alone_updates_warm_direct_and_reference_plans_without_sync()
    {
        var fake = new FakeProxyForwardService { ForwardResultFactory = _ => new AITool.Application.Proxy.ProxyForwardResult {
            StatusCode = 200, ResponseBody = """{"choices":[{"message":{"content":"ok"}}]}""", Success = true
        }};
        await using var factory = new ProxyFallbackWebApplicationFactory(fake);
        using var client = factory.CreateClient();
        await SeedPoolAsync(factory);
        Guid entryId;
        List<ProxyRouteRule> rules;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var poolId = (await db.AccountPools.FirstAsync()).Id;
            await scope.ServiceProvider.GetRequiredService<AccountPoolSyncService>().SyncPoolAsync(poolId, default);
            entryId = (await db.ProxyRouteEntries.FirstAsync(x => x.EntryName == Model)).Id;
            rules = (await db.ProxyRouteRules.Where(x => x.ExternalModelName == Model).ToListAsync()).OrderBy(x => x.Priority).ToList();
        }
        (await client.PostAsJsonAsync("/api/admin/route-rules/save", new {
            externalModelName = "pool-parent", supportsRouteReferences = true,
            rules = new[] { new { targetKind = "RouteEntry", targetRouteEntryId = entryId } }
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(ChatRequest(Model))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(ChatRequest("pool-parent"))).StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Requests.Select(r => r.TargetApiKey).Should().Equal(KeyA, KeyA);
        fake.Requests.Clear();
        var response = await client.PostAsJsonAsync("/api/admin/route-rules/pool-queue", new {
            entryId, rules = rules.AsEnumerable().Reverse().Select(r => new {
                ruleId = r.Id, expectedPriority = r.Priority, expectedModelPriority = r.ModelPriority,
                expectedInstancePriority = r.InstancePriority, expectedIsEnabled = r.IsEnabled, isEnabled = true
            }).ToArray()
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await client.SendAsync(ChatRequest(Model))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.SendAsync(ChatRequest("pool-parent"))).StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Requests.Select(r => r.TargetApiKey).Should().Equal(KeyB, KeyB);
    }
    private static HttpRequestMessage ChatRequest(string model) => new(HttpMethod.Post, "/v1/chat/completions")
    {
        Content = new StringContent(
            $$"""{"model":"{{model}}","messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8,
            "application/json"),
        Headers = { Authorization = new AuthenticationHeaderValue("Bearer", "test-key") },
    };

    /// <summary>显式准备两个 Codex 站点、账号和 m1 模型映射，不依赖供给默认导入。</summary>
    private static async Task SeedPoolAsync(ProxyFallbackWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var siteA = new Domain.Sites.Site
        {
            Id = SiteA, Name = "pool-a", BaseUrl = "https://a.example.com", ApiKey = KeyA,
            ProtocolType = "Responses", SupportsResponses = true, IsEnabled = true, ManagedSource = "Codex",
        };
        var siteB = new Domain.Sites.Site
        {
            Id = SiteB, Name = "pool-b", BaseUrl = "https://b.example.com", ApiKey = KeyB,
            ProtocolType = "Responses", SupportsResponses = true, IsEnabled = true, ManagedSource = "Codex",
        };
        var model = new Domain.Models.ModelLibraryItem { ModelName = Model, DisplayName = Model, IsEnabled = true };
        await db.Client.Insertable(new[] { siteA, siteB }).ExecuteCommandAsync();
        await db.Client.Insertable(model).ExecuteCommandAsync();
        await db.Client.Insertable(new[]
        {
            new Domain.SiteCatalog.SiteModelMapping { SiteId = SiteA, ModelLibraryItemId = model.Id, RemoteModelName = Model, IsEnabled = true },
            new Domain.SiteCatalog.SiteModelMapping { SiteId = SiteB, ModelLibraryItemId = model.Id, RemoteModelName = Model, IsEnabled = true },
        }).ExecuteCommandAsync();

        // 成员行必须挂在真实 CodexAccount 上（物化会清掉指向已消亡账号的行）。
        var accountA = new Domain.Codex.CodexAccount { DisplayName = "pool-e2e-a", LinkedSiteId = SiteA, AccessToken = KeyA };
        var accountB = new Domain.Codex.CodexAccount { DisplayName = "pool-e2e-b", LinkedSiteId = SiteB, AccessToken = KeyB };
        await db.Client.Insertable(new[] { accountA, accountB }).ExecuteCommandAsync();

        var pool = new AccountPool { Name = "pool-e2e", VendorKey = "codex", PlanType = null };
        await db.Client.Insertable(pool).ExecuteCommandAsync();
        await db.Client.Insertable(new[]
        {
            new AccountPoolMember { PoolId = pool.Id, AccountRefId = accountA.Id, LinkedSiteId = SiteA, Priority = 1 },
            new AccountPoolMember { PoolId = pool.Id, AccountRefId = accountB.Id, LinkedSiteId = SiteB, Priority = 0 },
        }).ExecuteCommandAsync();
    }
}
