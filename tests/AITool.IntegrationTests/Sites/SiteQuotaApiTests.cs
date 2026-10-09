using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AITool.Application.Sites;
using AITool.Domain.Sites;
using AITool.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AITool.IntegrationTests.Sites;

/// <summary>
/// 站点套餐额度端点集成测试（GET /api/admin/sites/quota/overview + POST {id}/quota/refresh）。
/// <para>
/// 实时查询路径用假供应商（quota-test.invalid）替换真实智谱实现，避免测试发起真实网络请求；
/// 总览路径（纯缓存解析）直接使用真实智谱供应商的 ParseCached。
/// </para>
/// </summary>
public sealed class SiteQuotaApiTests
{
    private const string OverviewUrl = "/api/admin/sites/quota/overview";

    [Fact]
    public async Task Overview_Returns_Cached_Zhipu_Windows_And_Skips_Unsupported_Sites()
    {
        await using var factory = new SiteQuotaWebApplicationFactory();
        using var client = factory.CreateClient();

        var zhipuSiteId = factory.SeedZhipuSiteWithCache();
        factory.SeedPlainSite("OpenAI 官方", "https://api.openai.com");

        var response = await client.GetAsync(OverviewUrl);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var doc = JsonDocument.Parse(body);
        var sites = doc.RootElement.EnumerateArray().ToList();
        sites.Should().ContainSingle(s => s.GetProperty("siteId").GetString() == zhipuSiteId.ToString());
        var site = sites.Single(s => s.GetProperty("siteId").GetString() == zhipuSiteId.ToString());

        site.GetProperty("providerKey").GetString().Should().Be("zhipu");
        site.GetProperty("providerLabel").GetString().Should().Be("智谱 GLM");

        var keys = site.GetProperty("keys").EnumerateArray().ToList();
        keys.Should().HaveCount(1);
        var key = keys[0];
        key.GetProperty("keyValueMasked").GetString().Should().Be("sk-a***-key");
        key.GetProperty("status").GetString().Should().Be("ok");
        key.GetProperty("level").GetString().Should().Be("GLM Coding Plan (Lite)");
        // checkedAtUtc 只断言存在：测试工厂没有生产侧的 DateTimeOffset 本地化 AOP，
        // 读回值会带宿主时区 offset（已知的测试工厂不对称），瞬时值断言不稳定。
        key.GetProperty("checkedAtUtc").ValueKind.Should().Be(JsonValueKind.String);

        var windows = key.GetProperty("windows").EnumerateArray().ToList();
        windows.Should().HaveCount(2);
        windows[0].GetProperty("id").GetString().Should().Be("five_hour");
        windows[0].GetProperty("label").GetString().Should().Be("5 小时窗口");
        windows[0].GetProperty("usedPercent").GetDouble().Should().Be(1.0);
        windows[1].GetProperty("id").GetString().Should().Be("weekly_limit");
        windows[1].GetProperty("usedPercent").GetDouble().Should().Be(42.0);
    }

