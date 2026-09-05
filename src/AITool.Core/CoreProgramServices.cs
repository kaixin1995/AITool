using AITool.Core.Services;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Proxy;

namespace AITool.Core;

/// <summary>
/// Core 宿主独有服务注册段（供 Core 与 AllInOne 两种宿主复用，避免双份注册漂移）。
/// 范围：代理控制器直接依赖的 OAuth 刷新链、事件发布器、诊断抓包、内存维护。
/// </summary>
public static class CoreProgramServices
{
    public static void AddCoreProxyHostServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<ModelConcurrencyQueryService>();

        // —— 托管 OAuth 账号的 Core 侧无库能力（401 即刷 / 403 禁用，split 双宿主）——
        // OAuth 客户端是纯 HTTP 实现（Infrastructure），Core 可直接复用；刷新后经事件总线回传 Admin 落库。
        builder.Services.AddHttpClient<AITool.Application.Codex.ICodexOAuthClient, AITool.Infrastructure.Codex.CodexOAuthClient>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(20);
        });
        builder.Services.AddHttpClient<AITool.Application.Google.IGoogleOAuthClient, AITool.Infrastructure.Google.GoogleOAuthClient>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddHttpClient<AITool.Application.Kimi.IKimiOAuthClient, AITool.Infrastructure.Kimi.KimiOAuthClient>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        builder.Services.AddScoped<CoreCredentialRefreshEngine>();
        builder.Services.AddScoped<CodexCredentialRefreshService>();
        builder.Services.AddScoped<GoogleCredentialRefreshService>();
        builder.Services.AddScoped<KimiCredentialRefreshService>();

        // 代理诊断抓包（文件型转储）。Core 与 Admin 部署为兄弟目录，共享同一抓包目录实现跨宿主可见；
        // Admin 目录不存在（独立部署）时回退 Core 本地目录。
        builder.Services.AddSingleton<IProxyDiagnosticService>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<ProxyDiagnosticService>>();
            var metadataCache = sp.GetService<ProxyRequestMetadataCache>();
            var sharedRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "AITool.Admin"));
            var baseDir = Directory.Exists(sharedRoot) ? sharedRoot : AppDomain.CurrentDomain.BaseDirectory;
            return new ProxyDiagnosticService(logger, metadataCache, baseDir);
        });

        // 注册统一代理事件发布器，当追踪完成、熔断触发、路由回退时发布事件到 Core 事件总线（Core 独有）。
        builder.Services.AddSingleton<CoreUnifiedProxyEventPublisher>();

        // 注册路由回退事件发布器，当代理请求在路由间回退时发布 route-fallback 事件（Core 独有）。
        builder.Services.AddSingleton<CoreRouteFallbackEventPublisher>();

        // 注册熔断状态变更事件发布器，当路由因连续失败达到阈值被首次熔断时发布 circuit-breaker 事件（Core 独有）。
        builder.Services.AddSingleton<CoreCircuitBreakerEventPublisher>();

        // LOH 碎片压缩：Core 是代理主链路，每请求产生大字符串碎片，必须定期压缩避免工作集持续升高。
        builder.Services.AddHostedService<AITool.Infrastructure.Hosting.MemoryMaintenanceService>();
    }
}