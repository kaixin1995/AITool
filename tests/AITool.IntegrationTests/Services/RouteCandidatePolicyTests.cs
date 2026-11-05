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

    private readonly FakeAccountQuotaProvider _accountProvider = new("fake-account");
    private readonly FakeAccountQuotaProvider _secondAccountProvider = new("fake-second-account");

    public RouteCandidatePolicyTests()
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        services.AddLogging();
        services.AddSqlSugar($"Data Source={_databasePath}");
        services.AddSingleton<FakeSiteQuotaProvider>();
        services.AddSingleton<ISiteQuotaProvider>(sp => sp.GetRequiredService<FakeSiteQuotaProvider>());
        services.AddSingleton<FakeAccountQuotaProvider>(_accountProvider);
        services.AddSingleton<IAccountQuotaProvider>(_accountProvider);
        services.AddSingleton<IAccountQuotaProvider>(_secondAccountProvider);
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
    [InlineData("rate limit is exceeded — try again in 30s", false)]
    [InlineData("per-minute quota exceeded", false)]
    [InlineData("You exceeded your quota. Billing required.", true)]
    [InlineData("rate limit reached and 余额不足", true)]
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
    public async Task Quota_ordering_batch_loads_by_site_key_id()
    {
        var provider = _serviceProvider.GetRequiredService<FakeSiteQuotaProvider>();
        var siteWithKeys = Guid.NewGuid();
        var soonKey = Guid.NewGuid();
        var lateKey = Guid.NewGuid();
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Sites.Add(new Domain.Sites.Site { Id = siteWithKeys, Name = "multi-key", BaseUrl = "https://mk.com", IsEnabled = true });
            db.SiteKeys.Add(new Domain.Sites.SiteKey
            {
                Id = soonKey, SiteId = siteWithKeys, KeyValue = "k-soon", Priority = 0, IsEnabled = true,
                LastQuotaRawJson = provider.BuildRawJson(resetInHours: 2)
            });
            db.SiteKeys.Add(new Domain.Sites.SiteKey
            {
                Id = lateKey, SiteId = siteWithKeys, KeyValue = "k-late", Priority = 1, IsEnabled = true,
                LastQuotaRawJson = provider.BuildRawJson(resetInHours: 96)
            });
            await db.SaveChangesAsync();
        }

        var policy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);
        var routes = new List<CachedProxyRouteTarget>
        {
            new() { CircuitKey = Guid.NewGuid(), SiteId = siteWithKeys, SiteKeyId = lateKey },
            new() { CircuitKey = Guid.NewGuid(), SiteId = siteWithKeys, SiteKeyId = soonKey },
        };

        var ordered = await policy.PreorderCandidatesAsync(routes, quotaOrderingEnabled: true, conversationKey: null, CancellationToken.None);

        ordered[0].SiteKeyId.Should().Be(soonKey, "带 SiteKeyId 的候选批量按密钥 ID 精确命中各自的重置时间");
        ordered[1].SiteKeyId.Should().Be(lateKey);
    }

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
            meta: new CircuitRouteMeta("q", "m"), CancellationToken.None);

        _circuitStore.IsBlocked(route.CircuitKey).Should().BeFalse("单次失败只计数不熔断（阈值 5）");
        _circuitStore.GetRest(route.CircuitKey).Should().BeNull("未启用分级冷却不休息");
    }

    // ---- OAuth 账号表配额回退（隐藏站点无 SiteKey） ----

    [Fact]
    public async Task Quota_ordering_falls_back_to_oauth_account_quota_cache()
    {
        // OAuth 隐藏站点没有 SiteKey 行，巡检数据落在账号表：配额排序必须能回退读到，
        // 否则对 Codex/Kimi 等托管账号的「最早重置优先」完全失效（全部按无数据殿后）。
        var accountProvider = _serviceProvider.GetRequiredService<FakeAccountQuotaProvider>();
        var soonSite = Guid.NewGuid();
        var lateSite = Guid.NewGuid();
        accountProvider.Seed(soonSite, resetInHours: 2);
        accountProvider.Seed(lateSite, resetInHours: 96);

        var policy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);

        var soonKey = Guid.NewGuid();
        var lateKey = Guid.NewGuid();
        var routes = new List<CachedProxyRouteTarget>
        {
            // 静态顺序：晚重置在前。回退生效后应被重排为早重置优先。
            new() { CircuitKey = lateKey, SiteId = lateSite, AccountIdentity = new(accountProvider.ProviderKey, lateSite) },
            new() { CircuitKey = soonKey, SiteId = soonSite, AccountIdentity = new(accountProvider.ProviderKey, soonSite) },
        };

        var ordered = await policy.PreorderCandidatesAsync(routes, quotaOrderingEnabled: true, conversationKey: null, CancellationToken.None);

        ordered[0].CircuitKey.Should().Be(soonKey, "隐藏站点从账号表回退读取重置时刻，最早重置优先");
        ordered[1].CircuitKey.Should().Be(lateKey);
    }

    [Fact]
    public async Task Tiered_cooldown_rests_until_account_quota_reset()
    {
        // 分级冷却的「休到重置点」对隐藏站点同样要回退账号表，否则只剩 30 分钟盲休。
        var accountProvider = _serviceProvider.GetRequiredService<FakeAccountQuotaProvider>();
        var siteId = Guid.NewGuid();
        accountProvider.Seed(siteId, resetInHours: 6);

        var policy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);
        var route = new CachedProxyRouteTarget
        {
            CircuitKey = Guid.NewGuid(),
            SiteId = siteId,
            AccountIdentity = new(accountProvider.ProviderKey, siteId),
        };

        await policy.RecordRouteFailureAsync(route, 402, """{"error":{"message":"额度不足"}}""",
            retryAfterSeconds: null, tieredCooldownEnabled: true,
            meta: new CircuitRouteMeta("q", "m"), CancellationToken.None);

        var rest = _circuitStore.GetRest(route.CircuitKey);
        rest.Should().NotBeNull("配额耗尽应休到账号额度窗口重置点");
        rest!.Value.UntilUtc.Should().BeAfter(DateTimeOffset.UtcNow.AddHours(5), "回退账号表后按重置点（+6h）休息，而非 30 分钟默认值");
        rest.Value.UntilUtc.Should().BeBefore(DateTimeOffset.UtcNow.AddHours(7));
    }

    [Fact]
    public async Task Sites_with_site_key_quota_do_not_use_account_fallback()
    {
        // 有 Key 配额数据的普通站点不得被账号回退干扰（数据源边界）。
        var accountProvider = _serviceProvider.GetRequiredService<FakeAccountQuotaProvider>();
        var siteProvider = _serviceProvider.GetRequiredService<FakeSiteQuotaProvider>();
        var mixedSite = Guid.NewGuid();
        var siteKeyId = Guid.NewGuid();
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Sites.Add(new Domain.Sites.Site { Id = mixedSite, Name = "mixed", BaseUrl = "https://m.com", IsEnabled = true });
            db.SiteKeys.Add(new Domain.Sites.SiteKey
            {
                Id = siteKeyId, SiteId = mixedSite, KeyValue = "k", Priority = 0, IsEnabled = true,
                LastQuotaRawJson = siteProvider.BuildRawJson(resetInHours: 3)
            });
            await db.SaveChangesAsync();
        }
        // 账号表若也有同站点数据（不同值），必须被 Key 数据压制。
        accountProvider.Seed(mixedSite, resetInHours: 96);

        var policy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);
        var route = new CachedProxyRouteTarget
        {
            CircuitKey = Guid.NewGuid(),
            SiteId = mixedSite,
            SiteKeyId = siteKeyId,
            AccountIdentity = new(accountProvider.ProviderKey, mixedSite),
        };

        await policy.RecordRouteFailureAsync(route, 402, "insufficient quota",
            retryAfterSeconds: null, tieredCooldownEnabled: true,
            meta: new CircuitRouteMeta("q", "m"), CancellationToken.None);

        var rest = _circuitStore.GetRest(route.CircuitKey);
        rest!.Value.UntilUtc.Should().BeBefore(DateTimeOffset.UtcNow.AddHours(5), "Key 配额数据（+3h）优先，账号回退（+96h）不参与");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Key_without_quota_never_uses_account_or_sibling_quota(bool includeSiblingCandidate)
    {
        var siteId = Guid.NewGuid();
        var emptyKey = Guid.NewGuid();
        var siblingKey = Guid.NewGuid();
        var siteProvider = _serviceProvider.GetRequiredService<FakeSiteQuotaProvider>();
        _accountProvider.Seed(siteId, resetInHours: 1);
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Sites.Add(new Domain.Sites.Site { Id = siteId, Name = "mixed-keys", BaseUrl = "https://mixed.invalid", IsEnabled = true });
            db.SiteKeys.Add(new Domain.Sites.SiteKey { Id = emptyKey, SiteId = siteId, KeyValue = "empty", IsEnabled = true });
            db.SiteKeys.Add(new Domain.Sites.SiteKey
            {
                Id = siblingKey, SiteId = siteId, KeyValue = "sibling", IsEnabled = true,
                LastQuotaRawJson = siteProvider.BuildRawJson(resetInHours: 3),
            });
            await db.SaveChangesAsync();
        }

        var empty = new CachedProxyRouteTarget
        {
            CircuitKey = Guid.NewGuid(), SiteId = siteId, SiteKeyId = emptyKey,
            AccountIdentity = new(_accountProvider.ProviderKey, siteId),
        };
        var sibling = new CachedProxyRouteTarget { CircuitKey = Guid.NewGuid(), SiteId = siteId, SiteKeyId = siblingKey };
        var other = new CachedProxyRouteTarget { CircuitKey = Guid.NewGuid(), SiteId = Guid.NewGuid() };
        var policy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);

        var ordered = await policy.PreorderCandidatesAsync([empty, includeSiblingCandidate ? sibling : other], true, null, CancellationToken.None);
        ordered.Should().Equal(includeSiblingCandidate ? new[] { sibling, empty } : new[] { empty, other });
        _accountProvider.LookupCount.Should().Be(0, "带 Key 的候选不能使用账号表配额");

        // 冷却使用新实例，确保单项查询而非批量排序的负缓存。
        var freshPolicy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);
        await freshPolicy.RecordRouteFailureAsync(empty, 402, "insufficient quota", null, true,
            new CircuitRouteMeta("mixed", "m"), CancellationToken.None);
        var rest = _circuitStore.GetRest(empty.CircuitKey);
        rest.Should().NotBeNull();
        rest!.Value.UntilUtc.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(29));
        rest.Value.UntilUtc.Should().BeBefore(DateTimeOffset.UtcNow.AddMinutes(31));
        _accountProvider.LookupCount.Should().Be(0);
    }

    [Fact]
    public async Task Site_level_candidate_with_empty_key_does_not_use_account_fallback()
    {
        var siteId = Guid.NewGuid();
        _accountProvider.Seed(siteId, resetInHours: 1);
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Sites.Add(new Domain.Sites.Site { Id = siteId, Name = "empty-key", BaseUrl = "https://empty.invalid", IsEnabled = true });
            db.SiteKeys.Add(new Domain.Sites.SiteKey { Id = Guid.NewGuid(), SiteId = siteId, KeyValue = "empty", IsEnabled = true });
            await db.SaveChangesAsync();
        }

        var route = new CachedProxyRouteTarget
        {
            CircuitKey = Guid.NewGuid(), SiteId = siteId, AccountIdentity = new(_accountProvider.ProviderKey, siteId),
        };
        var other = new CachedProxyRouteTarget { CircuitKey = Guid.NewGuid(), SiteId = Guid.NewGuid() };
        var policy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);
        var ordered = await policy.PreorderCandidatesAsync([other, route], true, null, CancellationToken.None);
        ordered.Should().Equal(other, route);

        var freshPolicy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);
        await freshPolicy.RecordRouteFailureAsync(route, 402, "insufficient quota", null, true,
            new CircuitRouteMeta("empty-key", "m"), CancellationToken.None);
        var rest = _circuitStore.GetRest(route.CircuitKey);
        rest.Should().NotBeNull();
        rest!.Value.UntilUtc.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(29));
        rest.Value.UntilUtc.Should().BeBefore(DateTimeOffset.UtcNow.AddMinutes(31));
        _accountProvider.LookupCount.Should().Be(0);
    }

    [Theory]
    [InlineData(1, 48, true)]
    [InlineData(48, 1, false)]
    public async Task Mixed_candidates_use_key_data_and_account_fallback_independently(int keyResetHours, int oauthResetHours, bool keyExpectedFirst)
    {
        // 双向锁定 Key 数据与 OAuth 账号回退按最早重置排序。
        var accountProvider = _serviceProvider.GetRequiredService<FakeAccountQuotaProvider>();
        var siteProvider = _serviceProvider.GetRequiredService<FakeSiteQuotaProvider>();
        var oauthSite = Guid.NewGuid();
        var keySite = Guid.NewGuid();
        var keyId = Guid.NewGuid();
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Sites.Add(new Domain.Sites.Site { Id = keySite, Name = "key-site", BaseUrl = "https://k.com", IsEnabled = true });
            db.SiteKeys.Add(new Domain.Sites.SiteKey
            {
                Id = keyId, SiteId = keySite, KeyValue = "k", Priority = 0, IsEnabled = true,
                LastQuotaRawJson = siteProvider.BuildRawJson(resetInHours: keyResetHours)
            });
            await db.SaveChangesAsync();
        }
        accountProvider.Seed(oauthSite, resetInHours: oauthResetHours);

        var policy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);

        var keyCircuitKey = Guid.NewGuid();
        var oauthCircuitKey = Guid.NewGuid();
        var keyRoute = new CachedProxyRouteTarget { CircuitKey = keyCircuitKey, SiteId = keySite, SiteKeyId = keyId };
        var oauthRoute = new CachedProxyRouteTarget
        {
            CircuitKey = oauthCircuitKey,
            SiteId = oauthSite,
            AccountIdentity = new(accountProvider.ProviderKey, oauthSite),
        };
        var routes = keyExpectedFirst
            ? new List<CachedProxyRouteTarget> { oauthRoute, keyRoute }
            : new List<CachedProxyRouteTarget> { keyRoute, oauthRoute };

        var ordered = await policy.PreorderCandidatesAsync(routes, quotaOrderingEnabled: true, conversationKey: null, CancellationToken.None);

        ordered.Should().HaveCount(2);
        var keyIndex = keyExpectedFirst ? 0 : 1;
        var oauthIndex = keyExpectedFirst ? 1 : 0;
        ordered[keyIndex].CircuitKey.Should().Be(keyCircuitKey);
        ordered[keyIndex].SiteKeyId.Should().Be(keyId);
        ordered[keyIndex].SiteId.Should().Be(keySite);
        ordered[oauthIndex].CircuitKey.Should().Be(oauthCircuitKey);
        ordered[oauthIndex].SiteKeyId.Should().BeNull();
        ordered[oauthIndex].SiteId.Should().Be(oauthSite);
    }

    [Fact]
    public async Task Grouped_quota_ordering_sorts_pool_leaves_without_crossing_outer_siblings()
    {
        var provider = _serviceProvider.GetRequiredService<FakeSiteQuotaProvider>();
        var siteId = Guid.NewGuid();
        var group = Guid.NewGuid();
        var a = new CachedProxyRouteTarget { SiteId = siteId, SiteKeyId = Guid.NewGuid(), FallbackGroupId = Guid.NewGuid(), FallbackGroupOrder = 0 };
        var late = new CachedProxyRouteTarget { SiteId = siteId, SiteKeyId = Guid.NewGuid(), FallbackGroupId = group, FallbackGroupOrder = 1 };
        var soon = new CachedProxyRouteTarget { SiteId = siteId, SiteKeyId = Guid.NewGuid(), FallbackGroupId = group, FallbackGroupOrder = 1 };
        var e = new CachedProxyRouteTarget { SiteId = siteId, SiteKeyId = Guid.NewGuid(), FallbackGroupId = Guid.NewGuid(), FallbackGroupOrder = 2 };
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Sites.Add(new Domain.Sites.Site { Id = siteId, Name = "grouped-keys", BaseUrl = "https://isolated.invalid", IsEnabled = true });
            foreach (var (route, hours) in new[] { (a, 96), (late, 48), (soon, 2), (e, 1) })
                db.SiteKeys.Add(new Domain.Sites.SiteKey
                {
                    Id = route.SiteKeyId!.Value, SiteId = siteId, KeyValue = "test-only", IsEnabled = true,
                    LastQuotaRawJson = provider.BuildRawJson(hours)
                });
            await db.SaveChangesAsync();
        }
        var policy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);

        var ordered = await policy.PreorderCandidatesAsync([a, late, soon, e], true, null, CancellationToken.None);

        ordered.Should().Equal(new[] { a, soon, late, e }, "batch lookup must distinguish four keys on one site, while retaining A -> [pool] -> E");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Grouped_affinity_only_promotes_within_its_own_outer_group(bool pinLastOuterSibling)
    {
        var poolGroup = Guid.NewGuid();
        var a = new CachedProxyRouteTarget { CircuitKey = Guid.NewGuid(), FallbackGroupId = Guid.NewGuid(), FallbackGroupOrder = 0 };
        var first = new CachedProxyRouteTarget { CircuitKey = Guid.NewGuid(), FallbackGroupId = poolGroup, FallbackGroupOrder = 1 };
        var pinned = new CachedProxyRouteTarget { CircuitKey = Guid.NewGuid(), FallbackGroupId = poolGroup, FallbackGroupOrder = 1 };
        var e = new CachedProxyRouteTarget { CircuitKey = Guid.NewGuid(), FallbackGroupId = Guid.NewGuid(), FallbackGroupOrder = 2 };
        _sessionAffinity.RecordSuccess("h:grouped", pinLastOuterSibling ? e.CircuitKey : pinned.CircuitKey);
        var policy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);

        var ordered = await policy.PreorderCandidatesAsync([a, first, pinned, e], false, "h:grouped", CancellationToken.None);

        ordered.Should().Equal(pinLastOuterSibling ? new[] { a, first, pinned, e } : new[] { a, pinned, first, e });
    }

    [Fact]
    public async Task Grouped_affinity_wins_over_quota_only_inside_the_pinned_group()
    {
        var provider = _serviceProvider.GetRequiredService<FakeAccountQuotaProvider>();
        var poolGroup = Guid.NewGuid();
        var a = new CachedProxyRouteTarget
        {
            SiteId = Guid.NewGuid(), CircuitKey = Guid.NewGuid(), FallbackGroupId = Guid.NewGuid(), FallbackGroupOrder = 0,
            AccountIdentity = new(provider.ProviderKey, Guid.NewGuid()),
        };
        var soon = new CachedProxyRouteTarget
        {
            SiteId = Guid.NewGuid(), CircuitKey = Guid.NewGuid(), FallbackGroupId = poolGroup, FallbackGroupOrder = 1,
            AccountIdentity = new(provider.ProviderKey, Guid.NewGuid()),
        };
        var pinned = new CachedProxyRouteTarget
        {
            SiteId = Guid.NewGuid(), CircuitKey = Guid.NewGuid(), FallbackGroupId = poolGroup, FallbackGroupOrder = 1,
            AccountIdentity = new(provider.ProviderKey, Guid.NewGuid()),
        };
        var e = new CachedProxyRouteTarget
        {
            SiteId = Guid.NewGuid(), CircuitKey = Guid.NewGuid(), FallbackGroupId = Guid.NewGuid(), FallbackGroupOrder = 2,
            AccountIdentity = new(provider.ProviderKey, Guid.NewGuid()),
        };
        provider.Seed(a.SiteId, 96);
        provider.Seed(soon.SiteId, 2);
        provider.Seed(pinned.SiteId, 48);
        provider.Seed(e.SiteId, 1);
        _sessionAffinity.RecordSuccess("h:quota-affinity", pinned.CircuitKey);
        var policy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);

        var ordered = await policy.PreorderCandidatesAsync([a, soon, pinned, e], true, "h:quota-affinity", CancellationToken.None);

        ordered.Should().Equal(a, pinned, soon, e);
    }

    [Fact]
    public async Task Ordinary_sites_without_account_identity_skip_account_provider_lookups()
    {
        // 普通站点（有 SiteKey 但 LastQuotaRawJson 为空、无 AccountIdentity）
        // 在批量排序和 402 冷却中两个 fake provider 的 LookupCount 均为 0
        var siteId = Guid.NewGuid();
        var siteKeyId = Guid.NewGuid();
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Sites.Add(new Domain.Sites.Site { Id = siteId, Name = "ordinary", BaseUrl = "https://o.com", IsEnabled = true });
            db.SiteKeys.Add(new Domain.Sites.SiteKey
            {
                Id = siteKeyId, SiteId = siteId, KeyValue = "k", Priority = 0, IsEnabled = true,
                LastQuotaRawJson = null,
            });
            await db.SaveChangesAsync();
        }

        var policy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);

        var route1 = new CachedProxyRouteTarget { CircuitKey = Guid.NewGuid(), SiteId = siteId, SiteKeyId = siteKeyId };
        var route2 = new CachedProxyRouteTarget { CircuitKey = Guid.NewGuid(), SiteId = siteId, SiteKeyId = siteKeyId };

        // 批量排序
        await policy.PreorderCandidatesAsync([route1, route2], quotaOrderingEnabled: true, conversationKey: null, CancellationToken.None);

        _accountProvider.LookupCount.Should().Be(0, "普通站点无 AccountIdentity，批量排序不得查询账号表");
        _secondAccountProvider.LookupCount.Should().Be(0);

        // 402 冷却
        var freshRoute = new CachedProxyRouteTarget { CircuitKey = Guid.NewGuid(), SiteId = Guid.NewGuid(), SiteKeyId = Guid.NewGuid() };
        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Sites.Add(new Domain.Sites.Site { Id = freshRoute.SiteId, Name = "fresh", BaseUrl = "https://f.com", IsEnabled = true });
            db.SiteKeys.Add(new Domain.Sites.SiteKey
            {
                Id = freshRoute.SiteKeyId!.Value, SiteId = freshRoute.SiteId, KeyValue = "k", Priority = 0, IsEnabled = true,
                LastQuotaRawJson = null,
            });
            await db.SaveChangesAsync();
        }

        await policy.RecordRouteFailureAsync(freshRoute, 402, "insufficient quota",
            retryAfterSeconds: null, tieredCooldownEnabled: true,
            meta: new CircuitRouteMeta("f", "m"), CancellationToken.None);

        _accountProvider.LookupCount.Should().Be(0, "普通站点无 AccountIdentity，402 冷却不得查询账号表");
        _secondAccountProvider.LookupCount.Should().Be(0);

        var rest = _circuitStore.GetRest(freshRoute.CircuitKey);
        rest.Should().NotBeNull();
        rest!.Value.UntilUtc.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(29));
        rest.Value.UntilUtc.Should().BeBefore(DateTimeOffset.UtcNow.AddMinutes(31));
    }

    [Fact]
    public async Task Multiple_oauth_sites_query_owning_provider_once_with_deduplicated_sites()
    {
        // 两个 OAuth 站点批量时 owning provider 只调用 1 次且 LastSiteIds 包含两站点、另一 provider 为 0
        var site1 = Guid.NewGuid();
        var site2 = Guid.NewGuid();
        _accountProvider.Seed(site1, 5);
        _accountProvider.Seed(site2, 10);

        var policy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);

        var r1 = new CachedProxyRouteTarget
        {
            CircuitKey = Guid.NewGuid(), SiteId = site1,
            AccountIdentity = new(_accountProvider.ProviderKey, site1),
        };
        var r2 = new CachedProxyRouteTarget
        {
            CircuitKey = Guid.NewGuid(), SiteId = site2,
            AccountIdentity = new(_accountProvider.ProviderKey, site2),
        };

        var initialCount = _accountProvider.LookupCount;
        var initialSecond = _secondAccountProvider.LookupCount;

        await policy.PreorderCandidatesAsync([r1, r2], quotaOrderingEnabled: true, conversationKey: null, CancellationToken.None);

        (_accountProvider.LookupCount - initialCount).Should().Be(1, "同一 provider 批量只查一次");
        _accountProvider.LastSiteIds.Should().BeEquivalentTo(new[] { site1, site2 });
        (_secondAccountProvider.LookupCount - initialSecond).Should().Be(0, "非 owning provider 不得被查询");
    }

    [Fact]
    public async Task Failed_account_provider_does_not_block_other_providers()
    {
        var failed = new CachedProxyRouteTarget
        {
            SiteId = Guid.NewGuid(), AccountIdentity = new(_accountProvider.ProviderKey, Guid.NewGuid()),
        };
        var healthy = new CachedProxyRouteTarget
        {
            SiteId = Guid.NewGuid(), AccountIdentity = new(_secondAccountProvider.ProviderKey, Guid.NewGuid()),
        };
        _accountProvider.OnLookup = _ => throw new InvalidOperationException("lookup failed");
        _secondAccountProvider.Seed(healthy.SiteId, 2);
        var policy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);

        var ordered = await policy.PreorderCandidatesAsync([failed, healthy], true, null, CancellationToken.None);

        ordered.Should().Equal(healthy, failed);
        _secondAccountProvider.LookupCount.Should().Be(1);
    }

    [Fact]
    public async Task Malformed_account_cache_does_not_block_other_accounts_or_providers()
    {
        var bad = new CachedProxyRouteTarget
        {
            SiteId = Guid.NewGuid(), AccountIdentity = new(_accountProvider.ProviderKey, Guid.NewGuid()),
        };
        var healthy = new CachedProxyRouteTarget
        {
            SiteId = Guid.NewGuid(), AccountIdentity = new(_accountProvider.ProviderKey, Guid.NewGuid()),
        };
        var other = new CachedProxyRouteTarget
        {
            SiteId = Guid.NewGuid(), AccountIdentity = new(_secondAccountProvider.ProviderKey, Guid.NewGuid()),
        };
        _accountProvider.Seed(bad.SiteId, -1);
        _accountProvider.Seed(healthy.SiteId, 2);
        _accountProvider.OnParse = raw =>
        {
            if (raw.Contains(":-1}")) throw new FormatException("invalid cache");
        };
        _secondAccountProvider.Seed(other.SiteId, 4);
        var policy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);

        var ordered = await policy.PreorderCandidatesAsync([bad, other, healthy], true, null, CancellationToken.None);

        ordered.Should().Equal(healthy, other, bad);
        _secondAccountProvider.LookupCount.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_account_fallback_propagates_and_does_not_cache(bool singleRoute)
    {
        var siteId = Guid.NewGuid();
        _accountProvider.Seed(siteId, 5);
        var route = new CachedProxyRouteTarget
        {
            CircuitKey = Guid.NewGuid(), SiteId = siteId,
            AccountIdentity = new(_accountProvider.ProviderKey, siteId),
        };
        var policy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);
        using var cts = new CancellationTokenSource();
        _accountProvider.OnLookup = token =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
        };

        async Task Lookup(CancellationToken token)
        {
            if (singleRoute)
                await policy.RecordRouteFailureAsync(route, 402, "insufficient quota", null, true,
                    new CircuitRouteMeta("f", "m"), token);
            else
                await policy.PreorderCandidatesAsync([route, route], true, null, token);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Lookup(cts.Token));
        _circuitStore.GetRest(route.CircuitKey).Should().BeNull();
        _accountProvider.LookupCount.Should().Be(1);
        _accountProvider.OnLookup = null;

        await Lookup(CancellationToken.None);

        _accountProvider.LookupCount.Should().Be(2, "取消的回退查询不能写入负缓存");
        if (singleRoute)
            _circuitStore.GetRest(route.CircuitKey)!.Value.UntilUtc.Should().BeAfter(DateTimeOffset.UtcNow.AddHours(4));
    }

    [Fact]
    public async Task Cached_reset_miss_filters_out_cached_sites_from_subsequent_batch()
    {
        // 先让 A 进入 60 秒缓存后再批量 [A, B]，断言只查询 B，验证“实际缓存未命中”
        var siteA = Guid.NewGuid();
        var siteB = Guid.NewGuid();
        _accountProvider.Seed(siteA, 5);
        _accountProvider.Seed(siteB, 10);

        var policy = new RouteCandidatePolicyService(
            _serviceProvider, _circuitStore, _sessionAffinity, NullLogger<RouteCandidatePolicyService>.Instance);

        var routeA = new CachedProxyRouteTarget
        {
            CircuitKey = Guid.NewGuid(), SiteId = siteA,
            AccountIdentity = new(_accountProvider.ProviderKey, siteA),
        };
        var routeB = new CachedProxyRouteTarget
        {
            CircuitKey = Guid.NewGuid(), SiteId = siteB,
            AccountIdentity = new(_accountProvider.ProviderKey, siteB),
        };

        // 查询 A，进入 60 秒缓存
        await policy.PreorderCandidatesAsync([routeA, routeA], quotaOrderingEnabled: true, conversationKey: null, CancellationToken.None);
        var countAfterA = _accountProvider.LookupCount;

        // 再次批量 [A, B]，应只查询 B
        await policy.PreorderCandidatesAsync([routeA, routeB], quotaOrderingEnabled: true, conversationKey: null, CancellationToken.None);

        (_accountProvider.LookupCount - countAfterA).Should().Be(1);
        _accountProvider.LastSiteIds.Should().BeEquivalentTo(new[] { siteB }, "A 已在缓存中，批次回退只应反查未命中的 B");
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

    /// <summary>
    /// 账号表配额回退测试桩：模拟 IAccountQuotaProvider 的账号巡检缓存
    /// （LinkedSiteId → LastQuotaRawJson），验证策略层的回退管线。
    /// </summary>
    private sealed class FakeAccountQuotaProvider : IAccountQuotaProvider
    {
        public FakeAccountQuotaProvider(string providerKey = "fake-account")
        {
            ProviderKey = providerKey;
        }

        public string ProviderKey { get; }
        public int LookupCount { get; private set; }
        public Action<CancellationToken>? OnLookup { get; set; }
        public Action<string>? OnParse { get; set; }
        public IReadOnlyList<Guid>? LastSiteIds { get; private set; }
        private readonly Dictionary<Guid, int> _resetHoursBySite = [];

        public void Seed(Guid linkedSiteId, int resetInHours) => _resetHoursBySite[linkedSiteId] = resetInHours;

        public Task<IReadOnlyList<AccountQuotaTarget>> GetAccountsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AccountQuotaTarget>>(
                _resetHoursBySite.Select(pair => new AccountQuotaTarget
                {
                    ProviderKey = ProviderKey,
                    AccountId = pair.Key,
                    DisplayName = "fake",
                    LinkedSiteId = pair.Key,
                    IsEnabled = true,
                    LastQuotaRawJson = BuildRawJson(pair.Value),
                }).ToList());

        public Task<IReadOnlyList<AccountQuotaTarget>> FindByLinkedSiteIdsAsync(
            IReadOnlyList<Guid> siteIds,
            CancellationToken cancellationToken)
        {
            LookupCount++;
            LastSiteIds = siteIds.ToList();
            OnLookup?.Invoke(cancellationToken);
            return Task.FromResult<IReadOnlyList<AccountQuotaTarget>>(
                _resetHoursBySite.Where(pair => siteIds.Contains(pair.Key))
                    .Select(pair => new AccountQuotaTarget
                    {
                        ProviderKey = ProviderKey,
                        AccountId = pair.Key,
                        DisplayName = "fake",
                        LinkedSiteId = pair.Key,
                        IsEnabled = true,
                        LastQuotaRawJson = BuildRawJson(pair.Value),
                    }).ToList());
        }

        public AccountQuotaSnapshot? ParseCachedQuota(string rawJson)
        {
            OnParse?.Invoke(rawJson);
            if (rawJson is null || !rawJson.Contains("\"fakeAccountQuota\""))
            {
                return null;
            }

            var resetHours = int.Parse(rawJson.Split(':')[1].TrimEnd('}'));
            return new AccountQuotaSnapshot
            {
                Success = true,
                RawJson = rawJson,
                Windows =
                [
                    new AccountQuotaWindow
                    {
                        Id = "fake_window",
                        Label = "测试窗口",
                        UsedPercent = 50,
                        ResetAtUtc = DateTimeOffset.UtcNow.AddHours(resetHours),
                    }
                ],
            };
        }

        public Task<AccountQuotaSnapshot> QueryAsync(AccountQuotaTarget account, bool forceRefresh, CancellationToken cancellationToken)
            => Task.FromResult(new AccountQuotaSnapshot { Success = false, Error = "not used" });

        public Task SetEnabledAsync(AccountQuotaTarget account, bool enabled, string reason, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ApplyFeatureToggleAsync(bool enabled, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public string BuildRawJson(int resetInHours) => $"{{\"fakeAccountQuota\":{resetInHours}}}";
    }
}
