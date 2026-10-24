using AITool.Application.Accounts;
using AITool.Application.Proxy;
using AITool.Application.Sites;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Web.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AITool.IntegrationTests.Services;

/// <summary>
/// 路由候选策略测试：熔断存储的分级休息（Rest）语义、失败分类词表、
/// 会话指纹解析与亲和排序、配额感知排序（配 DB 的 SiteKey 额度缓存）。
/// </summary>
public sealed class RouteCandidatePolicyTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-route-policy-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _serviceProvider;
    private readonly RouteCircuitStateStore _circuitStore = new();
    private readonly SessionAffinityService _sessionAffinity = new();

    public RouteCandidatePolicyTests()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddLogging();
        services.AddSqlSugar($"Data Source={_databasePath}");
        services.AddSingleton<FakeSiteQuotaProvider>();
        services.AddSingleton<ISiteQuotaProvider>(sp => sp.GetRequiredService<FakeSiteQuotaProvider>());
        _serviceProvider = services.BuildServiceProvider();
        SqlSugarSetup.InitializeDatabase(_serviceProvider.GetRequiredService<SqlSugar.ISqlSugarClient>());
    }

    // ---- 熔断存储：分级休息 ----

    [Fact]
    public void Rest_blocks_route_until_given_time_and_clears_fail_count()
    {
        var key = Guid.NewGuid();
        _circuitStore.Block(key);
        _circuitStore.Block(key);
        _circuitStore.Rest(key, DateTimeOffset.UtcNow.AddMinutes(10), "配额耗尽");
        _circuitStore.IsBlocked(key).Should().BeTrue();
        _circuitStore.GetRest(key).Should().NotBeNull();
        _circuitStore.GetRest(key)!.Value.Reason.Should().Be("配额耗尽");

        // 成功后休息与计数一并清除
        _circuitStore.Succeed(key);
        _circuitStore.IsBlocked(key).Should().BeFalse();
        _circuitStore.GetRest(key).Should().BeNull();
    }

    [Fact]
    public void Rest_until_past_time_is_no_op()
    {
        var key = Guid.NewGuid();
        _circuitStore.Rest(key, DateTimeOffset.UtcNow.AddSeconds(-1), "过期");
        _circuitStore.IsBlocked(key).Should().BeFalse();
    }

    [Fact]
    public void GetAllCircuitStates_merges_rest_into_blocked_view()
    {
        var key = Guid.NewGuid();
        _circuitStore.Rest(key, DateTimeOffset.UtcNow.AddMinutes(5), "限流");
        var states = _circuitStore.GetAllCircuitStates();
        states.Should().ContainKey(key);
        states[key].IsBlocked.Should().BeTrue();
        states[key].RestReason.Should().Be("限流");
        states[key].BlockedUntil.Should().NotBeNull();

        _circuitStore.Reset(key);
        _circuitStore.GetAllCircuitStates().Should().NotContainKey(key);
    }

    // ---- 失败分类词表 ----

    [Theory]
    [InlineData("""{"error":{"code":"usage_limit_reached"}}""", true)]
    [InlineData("""{"error":{"message":"余额不足或无可用资源包"}}""", true)]
    [InlineData("Insufficient quota", true)]
    [InlineData("""{"error":{"message":"rate limited, slow down"}}""", false)]
    [InlineData("", false)]
    public void Quota_exhaustion_classification(string body, bool expected)
    {
        RouteCandidatePolicyService.LooksLikeQuotaExhaustion(body).Should().Be(expected);
    }

    [Fact]
    public void Client_error_strict_fallback_gating()
    {
        var strict = true;
        var shape400 = new ProxyForwardResult { StatusCode = 400, ResponseBody = """{"error":{"message":"invalid tool schema"}}""" };
        var model400 = new ProxyForwardResult { StatusCode = 400, ResponseBody = """{"error":{"code":"model_not_found","message":"model x not found"}}""" };
        var server500 = new ProxyForwardResult { StatusCode = 500, ResponseBody = "boom" };

        RouteCandidatePolicyService.ClientErrorBlocksFallback(strict, shape400).Should().BeTrue("纯请求形态错误不回退");
        RouteCandidatePolicyService.ClientErrorBlocksFallback(strict, model400).Should().BeFalse("模型未提供换一家可能就好");
        RouteCandidatePolicyService.ClientErrorBlocksFallback(strict, server500).Should().BeFalse("非 4xx 不拦截");
        RouteCandidatePolicyService.ClientErrorBlocksFallback(false, shape400).Should().BeFalse("关闭时全量回退");
    }

    // ---- 会话亲和 ----

    [Fact]
    public void Conversation_key_uses_header_first()
    {
        var request = new Microsoft.AspNetCore.Http.DefaultHttpContext().Request;
        request.Headers["X-Session-Id"] = "abc123";

        var key = SessionAffinityService.TryResolveConversationKey(request, "glm-5.3", """{"messages":[]}""");
        key.Should().Be("h:abc123");
    }

    [Fact]
    public void Conversation_key_hashes_first_user_message()
    {
        var request = new Microsoft.AspNetCore.Http.DefaultHttpContext().Request;
        var body = """
            {"model":"gpt","messages":[{"role":"system","content":"sys"},{"role":"user","content":"帮我重构这个函数"}]}
            """;

        var key1 = SessionAffinityService.TryResolveConversationKey(request, "gpt", body);
        var key2 = SessionAffinityService.TryResolveConversationKey(request, "gpt", body + " ");
        key1.Should().NotBeNull().And.StartWith("m:");
        key2.Should().Be(key1, "首条用户消息相同即同一会话");

        var otherBody = body.Replace("帮我重构这个函数", "写个单元测试");
        SessionAffinityService.TryResolveConversationKey(request, "gpt", otherBody).Should().NotBe(key1);

        SessionAffinityService.TryResolveConversationKey(request, "gpt", """{"messages":[]}""").Should().BeNull("无用户消息无指纹");
        SessionAffinityService.TryResolveConversationKey(request, "gpt", "not json").Should().BeNull();
    }

    [Fact]
    public void Affinity_orders_pinned_candidate_first()
    {
        var conversationKey = "h:s1";
        var routes = new List<CachedProxyRouteTarget>
        {
            new() { CircuitKey = Guid.NewGuid() },
            new() { CircuitKey = Guid.NewGuid() },
            new() { CircuitKey = Guid.NewGuid() },
        };
        var pinned = routes[2].CircuitKey;
        _sessionAffinity.RecordSuccess(conversationKey, pinned);

        var list = routes.ToList();
        _sessionAffinity.TryOrderForConversation(list, conversationKey).Should().BeTrue();
        list[0].CircuitKey.Should().Be(pinned);
        list.Should().HaveCount(3, "只重排不删减");

        _sessionAffinity.TryOrderForConversation(list, "h:unknown").Should().BeFalse("未命中");
    }

    // ---- 配额感知排序（配 DB） ----

    [Fact]
    public async Task Quota_ordering_puts_soonest_reset_first()
    {
        var provider = _serviceProvider.GetRequiredService<FakeSiteQuotaProvider>();
        var soonSite = Guid.NewGuid();
        var lateSite = Guid.NewGuid();
        var noQuotaSite = Guid.NewGuid();
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Sites.Add(new Domain.Sites.Site { Id = soonSite, Name = "soon", BaseUrl = "https://a.com", IsEnabled = true });
            db.Sites.Add(new Domain.Sites.Site { Id = lateSite, Name = "late", BaseUrl = "https://b.com", IsEnabled = true });
            db.Sites.Add(new Domain.Sites.Site { Id = noQuotaSite, Name = "none", BaseUrl = "https://c.com", IsEnabled = true });
            db.SiteKeys.Add(new Domain.Sites.SiteKey
            {
                Id = Guid.NewGuid(), SiteId = soonSite, KeyValue = "k1", Priority = 0, IsEnabled = true,
                LastQuotaRawJson = provider.BuildRawJson(resetInHours: 3)
            });
            db.SiteKeys.Add(new Domain.Sites.SiteKey
            {
                Id = Guid.NewGuid(), SiteId = lateSite, KeyValue = "k2", Priority = 0, IsEnabled = true,
                LastQuotaRawJson = provider.BuildRawJson(resetInHours: 72)
            });
            await db.SaveChangesAsync();
        }

        var policy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);

        var soonKey = Guid.NewGuid();
        var lateKey = Guid.NewGuid();
        var noQuotaKey = Guid.NewGuid();
        var routes = new List<CachedProxyRouteTarget>
        {
            // 静态顺序：无额度 → 晚重置 → 早重置
            new() { CircuitKey = noQuotaKey, SiteId = noQuotaSite },
            new() { CircuitKey = lateKey, SiteId = lateSite },
            new() { CircuitKey = soonKey, SiteId = soonSite },
        };

        var ordered = await policy.PreorderCandidatesAsync(routes, quotaOrderingEnabled: true, conversationKey: null, CancellationToken.None);

        ordered[0].CircuitKey.Should().Be(soonKey, "最早重置者优先（重置即清零，不用就是浪费）");
        ordered[1].CircuitKey.Should().Be(lateKey);
        ordered[2].CircuitKey.Should().Be(noQuotaKey, "无额度数据的候选殿后且保持原相对次序");
    }

    [Fact]
    public async Task RecordRouteFailure_429_quota_rests_until_reset()
    {
        var provider = _serviceProvider.GetRequiredService<FakeSiteQuotaProvider>();
        var siteId = Guid.NewGuid();
        var siteKeyId = Guid.NewGuid();
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Sites.Add(new Domain.Sites.Site { Id = siteId, Name = "q", BaseUrl = "https://q.com", IsEnabled = true });
            db.SiteKeys.Add(new Domain.Sites.SiteKey
            {
                Id = siteKeyId, SiteId = siteId, KeyValue = "k", Priority = 0, IsEnabled = true,
                LastQuotaRawJson = provider.BuildRawJson(resetInHours: 6)
            });
            await db.SaveChangesAsync();
        }

        var policy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);
        var route = new CachedProxyRouteTarget { CircuitKey = Guid.NewGuid(), SiteId = siteId, SiteKeyId = siteKeyId };

        await policy.RecordRouteFailureAsync(route, 429, """{"error":{"code":"usage_limit_reached"}}""",
            retryAfterSeconds: null, tieredCooldownEnabled: true,
            meta: new CircuitRouteMeta("q", "m"), CancellationToken.None);

        _circuitStore.IsBlocked(route.CircuitKey).Should().BeTrue();
        var rest = _circuitStore.GetRest(route.CircuitKey);
        rest.Should().NotBeNull();
        rest!.Value.Reason.Should().Contain("配额");
        rest.Value.UntilUtc.Should().BeAfter(DateTimeOffset.UtcNow.AddHours(5));
        rest.Value.UntilUtc.Should().BeBefore(DateTimeOffset.UtcNow.AddHours(7));
    }

    [Fact]
    public async Task RecordRouteFailure_disabled_falls_back_to_counting()
    {
        var policy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);
        var route = new CachedProxyRouteTarget { CircuitKey = Guid.NewGuid(), SiteId = Guid.NewGuid() };

        await policy.RecordRouteFailureAsync(route, 429, "usage limit", null, tieredCooldownEnabled: false,
            meta: null, CancellationToken.None);

        _circuitStore.IsBlocked(route.CircuitKey).Should().BeFalse("单次失败只计数不熔断（阈值 5）");
        _circuitStore.GetRest(route.CircuitKey).Should().BeNull("未启用分级冷却不休息");
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        TryDelete(_databasePath);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
        try { File.Delete(path + "-wal"); } catch { }
        try { File.Delete(path + "-shm"); } catch { }
    }

    /// <summary>额度解析桩：LastQuotaRawJson 按标记还原出带重置时间的窗口。</summary>
    private sealed class FakeSiteQuotaProvider : ISiteQuotaProvider
    {
        public string ProviderKey => "fake";
        public string ProviderLabel => "Fake";
        public bool MatchesBaseUrl(string baseUrl) => false;

        public Task<SiteQuotaQueryResult> QueryAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
            => Task.FromResult(new SiteQuotaQueryResult { Success = false, Error = "not used" });

        public SiteQuotaQueryResult? ParseCached(string rawJson)
        {
            if (rawJson is null || !rawJson.Contains("\"fakeQuota\""))
            {
                return null;
            }

            var resetHours = int.Parse(rawJson.Split(':')[1].TrimEnd('}'));
            return new SiteQuotaQueryResult
            {
                Success = true,
                Windows = [new SiteQuotaWindow("five_hour", "5 小时窗口", 30, null, DateTimeOffset.UtcNow.AddHours(resetHours))]
            };
        }

        public string BuildRawJson(int resetInHours) => $"{{\"fakeQuota\":{resetInHours}}}";
    }
}
