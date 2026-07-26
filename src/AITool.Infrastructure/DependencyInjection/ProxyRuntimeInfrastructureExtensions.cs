using AITool.Application.Conversations;
using AITool.Application.CoreRuntime;
using AITool.Application.Proxy;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Conversations;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Proxy;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Infrastructure.DependencyInjection;

/// <summary>
/// 代理运行时基础设施服务注册扩展方法。
/// <para>
/// 这些服务是 Core 和 Web 宿主在代理转发运行时共享的基础设施：
/// 代理转发入口、并发控制、熔断状态、事件总线与 spool、
/// 使用日志/对话日志批处理写入器、元数据缓存等。
/// Core 宿主和 Web 宿主的 Program.cs 都调用本方法来消除 DI 注册重复。
/// </para>
/// <para>
/// 不包含在本方法中的服务（由各宿主自行注册）：
/// <list type="bullet">
///   <item>CoreRuntimeConfigProvider 及其选项 — 两个宿主注册时机和上下文不同</item>
///   <item>ModelConcurrencyQueryService — Core 独有的查询服务</item>
///   <item>DeveloperInvocationTraceQueryService — Core 独有的查询服务</item>
///   <item>CoreDeveloperTraceEventPublisher — Core 独有的事件发布器</item>
///   <item>CoreRouteFallbackEventPublisher — Core 独有的事件发布器</item>
///   <item>CoreCircuitBreakerEventPublisher — Core 独有的事件发布器</item>
/// </list>
/// </para>
/// </summary>
public static class ProxyRuntimeInfrastructureExtensions
{
    /// <summary>
    /// 注册 Core 和 Web 宿主共享的代理运行时基础设施服务。
    /// <para>
    /// 包括：代理转发配置与入口、并发控制、熔断状态存储、开发者追踪存储、
    /// 事件序列/总线/spool、各类事件发布器、使用日志和对话日志批处理写入器、
    /// 使用日志和对话日志服务接口、代理请求元数据缓存。
    /// </para>
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="proxyForwardingConfigSection">
    /// 代理转发配置的 IConfiguration section，用于绑定 ProxyForwardingOptions。
    /// </param>
    /// <param name="coreEventSpoolRootPath">
    /// Core 事件 spool 文件存储根路径。测试环境应传入随机临时目录以确保隔离。
    /// </param>
    /// <param name="useCoreRuntimeConfigProviderForCache">
    /// 是否在代理请求元数据缓存中使用 ICoreRuntimeConfigProvider。
    /// Core 宿主传入 true，使缓存方法在配置快照可用时优先从快照读取；
    /// Web 宿主传入 false，使缓存方法始终通过数据库查询获取数据。
    /// </param>
    public static IServiceCollection AddProxyRuntimeInfrastructure(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration proxyForwardingConfigSection,
        string coreEventSpoolRootPath,
        bool useCoreRuntimeConfigProviderForCache = false)
    {
        // 注册代理转发配置，统一控制单路由超时和失败重试策略。
        services.Configure<ProxyForwardingOptions>(proxyForwardingConfigSection);

        // 注册代理主入口实体配置。
        // 配置 SocketsHttpHandler 连接池：提高每服务器并发连接上限，定期回收空闲连接刷新 DNS。
        services.AddHttpClient<IProxyForwardService, ProxyForwardService>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 200,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30)
            });

        // 注册代理请求元数据缓存，缓存路由、密钥、并发限制等运行时数据。
        // Core 宿主的缓存数据来源是 Admin 下发的配置快照，而非直接查询数据库。
        // Web 宿主不需要配置快照，传入 null 使缓存始终通过数据库查询获取数据。
        services.AddSingleton<ProxyRequestMetadataCache>(sp =>
        {
            var memoryCache = sp.GetRequiredService<IMemoryCache>();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var configProvider = useCoreRuntimeConfigProviderForCache
                ? sp.GetRequiredService<ICoreRuntimeConfigProvider>()
                : null;
            return new ProxyRequestMetadataCache(memoryCache, scopeFactory, configProvider);
        });
        services.AddSingleton<AdminQueryMetadataService>();

        // 注册并发控制。
        services.AddSingleton<ModelConcurrencyLimiter>();

        // 注册熔断状态存储，跟踪因连续失败而被临时屏蔽的站点。
        services.AddSingleton<RouteCircuitStateStore>();

        // 注册开发者调用追踪存储（代理运行时写入端）。
        services.AddSingleton<DeveloperInvocationTraceStore>();

        // 注册事件序列、事件总线与 spool，支撑 Core -> Admin 可靠事件推送。
        services.AddSingleton<CoreEventSequenceProvider>();
        services.AddSingleton<CoreAdminEventBus>();
        services.AddSingleton(new CoreEventSpoolOptions { RootPath = coreEventSpoolRootPath });
        services.AddSingleton<CoreEventSpoolStore>();
        services.AddHostedService<CoreEventSpoolBackgroundService>();

        // 注册使用日志事件发布器和批处理写入器。
        // 宿主发布事件到总线，后台写入器批量持久化到数据库。
        services.AddSingleton<CoreConversationEventPublisher>();
        services.AddSingleton<CoreConfigAppliedEventPublisher>();
        // Site 使用时间内存映射：日志入队时增量更新，Codex 巡检读它判断账号是否被使用，避免回查 DB。
        // 注册在共享层，确保 Core 和 Admin 宿主都能解析（ProxyUsageLogBatchWriter 依赖它）。
        // 预热（从 DB 读历史）由持库的 Admin 宿主在启动时执行。
        services.AddSingleton<SiteUsageTracker>();
        services.AddSingleton<ProxyUsageLogBatchWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<ProxyUsageLogBatchWriter>());

        // 注册对话日志批处理写入器。
        // 对话日志文件存储已通过 AddCommonInfrastructure 注册，此处仅补充批处理写入器。
        services.AddSingleton<ConversationLogBatchWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<ConversationLogBatchWriter>());

        // 注册使用日志服务，记录每次代理调用的 Token 用量。
        services.AddSingleton<IUsageLogService, UsageLogService>();

        // 注册对话日志服务，提供对话日志的写入和查询能力。
        services.AddSingleton<IConversationLogService, ConversationLogService>();

        // 注册代理调用统一记录服务，将 UsageLog、DeveloperInvocationTrace、ConversationLog
        // 三套写入逻辑收口到单一入口，避免代理管道中分散地重复采集。
        services.AddSingleton<IProxyCallRecorder, ProxyCallRecorder>();

        return services;
    }
}