    [Fact]
    public async Task Overview_Empty_When_No_Quota_Capable_Site()
    {
        await using var factory = new SiteQuotaWebApplicationFactory();
        using var client = factory.CreateClient();

        factory.SeedPlainSite("OpenAI 官方", "https://api.openai.com");

        var response = await client.GetAsync(OverviewUrl);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Refresh_Queries_All_Keys_And_Persists_Cache()
    {
        await using var factory = new SiteQuotaWebApplicationFactory(useFakeProvider: true);
        using var client = factory.CreateClient();

        var siteId = factory.SeedPlainSite("测试套餐站", $"https://{FakeSiteQuotaProvider.Host}/v1");
        factory.SeedKeys(siteId,
            ("sk-good-key-0001", "主号", 0, true),
            ("bad-key", "失效号", 1, false));

        var refreshRes = await client.PostAsJsonAsync($"/api/admin/sites/{siteId}/quota/refresh", (object?)null);
        var refreshBody = await refreshRes.Content.ReadAsStringAsync();
        refreshRes.StatusCode.Should().Be(HttpStatusCode.OK, refreshBody);

        using var doc = JsonDocument.Parse(refreshBody);
        var root = doc.RootElement;
        root.GetProperty("providerKey").GetString().Should().Be("fake");
        var keys = root.GetProperty("keys").EnumerateArray().ToList();
        keys.Should().HaveCount(2);

        keys[0].GetProperty("keyValueMasked").GetString().Should().Be("sk-g***0001");
        keys[0].GetProperty("status").GetString().Should().Be("ok");
        keys[0].GetProperty("level").GetString().Should().Be("Fake Coding Pro");
        keys[0].GetProperty("windows").GetArrayLength().Should().Be(2);
        keys[0].GetProperty("balances").GetArrayLength().Should().Be(1);
        keys[0].GetProperty("balances")[0].GetProperty("currency").GetString().Should().Be("CNY");
        keys[0].GetProperty("balances")[0].GetProperty("totalBalance").GetDecimal().Should().Be(110.00m);

        // 坏密钥：状态 invalid_credential（凭据失效）+ 错误文案在响应中可见。
        keys[1].GetProperty("status").GetString().Should().Be("invalid_credential");
        keys[1].GetProperty("error").GetString().Should().Contain("认证失败");

        // 刷新结果已落库：总览（纯缓存路径）无需再查上游即可还原窗口。
        var overviewRes = await client.GetAsync(OverviewUrl);
        overviewRes.StatusCode.Should().Be(HttpStatusCode.OK);
        using var overview = JsonDocument.Parse(await overviewRes.Content.ReadAsStringAsync());
        var persisted = overview.RootElement.EnumerateArray()
            .Single(s => s.GetProperty("siteId").GetString() == siteId.ToString())
            .GetProperty("keys").EnumerateArray().ToList();
        persisted[0].GetProperty("status").GetString().Should().Be("ok");
        persisted[0].GetProperty("windows").GetArrayLength().Should().Be(2);
        persisted[1].GetProperty("status").GetString().Should().Be("invalid_credential");
    }

    [Fact]
    public async Task Refresh_Unknown_Site_Returns_404()
    {
        await using var factory = new SiteQuotaWebApplicationFactory(useFakeProvider: true);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync($"/api/admin/sites/{Guid.NewGuid()}/quota/refresh", (object?)null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Refresh_Unsupported_Site_Returns_404()
    {
        await using var factory = new SiteQuotaWebApplicationFactory(useFakeProvider: true);
        using var client = factory.CreateClient();

        var siteId = factory.SeedPlainSite("OpenAI 官方", "https://api.openai.com");

        var response = await client.PostAsJsonAsync($"/api/admin/sites/{siteId}/quota/refresh", (object?)null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}

/// <summary>额度端点测试宿主：临时库 + 可选的假供应商替换（避免真实网络请求）。</summary>
internal sealed class SiteQuotaWebApplicationFactory : WebApplicationFactory<Program>
{
    // 智谱真实响应形态（cc-switch issue #3036：周桶先重置，靠 unit 字段区分两桶）。
    private const string ZhipuCachedJson = """
        {
          "success": true,
          "data": {
            "level": "GLM Coding Plan (Lite)",
            "limits": [
              { "type": "TOKENS_LIMIT", "unit": 6, "number": 7, "percentage": 42.0, "nextResetTime": 1000003600000 },
              { "type": "TOKENS_LIMIT", "unit": 3, "number": 5, "percentage": 1.0,  "nextResetTime": 1000018000000 }
            ]
          }
        }
        """;

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"site-quota-test-{Guid.NewGuid():N}.db");
    private readonly bool _useFakeProvider;

    public SiteQuotaWebApplicationFactory(bool useFakeProvider = false)
    {
        _useFakeProvider = useFakeProvider;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            if (_useFakeProvider)
            {
                // 移除真实供应商（智谱），注入假供应商：刷新路径不发起真实网络请求。
                var providerDescriptors = services
                    .Where(d => d.ServiceType == typeof(ISiteQuotaProvider))
                    .ToList();
                foreach (var descriptor in providerDescriptors)
                {
                    services.Remove(descriptor);
                }

                services.AddSingleton<ISiteQuotaProvider>(new FakeSiteQuotaProvider());
            }

            IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);
        });
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        Seed();
    }

    private void Seed()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SqlSugarSetup.InitializeDatabase(db.Client);
    }

