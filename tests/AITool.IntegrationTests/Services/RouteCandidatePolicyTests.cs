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
        services.AddSingleton<FakeAccountQuotaProvider>();
        services.AddSingleton<IAccountQuotaProvider>(sp => sp.GetRequiredService<FakeAccountQuotaProvider>());
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
            new() { CircuitKey = lateKey, SiteId = lateSite },
            new() { CircuitKey = soonKey, SiteId = soonSite },
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
        var route = new CachedProxyRouteTarget { CircuitKey = Guid.NewGuid(), SiteId = siteId };

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
        var route = new CachedProxyRouteTarget { CircuitKey = Guid.NewGuid(), SiteId = mixedSite, SiteKeyId = siteKeyId };

        await policy.RecordRouteFailureAsync(route, 402, "insufficient quota",
            retryAfterSeconds: null, tieredCooldownEnabled: true,
            meta: new CircuitRouteMeta("q", "m"), CancellationToken.None);

        var rest = _circuitStore.GetRest(route.CircuitKey);
        rest!.Value.UntilUtc.Should().BeBefore(DateTimeOffset.UtcNow.AddHours(5), "Key 配额数据（+3h）优先，账号回退（+96h）不参与");
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
        public string ProviderKey => "fake-account";
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
            => Task.FromResult<IReadOnlyList<AccountQuotaTarget>>(
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

        public AccountQuotaSnapshot? ParseCachedQuota(string rawJson)
        {
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
