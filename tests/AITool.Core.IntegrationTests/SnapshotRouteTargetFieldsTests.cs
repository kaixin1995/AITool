using AITool.Application.CoreRuntime;
using AITool.Infrastructure.Proxy;
using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Core.IntegrationTests;

/// <summary>
/// 验证 Core 快照路径的路由/兜底/聊天目标携带 master 新字段
/// （ManagedSource/ClientEmulation/ExtraHeaders 三层合并/EgressProxyUrl/GoogleProjectId/SupportsResponses）。
/// 这些字段缺失会导致：Core 上托管站点 401 即刷永不触发、仿真头/出口代理/Gemini project 失效。
/// </summary>
public sealed class SnapshotRouteTargetFieldsTests
{
    private static CoreRuntimeConfigSnapshot BuildSnapshot()
    {
        var siteId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var modelId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var mappingId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var accountId = Guid.Parse("44444444-4444-4444-4444-444444444444");

        return new CoreRuntimeConfigSnapshot
        {
            ConfigVersion = 1,
            // 本测试验证代理池字段的快照贯通：显式开启出口网络代理开关（新开关默认关闭）。
            RuntimeSettings = new CoreRuntimeSettings { DeveloperProxyProfilesEnabled = true },
            Sites =
            [
                new CoreRuntimeSite
                {
                    Id = siteId,
                    Name = "Antigravity 托管站",
                    BaseUrl = "https://daily-cloudcode-pa.googleapis.com",
                    EndpointPathMode = "standard-root",
                    ApiKey = "ya29-access-token",
                    ProtocolType = "Gemini",
                    SupportsOpenAi = false,
                    SupportsAnthropic = false,
                    SupportsResponses = false,
                    IsEnabled = true,
                    ManagedSource = "Google",
                    ClientEmulation = "None",
                    ExtraHeadersJson = "{\"X-Site-Level\":\"site\"}"
                }
            ],
            SiteKeys = [],
            Models =
            [
                new CoreRuntimeModel
                {
                    Id = modelId,
                    ModelName = "gemini-3-pro",
                    IsEnabled = true,
                    ClientEmulation = "Antigravity",
                    ExtraHeadersJson = "{\"X-Model-Level\":\"model\"}"
                }
            ],
            SiteModelMappings =
            [
                new CoreRuntimeSiteModelMapping
                {
                    Id = mappingId,
                    SiteId = siteId,
                    ModelLibraryItemId = modelId,
                    RemoteModelName = "gemini-3-pro-site",
                    IsEnabled = true,
                    MaxConcurrency = 0,
                    ClientEmulation = "None",
                    ExtraHeadersJson = "{\"X-Mapping-Level\":\"mapping\"}",
                    EgressProxyUrl = "egress-pool-a"
                }
            ],
            RouteRules =
            [
                new CoreRuntimeRouteRule
                {
                    Id = Guid.Parse("55555555-5555-5555-5555-555555555555"),
                    ExternalModelName = "gemini-3-pro",
                    UpstreamModelName = "gemini-3-pro",
                    SiteId = siteId,
                    SiteModelName = "gemini-3-pro-site",
                    IsEnabled = true,
                    Priority = 0
                }
            ],
            AccessKeys = [],
            AccountCredentials =
            [
                new CoreRuntimeAccountCredential
                {
                    Provider = "Google",
                    AccountId = accountId,
                    LinkedSiteId = siteId,
                    RefreshToken = "google-refresh-token",
                    ProjectId = "my-gcp-project-123",
                    AccountKind = "Antigravity",
                    IsEnabled = true
                }
            ],
            HeaderProfiles =
            [
                new CoreRuntimeHeaderProfile { Key = "Antigravity", HeadersJson = "{\"User-Agent\":\"antigravity/cli/1.1.20\"}", IsEnabled = true }
            ],
            ProxyProfiles =
            [
                new CoreRuntimeProxyProfile { Key = "egress-pool-a", ProxyUrl = "socks5://127.0.0.1:10808", IsEnabled = true }
            ]
        };
    }