    /// <summary>建一个普通站点（无 SiteKey），返回站点 Id。</summary>
    public Guid SeedPlainSite(string name, string baseUrl)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var site = new Site { Id = Guid.NewGuid(), Name = name, BaseUrl = baseUrl, ApiKey = "unused" };
        db.Client.Insertable(site).ExecuteCommand();
        return site.Id;
    }

    /// <summary>建智谱站点并预置一份缓存额度数据（LastQuota* 列）。</summary>
    public Guid SeedZhipuSiteWithCache()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var site = new Site
        {
            Id = Guid.NewGuid(),
            Name = "智谱 GLM",
            BaseUrl = "https://open.bigmodel.cn/api/coding/paas/v4",
            ApiKey = "unused",
        };
        db.Client.Insertable(site).ExecuteCommand();
        db.Client.Insertable(new SiteKey
        {
            Id = Guid.NewGuid(),
            SiteId = site.Id,
            KeyValue = "sk-abcdef1234567890-key",
            Remark = "主号",
            Priority = 0,
            IsEnabled = true,
            LastQuotaRawJson = ZhipuCachedJson,
            LastQuotaCheckedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            LastQuotaStatus = "ok",
            LastQuotaError = null,
        }).ExecuteCommand();
        return site.Id;
    }

    /// <summary>给站点追加多个密钥（值、备注、优先级、启用）。</summary>
    public void SeedKeys(Guid siteId, params (string KeyValue, string Remark, int Priority, bool Enabled)[] keys)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var (keyValue, remark, priority, enabled) in keys)
        {
            db.Client.Insertable(new SiteKey
            {
                Id = Guid.NewGuid(),
                SiteId = siteId,
                KeyValue = keyValue,
                Remark = remark,
                Priority = priority,
                IsEnabled = enabled,
            }).ExecuteCommand();
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (File.Exists(_databasePath))
        {
            try { File.Delete(_databasePath); } catch { }
        }
    }
}

/// <summary>
/// 假站点额度供应商：匹配 quota-test.invalid；密钥值为 bad-key 时返回凭据失效，
/// 其余返回窗口 + 余额混合结果。RawJson 与 ParseCached 对称，验证落库回读链路。
/// </summary>
internal sealed class FakeSiteQuotaProvider : ISiteQuotaProvider
{
    public const string Host = "quota-test.invalid";

    private const string GoodRawJson = """{ "fake": "quota", "seed": 1 }""";

    public string ProviderKey => "fake";

    public string ProviderLabel => "测试供应商";

    public bool MatchesBaseUrl(string baseUrl)
        => Uri.TryCreate((baseUrl ?? string.Empty).Trim(), UriKind.Absolute, out var uri)
            && (uri.Host == Host || uri.Host.EndsWith("." + Host, StringComparison.OrdinalIgnoreCase));

    public Task<SiteQuotaQueryResult> QueryAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        if (string.Equals(apiKey, "bad-key", StringComparison.Ordinal))
        {
            return Task.FromResult(new SiteQuotaQueryResult
            {
                Success = false,
                Error = "认证失败（HTTP 401），请检查密钥",
                CredentialInvalid = true,
            });
        }

        return Task.FromResult(new SiteQuotaQueryResult
        {
            Success = true,
            Level = "Fake Coding Pro",
            RawJson = GoodRawJson,
            Windows =
            [
                new SiteQuotaWindow("five_hour", "5 小时窗口", 30, null, null),
                new SiteQuotaWindow("weekly_limit", "每周额度", 60, null, null),
            ],
            Balances =
            [
                new SiteQuotaBalanceInfo("CNY", 110.00m, 10.00m, 100.00m),
            ],
        });
    }

    public SiteQuotaQueryResult? ParseCached(string rawJson)
    {
        if (!string.Equals(rawJson, GoodRawJson, StringComparison.Ordinal))
        {
            return null;
        }

        return new SiteQuotaQueryResult
        {
            Success = true,
            Level = "Fake Coding Pro",
            RawJson = rawJson,
            Windows =
            [
                new SiteQuotaWindow("five_hour", "5 小时窗口", 30, null, null),
                new SiteQuotaWindow("weekly_limit", "每周额度", 60, null, null),
            ],
            Balances =
            [
                new SiteQuotaBalanceInfo("CNY", 110.00m, 10.00m, 100.00m),
            ],
        };
    }
}
