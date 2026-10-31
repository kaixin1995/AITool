using System.Net;
using System.Net.Http.Headers;
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

        // 请求模型名（不是池名）：应命中优先级 0 的成员 B。
        fake.Requests.Clear();
        var response = await client.SendAsync(ChatRequest(Model));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Requests.Should().HaveCount(1);
        fake.Requests[0].TargetApiKey.Should().Be(KeyB, "池内静态顺序：B(优先级0)首选");

        // B 持续失败：无感回退到 A（同模型扇出，不是跨模型回退）。
        fake.ForwardResultFactory = r => r.TargetApiKey == KeyB
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
            [KeyB, KeyA],
            "成员 B 失败后同模型无感回退到成员 A——号池账号扇出的核心语义");
    }

    private static HttpRequestMessage ChatRequest(string model) => new(HttpMethod.Post, "/v1/chat/completions")
    {
        Content = new StringContent(
            $$"""{"model":"{{model}}","messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8,
            "application/json"),
        Headers = { Authorization = new AuthenticationHeaderValue("Bearer", "test-key") },
    };

    /// <summary>两个 Codex 隐藏站点 + 账号行 + m1 映射 + 手动池（B 优先级 0）。</summary>
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
