using System.Security.Cryptography;
using System.Text;
using AITool.Application.Proxy;
using AITool.Domain.Proxy;
using AITool.Domain.Sites;
using AITool.Domain.Models;
using AITool.Domain.SiteCatalog;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AITool.Infrastructure.Proxy;
// split: Core 侧 ProxyFallbackFlowTests 的共享工厂副本（宿主标记换 Admin，供 Admin 侧队列编辑测试复用）。
namespace AITool.Admin.IntegrationTests.Proxy;

internal sealed class ProxyFallbackWebApplicationFactory : WebApplicationFactory<AITool.AllInOne.AllInOneProgramMarker>
{
    /// <summary>
    /// 保存当前测试使用的临时数据库文件路径。
    /// </summary>
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"aitool-proxy-fallback-{Guid.NewGuid():N}.db");
    /// <summary>
    /// 保存当前测试使用的伪造转发服务。
    /// </summary>
    private readonly FakeProxyForwardService _fakeForwardService;

    /// <summary>
    /// 初始化代理回退测试宿主。
    /// </summary>
    public ProxyFallbackWebApplicationFactory()
        : this(new FakeProxyForwardService())
    {
    }

    /// <summary>
    /// 初始化代理回退测试宿主。
    /// </summary>
    public ProxyFallbackWebApplicationFactory(FakeProxyForwardService fakeForwardService)
    {
        _fakeForwardService = fakeForwardService;
    }

    /// <summary>
    /// 重写测试宿主依赖，接入隔离数据库和伪造转发服务。
    /// </summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            IntegrationTestDbHelper.ReplaceWithSqlSugar(services, _databasePath);
                // split 双宿主：Core 宿主默认快照模式；测试用 DB 支撑提供器让 master 的 DB 种子直通。
                services.RemoveAll<AITool.Application.CoreRuntime.ICoreRuntimeConfigProvider>();
                services.AddSingleton<AITool.Application.CoreRuntime.ICoreRuntimeConfigProvider, AITool.Infrastructure.CoreRuntime.DbBackedRuntimeConfigProvider>();
            services.RemoveAll<IProxyForwardService>();
            services.AddSingleton<IProxyForwardService>(_fakeForwardService);
        });
    }

    /// <summary>
    /// 在客户端配置完成后执行测试数据初始化。
    /// </summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        SeedAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 准备当前测试场景所需的数据。
    /// </summary>
    private async Task SeedAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SqlSugarSetup.InitializeDatabase(db.Client);

        var firstSite = new Site
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "Primary OpenAI",
            BaseUrl = "https://invalid-primary.example.com",
            ApiKey = "upstream-key-1",
            ProtocolType = "OpenAI",
            SupportsOpenAi = true,
            SupportsAnthropic = false,
            IsEnabled = true
        };
        var secondSite = new Site
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Name = "Fallback GLM",
            BaseUrl = "https://invalid-fallback.example.com",
            ApiKey = "upstream-key-2",
            ProtocolType = "OpenAI",
            SupportsOpenAi = true,
            SupportsAnthropic = false,
            IsEnabled = true
        };
        var thirdSite = new Site
        {
            Id = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Name = "Primary OpenAI Replica",
            BaseUrl = "https://invalid-replica.example.com",
            ApiKey = "upstream-key-3",
            ProtocolType = "OpenAI",
            SupportsOpenAi = true,
            SupportsAnthropic = false,
            IsEnabled = true
        };

        var accessKeyRaw = "test-key";
        var accessKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accessKeyRaw)));
        var proxyAccessKey = new ProxyAccessKey
        {
            Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            KeyName = "integration",
            AccessKeyHash = accessKeyHash,
            MaskedValue = "sk-***key",
            IsEnabled = true
        };

        var routeRules = new[]
        {
            new ProxyRouteRule
            {
                ExternalModelName = "chat-prod",
                UpstreamModelName = "gpt-5.5",
                SiteId = firstSite.Id,
                SiteModelName = "gpt-5.5-a",
                Priority = 0,
                ModelPriority = 0,
                InstancePriority = 0,
                IsEnabled = true
            },
            new ProxyRouteRule
            {
                ExternalModelName = "chat-prod",
                UpstreamModelName = "glm-5.1",
                SiteId = secondSite.Id,
                SiteModelName = "glm-5.1-a",
                Priority = 1,
                ModelPriority = 1,
                InstancePriority = 0,
                IsEnabled = true
            }
        };

        db.Sites.AddRange(firstSite, secondSite, thirdSite);
        db.ProxyAccessKeys.Add(proxyAccessKey);
        db.ProxyRouteEntries.Add(new ProxyRouteEntry
        {
            EntryName = "chat-prod"
        });
        db.SiteModelMappings.AddRange(
            new SiteModelMapping
            {
                SiteId = firstSite.Id,
                ModelLibraryItemId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                RemoteModelName = "gpt-5.5-a",
                LastStatus = "ok",
                IsEnabled = true
            },
            new SiteModelMapping
            {
                SiteId = thirdSite.Id,
                ModelLibraryItemId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                RemoteModelName = "gpt-5.5-b",
                LastStatus = "ok",
                IsEnabled = true
            },
            new SiteModelMapping
            {
                SiteId = secondSite.Id,
                ModelLibraryItemId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
                RemoteModelName = "glm-5.1-a",
                LastStatus = "ok",
                IsEnabled = true
            });
        db.ProxyRouteRules.AddRange(routeRules);
        // 单例表 Id=1 可能已被启动逻辑创建，先删除避免唯一约束冲突。
        db.Client.Deleteable<AITool.Domain.Operations.SystemRuntimeSettings>().Where(x => x.Id == 1).ExecuteCommand();
        db.SystemRuntimeSettings.Add(new AITool.Domain.Operations.SystemRuntimeSettings
        {
            Id = 1,
            ProxyRequestTimeoutSeconds = 9,
            ProxyRetryCount = 2,
            DetectionRequestTimeoutSeconds = 60,
            DetectionRetryCount = 0,
            DetectionConcurrency = 1,
            CircuitBreakerFailureThreshold = 5,
            CircuitBreakerRecoveryMinutes = 2,
            UsageLogRetentionDays = 7,
            UsageLogAutoCleanupEnabled = true
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 释放测试过程中创建的资源。
    /// </summary>
    public new async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
    }
}

