using System.Text;
using AITool.Application.Codex;
using AITool.Application.CoreRuntime;
using AITool.Application.Google;
using AITool.Application.Kimi;
using AITool.Application.Proxy;
using AITool.Core.Services;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.DependencyInjection;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AITool.Admin.IntegrationTests.Developer;

/// <summary>
/// 诊断抓包端到端（master 的 ProxyDiagnosticServiceTests 两个 E2E 用例的 split 双宿主版）：
/// 同一测试宿主挂载 Admin 管理端 + Core 代理控制器，验证 /v1 失败请求自动落盘复现文件、
/// 成功采样对比样本，以及开发者功能关闭时不落盘。
/// </summary>
public sealed class ProxyDiagnosticEndToEndTests
{
    [Fact]
    public async Task EndToEnd_failing_and_successful_proxy_requests_automatically_record_diagnostics_and_samples()
    {
        var fakeForwardService = new FakeProxyForwardServiceForDiagnostics { ReturnSuccess = false };
        await using var factory = new DiagnosticDualHostFactory(fakeForwardService, developerFeaturesEnabled: true);
        using var client = factory.CreateClient();

        var diagnosticService = factory.Services.GetRequiredService<IProxyDiagnosticService>();

        diagnosticService.ClearAllDumps();

        try
        {
            // 1. 失败请求：即使采样关闭也自动抓盘
            var failRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = new StringContent("{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"will fail\"}]}", Encoding.UTF8, "application/json")
            };
            failRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "openai-cross-key");

            var failResponse = await client.SendAsync(failRequest);
            ((int)failResponse.StatusCode).Should().BeOneOf(new[] { 400, 502 }, "失败转发映射为客户端错误/网关错误");

            var failItem = await WaitForDumpAsync(diagnosticService, x => x.Category == "failure" && x.RouteName == "auto", TimeSpan.FromSeconds(5));
            failItem.Should().NotBeNull("失败请求应自动落盘复现文件");
            failItem!.SiteName.Should().Be("Anthropic Only Site");
            failItem.StatusCode.Should().Be(400);
            failItem.Success.Should().BeFalse();

            // 2. 开启成功采样后：成功请求产生对比样本
            diagnosticService.EnableSuccessSampling(10);
            fakeForwardService.ReturnSuccess = true;