    private static (ProxyRequestMetadataCache Cache, ServiceProvider Sp) CreateCacheWithSnapshot(CoreRuntimeConfigSnapshot snapshot)
    {
        var services = new ServiceCollection();
        services.AddMemoryCache();
        var provider = new StubConfigProvider(snapshot);
        services.AddSingleton<ICoreRuntimeConfigProvider>(provider);
        var sp = services.BuildServiceProvider();
        var cache = new ProxyRequestMetadataCache(
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ICoreRuntimeConfigProvider>());
        return (cache, sp);
    }

    [Fact]
    public async Task Route_targets_carry_master_fields_from_snapshot()
    {
        var (cache, sp) = CreateCacheWithSnapshot(BuildSnapshot());
        using (sp)
        {
            var targets = await cache.GetRouteTargetsForModelAsync("OpenAI", "gemini-3-pro", CancellationToken.None);

            targets.Should().ContainSingle();
            var target = targets[0];
            // 托管源：401/403 即刷回调按此分流，缺失则 Core 上 Google 托管刷新永不触发。
            target.ManagedSource.Should().Be("Google");
            // 三层仿真（映射 None → 模型 Antigravity 生效）+ 头部逐层覆盖（mapping > model > site）。
            target.ClientEmulation.Should().Be("Antigravity");
            target.ExtraHeaders.Should().ContainKey("X-Mapping-Level").WhoseValue.Should().Be("mapping");
            target.ExtraHeaders.Should().ContainKey("X-Model-Level").WhoseValue.Should().Be("model");
            target.ExtraHeaders.Should().ContainKey("X-Site-Level").WhoseValue.Should().Be("site");
            // 出口代理档案 Key 解析为实际代理地址。
            target.EgressProxyUrl.Should().Be("socks5://127.0.0.1:10808");
            // Google 项目标识（Gemini 封套 project 字段）。
            target.GoogleProjectId.Should().Be("my-gcp-project-123");
            // 协议能力：Gemini 站点推导出的 Responses 支持口径与 DB 路径一致。
            target.SupportsResponses.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Fallback_targets_carry_master_fields_from_snapshot()
    {
        var (cache, sp) = CreateCacheWithSnapshot(BuildSnapshot());
        using (sp)
        {
            var model = await cache.GetEnabledModelAsync(Guid.Parse("22222222-2222-2222-2222-222222222222"), CancellationToken.None);
            model.Should().NotBeNull();
            var fallback = await cache.GetFallbackTargetAsync(model!.ModelId, CancellationToken.None);

            fallback.Should().NotBeNull();
            fallback!.ManagedSource.Should().Be("Google");
            fallback.ClientEmulation.Should().Be("Antigravity");
            fallback.EgressProxyUrl.Should().Be("socks5://127.0.0.1:10808");
            fallback.GoogleProjectId.Should().Be("my-gcp-project-123");
        }
    }

    [Fact]
    public async Task Chat_targets_carry_master_fields_from_snapshot()
    {
        var (cache, sp) = CreateCacheWithSnapshot(BuildSnapshot());
        using (sp)
        {
            var targets = await cache.GetChatTargetsAsync(CancellationToken.None);

            targets.Should().ContainSingle();
            var target = targets[0];
            target.ManagedSource.Should().Be("Google");
            target.ClientEmulation.Should().Be("Antigravity");
            target.GoogleProjectId.Should().Be("my-gcp-project-123");
            target.ExtraHeaders.Should().ContainKey("X-Mapping-Level").WhoseValue.Should().Be("mapping");
        }
    }

    private sealed class StubConfigProvider(CoreRuntimeConfigSnapshot snapshot) : ICoreRuntimeConfigProvider
    {
        public CoreRuntimeConfigSnapshot? GetCurrent() => snapshot;
        public void SetCurrent(CoreRuntimeConfigSnapshot value) { }
        public bool IsReady => true;
        public Task<bool> TryLoadFromFileAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