/// <summary>
/// 用于模拟代理转发结果，支撑 FakeProxyForwardService 相关断言。
/// </summary>
internal sealed class FakeProxyForwardService : IProxyForwardService
{
    /// <summary>
    /// 记录当前测试中已经发生的转发尝试次数。
    /// </summary>
    private int _attemptCount;

    /// <summary>
    /// 保存测试期间捕获到的转发请求。
    /// </summary>
    public List<ProxyForwardRequest> Requests { get; } = new();
    /// <summary>
    /// 保存测试时返回的流式响应片段。
    /// </summary>
    public List<string>? StreamingLines { get; set; }
    /// <summary>
    /// 允许按请求动态生成非流式转发结果，便于模拟首路由失败等场景。
    /// </summary>
    public Func<ProxyForwardRequest, ProxyForwardResult>? ForwardResultFactory { get; set; }
    /// <summary>
    /// 允许按请求动态生成流式转发结果，便于覆盖中断与透传场景。
    /// </summary>
    public Func<ProxyForwardRequest, ProxyForwardResult>? StreamingResultFactory { get; set; }

    /// <summary>
    /// 使用固定的两次结果模拟主路由失败、备路由成功的回退链路
    /// </summary>
    public Task<ProxyForwardResult> ForwardAsync(ProxyForwardRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(CloneRequest(request));

        var customResult = ForwardResultFactory?.Invoke(request);
        if (customResult is not null)
        {
            return Task.FromResult(customResult);
        }

        _attemptCount++;
        if (_attemptCount == 1)
        {
            return Task.FromResult(new ProxyForwardResult
            {
                Success = false,
                StatusCode = 500,
                ErrorMessage = "first route failed"
            });
        }

        return Task.FromResult(new ProxyForwardResult
        {
            Success = true,
            StatusCode = 200,
            ResponseBody = "{\"choices\":[{\"message\":{\"content\":\"success-from-second-route\"}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2}}",
            InputTokens = 1,
            OutputTokens = 2
        });
    }

    /// <summary>
    /// 模拟流式转发过程，并按测试设定回放 OpenAI SSE 分片。
    /// </summary>
    public async Task<ProxyForwardResult> ForwardStreamingAsync(
        ProxyForwardRequest request,
        Func<string, CancellationToken, Task> onSseDataAsync,
        CancellationToken cancellationToken = default)
    {
        if (StreamingLines is not null || StreamingResultFactory is not null)
        {
            Requests.Add(CloneRequest(request));
            var lines = StreamingLines ?? [];
            foreach (var line in lines)
            {
                await onSseDataAsync(line, cancellationToken);
            }

            var streamingResult = StreamingResultFactory?.Invoke(request) ?? new ProxyForwardResult
            {
                Success = true,
                StatusCode = 200,
                ResponseBody = string.Join("\n", lines),
                IsStreaming = true,
                HasStartedStreaming = lines.Any(line => !string.IsNullOrEmpty(line))
            };
            streamingResult.ResponseBody = string.IsNullOrWhiteSpace(streamingResult.ResponseBody)
                ? string.Join("\n", lines)
                : streamingResult.ResponseBody;
            streamingResult.IsStreaming = true;
            return streamingResult;
        }

        var result = await ForwardAsync(request, cancellationToken);
        foreach (var line in result.ResponseBody.Replace("\r\n", "\n").Split('\n'))
        {
            await onSseDataAsync(line, cancellationToken);
        }

        result.IsStreaming = true;
        return result;
    }

    /// <summary>
    /// 复制转发请求的关键字段，避免测试断言读到被后续修改的引用。
    /// </summary>
    private static ProxyForwardRequest CloneRequest(ProxyForwardRequest request)
    {
        return new ProxyForwardRequest
        {
            TargetBaseUrl = request.TargetBaseUrl,
            TargetApiKey = request.TargetApiKey,
            ProtocolType = request.ProtocolType,
            TargetModelName = request.TargetModelName,
            RequestBody = request.RequestBody,
            PreparedRequestBody = request.PreparedRequestBody,
            EnableStreaming = request.EnableStreaming,
            RequestTimeoutSeconds = request.RequestTimeoutSeconds,
            RetryCount = request.RetryCount,
            TargetPath = request.TargetPath,
            ForwardHeaders = new Dictionary<string, string>(request.ForwardHeaders, StringComparer.OrdinalIgnoreCase)
        };
    }
}