            var okRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            {
                Content = new StringContent("{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"will succeed\"}]}", Encoding.UTF8, "application/json")
            };
            okRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "openai-cross-key");

            var okResponse = await client.SendAsync(okRequest);
            okResponse.IsSuccessStatusCode.Should().BeTrue("成功转发应返回 2xx");

            var successItem = await WaitForDumpAsync(diagnosticService, x => x.Category == "sample" && x.RouteName == "auto", TimeSpan.FromSeconds(5));
            successItem.Should().NotBeNull("采样开启后成功请求应产生对比样本");
            successItem!.Success.Should().BeTrue();
        }
        finally
        {
            diagnosticService.ClearAllDumps();
        }
    }

    [Fact]
    public async Task EndToEnd_failure_dumps_are_skipped_when_developer_features_disabled()
    {
        // 开发者功能关闭时（与查看转储的管理端点门控对齐），失败请求不落盘、不进内存清单。
        var fakeForwardService = new FakeProxyForwardServiceForDiagnostics { ReturnSuccess = false };
        await using var factory = new DiagnosticDualHostFactory(fakeForwardService, developerFeaturesEnabled: false);
        using var client = factory.CreateClient();

        var diagnosticService = factory.Services.GetRequiredService<IProxyDiagnosticService>();
        diagnosticService.ClearAllDumps();

        var failRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("{\"model\":\"auto\",\"messages\":[{\"role\":\"user\",\"content\":\"dev off\"}]}", Encoding.UTF8, "application/json")
        };
        failRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "openai-cross-key");

        await client.SendAsync(failRequest);

        // 轮询一小段时间确认没有异步落盘（避免"尚未写完"的假阴性）。
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            diagnosticService.ListRecentDumps(50).Should().BeEmpty("开发者功能关闭时不应落盘");
            await Task.Delay(100);
        }
    }

    private static async Task<ProxyDiagnosticDumpItem?> WaitForDumpAsync(
        IProxyDiagnosticService service,
        Func<ProxyDiagnosticDumpItem, bool> predicate,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var match = service.ListRecentDumps(50).FirstOrDefault(predicate);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(100);
        }

        return null;
    }

    private sealed class FakeProxyForwardServiceForDiagnostics : IProxyForwardService
    {
        public bool ReturnSuccess { get; set; } = true;

        public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
        {
            if (ReturnSuccess)
            {
                return Task.FromResult(new ProxyForwardResult
                {
                    Success = true,
                    StatusCode = 200,
                    ResponseBody = "{\"content\":[{\"type\":\"text\",\"text\":\"success-content\"}],\"usage\":{\"input_tokens\":5,\"cache_read_input_tokens\":0,\"output_tokens\":5}}",
                    InputTokens = 5,
                    OutputTokens = 5
                });
            }

            return Task.FromResult(new ProxyForwardResult
            {
                Success = false,
                StatusCode = 400,
                ErrorMessage = "Mock invalid parameter in upstream model",
                ResponseBody = "{\"error\":{\"message\":\"Mock invalid parameter in upstream model\"}}"
            });
        }

        public Task<ProxyForwardResult> ForwardStreamingAsync(ProxyForwardRequest request, Func<string, CancellationToken, Task> onSseDataAsync, CancellationToken cancellationToken = default)
            => ForwardAsync(request, cancellationToken);

        public Task<ProxyForwardResult> ForwardStreamingWithStateAsync(ProxyForwardRequest request, Func<string, CancellationToken, Task> onSseDataAsync, Func<ProxyForwardResult, Task>? onStreamStartedAsync = null, CancellationToken cancellationToken = default)
            => ForwardAsync(request, cancellationToken);
    }

    /// <summary>
    /// 双宿主测试工厂：Admin 宿主 + Core 代理控制器（/v1）同进程挂载，
    /// 补注册 Core 控制器所需的凭证刷新门面与事件发布器。
    /// </summary>
    private sealed class DiagnosticDualHostFactory : WebApplicationFactory<AITool.Admin.AdminProgramMarker>
    {
        private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-diag-{Guid.NewGuid():N}.db");
        private readonly IProxyForwardService _fakeForwardService;
        private readonly bool _developerFeaturesEnabled;

        public DiagnosticDualHostFactory(IProxyForwardService fakeForwardService, bool developerFeaturesEnabled)
        {
            _fakeForwardService = fakeForwardService;
            _developerFeaturesEnabled = developerFeaturesEnabled;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);

                // Core 控制器依赖的代理运行时基础设施（事件 spool、批量写入、元数据缓存走 DB 路径）。
                var spoolPath = Path.Combine(Path.GetTempPath(), $"aitool-diag-spool-{Guid.NewGuid():N}");
                services.AddProxyRuntimeInfrastructure(
                    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ProxyForwarding:RequestTimeoutSeconds"] = "30",
                        ["ProxyForwarding:RetryCount"] = "0"
                    }).Build().GetSection("ProxyForwarding"),
                    spoolPath,
                    useCoreRuntimeConfigProviderForCache: false);

                services.AddSingleton(new CoreRuntimeConfigFileOptions
                {
                    FilePath = Path.Combine(Path.GetTempPath(), $"aitool-diag-config-{Guid.NewGuid():N}.json")
                });
                services.AddSingleton<CoreRuntimeConfigProvider>();
                services.AddSingleton<ICoreRuntimeConfigProvider>(sp => sp.GetRequiredService<CoreRuntimeConfigProvider>());

                services.RemoveAll<ProxyRequestMetadataCache>();
                services.AddSingleton<ProxyRequestMetadataCache>(sp =>
                {
                    var memoryCache = sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
                    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
                    return new ProxyRequestMetadataCache(memoryCache, scopeFactory);
                });

                // Core 代理控制器构造所需的凭证刷新门面与路由回退事件发布器（Core Program 中注册、此处补齐）。
                services.AddHttpClient<ICodexOAuthClient, AITool.Infrastructure.Codex.CodexOAuthClient>();
                services.AddHttpClient<IGoogleOAuthClient, AITool.Infrastructure.Google.GoogleOAuthClient>();
                services.AddHttpClient<IKimiOAuthClient, AITool.Infrastructure.Kimi.KimiOAuthClient>();
                services.AddScoped<CoreCredentialRefreshEngine>();
                services.AddScoped<CodexCredentialRefreshService>();
                services.AddScoped<GoogleCredentialRefreshService>();
                services.AddScoped<KimiCredentialRefreshService>();
                services.AddSingleton<CoreRouteFallbackEventPublisher>();
                services.AddSingleton<ModelConcurrencyQueryService>();

                // 替换转发服务为可控桩。
                services.RemoveAll<IProxyForwardService>();
                services.AddSingleton<IProxyForwardService>(_fakeForwardService);

                // 挂载 Core 程序集控制器（/v1 代理端点）。
                services.AddControllers()
                    .AddApplicationPart(typeof(AITool.Core.Controllers.Core.CoreConfigSyncController).Assembly);
            });
        }

        protected override void ConfigureClient(HttpClient client)
        {
            base.ConfigureClient(client);
            SeedAsync().GetAwaiter().GetResult();
        }

        private async Task SeedAsync()
        {
            await IntegrationTestDbHelper.InitializeDatabaseAsync(Services);
            await using var scope = Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var siteId = Guid.Parse("12121212-1212-1212-1212-121212121212");
            var accessKeyRaw = "openai-cross-key";
            var accessKeyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(accessKeyRaw)));

            db.Sites.Add(new AITool.Domain.Sites.Site
            {
                Id = siteId,
                Name = "Anthropic Only Site",
                BaseUrl = "https://anthropic-only.example.com",
                ApiKey = "anthropic-only-key",
                ProtocolType = "Anthropic",
                SupportsAnthropic = true,
                IsEnabled = true
            });

            db.ProxyAccessKeys.Add(new AITool.Domain.Proxy.ProxyAccessKey
            {
                Id = Guid.Parse("34343434-3434-3434-3434-343434343434"),
                KeyName = "openai-cross",
                PlainKey = accessKeyRaw,
                AccessKeyHash = accessKeyHash,
                MaskedValue = "sk-***cross",
                IsEnabled = true
            });

            db.ProxyRouteRules.Add(new AITool.Domain.Proxy.ProxyRouteRule
            {
                Id = Guid.Parse("56565656-5656-5656-5656-565656565656"),
                ExternalModelName = "auto",
                UpstreamModelName = "claude-3-7-sonnet",
                SiteId = siteId,
                SiteModelName = "claude-3-7-sonnet-real",
                IsEnabled = true
            });

            // 设置单例：开发者功能开关控制失败抓盘是否落盘。
            // 用 SqlSugar 原生写入（EF-compat Add+SaveChangesAsync 在测试宿主可能不生效）。
            var settingsList = await db.SystemRuntimeSettings.Where(x => x.Id == 1).ToListAsync();
            var settings = settingsList.FirstOrDefault();
            if (settings is null)
            {
                settings = new AITool.Domain.Operations.SystemRuntimeSettings
                {
                    Id = 1,
                    ProxyRequestTimeoutSeconds = 30,
                    ProxyRetryCount = 0,
                    DeveloperFeaturesEnabled = _developerFeaturesEnabled
                };
                await db.InsertAsync(settings);
            }
            else
            {
                settings.ProxyRequestTimeoutSeconds = 30;
                settings.ProxyRetryCount = 0;
                settings.DeveloperFeaturesEnabled = _developerFeaturesEnabled;
                await db.UpdateAsync(settings);
            }

            // 双宿主工厂：后台服务可能在种子前已把默认运行时设置永久缓存（NeverRemove），
            // 种子后显式失效，否则 DeveloperFeaturesEnabled 等开关读到旧值。
            var seedCache = scope.ServiceProvider.GetRequiredService<ProxyRequestMetadataCache>();
            seedCache.InvalidateRuntimeSettings();
            seedCache.InvalidateRouteTargets();
            seedCache.InvalidateAccessKeys();
        }
    }
}
