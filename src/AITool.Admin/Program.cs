using AppVersionInfo = AITool.Infrastructure.Hosting.AppVersionInfo;
using AITool.Application.Common;
using AITool.Infrastructure.Conversations;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.DependencyInjection;
using AITool.Infrastructure.Hosting;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Infrastructure.Retention;
using AITool.Admin.Services;
using Hangfire;
using NLog;
using NLog.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Host.UseNLog();

var startupLogger = LogManager.GetLogger("Startup");
var applicationVersion = "1.0.1.4-admin";
builder.Services.AddSingleton(new AppVersionInfo(applicationVersion));

var serverPort = builder.Configuration.GetValue<int?>("AdminServer:Port") ?? builder.Configuration.GetValue<int?>("Server:Port") ?? 5030;
builder.WebHost.UseUrls($"http://0.0.0.0:{serverPort}");

// 注册所有宿主共享的基础设施：控制器、内存缓存、异常过滤器、对话日志存储。
var conversationLogRootPath = builder.Environment.IsEnvironment("Testing")
    ? Path.Combine(Path.GetTempPath(), $"aitool-conversation-logs-{Guid.NewGuid():N}")
    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "conversation-logs");
builder.Services.AddCommonInfrastructure(conversationLogRootPath);

// 注册 Web + Admin 共享的管理后台基础设施：Razor Pages、认证、数据库、Hangfire。
var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aitool.db");
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? $"Data Source={Path.GetFullPath(dbPath)}";
builder.Services.AddAdminInfrastructure(connectionString);

// 管理后台认证服务，用于 Login 页面密码验证和 AdminAuthenticationMiddleware。
builder.Services.AddSingleton<AdminAuthService>();

// Admin 侧 ConversationTurn 事件消费器，将 Core 代理产生的对话记录事件写入 Admin 本地 JSONL 存储。
builder.Services.AddScoped<AdminConversationTurnEventIngestor>();

// Admin 侧开发者追踪内存存储，缓存从 Core 拉取的 developer-trace 事件摘要。
// Singleton 生命周期：内存数据跨请求保持，6 小时过期自动清理，最多 100 条。
builder.Services.AddSingleton<AdminDeveloperTraceStore>();
// Admin 侧 UnifiedProxy 事件消费器，统一消费 Core 代理产生的 UsageLog 和 DeveloperTrace 事件。
builder.Services.AddScoped<AdminUnifiedProxyEventIngestor>();

// Admin 侧路由回退事件内存存储，缓存从 Core 拉取的 route-fallback 事件。
// Singleton 生命周期：内存数据跨请求保持，6 小时过期自动清理，最多 200 条。
builder.Services.AddSingleton<AdminRouteFallbackStore>();
// Admin 侧 RouteFallback 事件消费器，将 Core 代理产生的路由回退事件写入内存存储。
builder.Services.AddScoped<AdminRouteFallbackEventIngestor>();

// Admin 侧配置变更应用事件内存存储，缓存从 Core 拉取的 config-applied 事件。
// Singleton 生命周期：内存数据跨请求保持，24 小时过期自动清理，最多 100 条。
builder.Services.AddSingleton<AdminConfigAppliedStore>();
// Admin 侧 ConfigApplied 事件消费器，将 Core 配置变更确认事件写入内存存储。
builder.Services.AddScoped<AdminConfigAppliedEventIngestor>();

// Admin 侧熔断状态变更事件内存存储，缓存从 Core 拉取的 circuit-breaker 事件。
// Singleton 生命周期：内存数据跨请求保持，6 小时过期自动清理，最多 200 条。
builder.Services.AddSingleton<AdminCircuitBreakerStore>();
// Admin 侧 CircuitBreaker 事件消费器，将 Core 代理产生的熔断事件写入内存存储。
builder.Services.AddScoped<AdminCircuitBreakerEventIngestor>();

// Admin 侧事件 ack 状态持久化，将已确认序号写入本地文件，确保重启后不重复消费历史事件。
var ackMetaPath = builder.Environment.IsEnvironment("Testing")
    ? Path.Combine(Path.GetTempPath(), $"aitool-core-event-ack-{Guid.NewGuid():N}", "ack.meta")
    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "core-runtime", "ack.meta");
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<CoreEventAckStateStore>>();
    return new CoreEventAckStateStore(ackMetaPath, logger);
});

// Admin 侧事件拉取核心逻辑，从 HostedService 中提取出来以便独立测试。
// HostedService 每个轮次创建新 scope 并通过 ActivatorUtilities 解析此服务。
builder.Services.AddScoped<CoreEventPullService>();

// Admin 侧缓存失效门面，通过 CoreAdminClient 向 Core 下发全量配置快照以刷新运行时缓存。
builder.Services.AddSingleton<CoreSyncStatusStore>();
builder.Services.AddSingleton<ProxyRequestMetadataCache>(sp =>
{
    var memoryCache = sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    return new ProxyRequestMetadataCache(memoryCache, scopeFactory);
});
builder.Services.AddSingleton<AdminQueryMetadataService>();
builder.Services.AddScoped<AdminCacheInvalidationService>();

// Admin 侧并发控制门面（占位实现）。后续通过 CoreAdminClient 代理运行时并发限制变更。
builder.Services.AddSingleton<AdminConcurrencyControlService>();

// 模型厂商目录服务（可选，在模型库页面管理厂商规则时使用）。
builder.Services.AddSingleton<ModelVendorCatalogService>();

// 注册日志保留策略服务，定时清理过期日志。
builder.Services.AddScoped<ILogRetentionService, LogRetentionService>();

// Admin 通过最小 Core 客户端与核心宿主通信。当前阶段先提供握手、full-sync、ack、replay 这几项最关键能力。
var coreBaseUrl = builder.Configuration["CoreServer:BaseUrl"] ?? $"http://127.0.0.1:{builder.Configuration.GetValue<int?>("CoreServer:Port") ?? 5029}/";
builder.Services.AddHttpClient<CoreAdminClient>(client =>
{
    client.BaseAddress = new Uri(coreBaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(30);
});
// SSE 专用 HttpClient，用于 CoreEventPullHostedService 实时监听 Core 事件通知流。
// SSE 是无限流，不能用默认的 30 秒超时，必须设置无限超时。
builder.Services.AddHttpClient("CoreSSE");

// Admin 启动后自动将数据库配置同步到 Core 宿主。
// 如果 Core 尚未就绪，会按指数退避重试，最多 5 次。
builder.Services.AddHostedService<CoreConfigSyncHostedService>();

// Admin 定时从 Core 拉取事件（replay）、消费入库（ingest）、提交确认（ack）。
// 构成完整的事件消费闭环：Core 产生事件 → spool 兜底 → Admin 拉取 → 入库 → 确认。
builder.Services.AddHostedService<CoreEventPullHostedService>();

var app = builder.Build();

// 执行管理后台启动初始化：数据库创建、Schema 迁移、Hangfire 调度注册。
var initLogger = app.Services.GetRequiredService<ILogger<Program>>();
await AdminStartupInitializer.InitializeAsync(app.Services, initLogger);

startupLogger.Info(
    "Admin 宿主启动完成。Version={Version}, Environment={Environment}, Port={Port}, CoreBaseUrl={CoreBaseUrl}",
    applicationVersion,
    app.Environment.EnvironmentName,
    serverPort,
    coreBaseUrl);
Console.WriteLine($"AI Tool Admin 已启动：http://127.0.0.1:{serverPort}");

// 全局异常处理：捕获未处理异常并记录详细日志，返回统一 JSON 错误响应。
app.UseGlobalExceptionHandler(app.Environment);

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAdminAuthentication();

// 映射健康检查端点，作为集成测试的验证入口。
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// 启用 Hangfire 仪表盘，仅限本地访问。
app.UseHangfireDashboard("/hangfire");

// 注册日志清理定时任务，每天凌晨 3 点执行。
RecurringJob.AddOrUpdate<ILogRetentionService>(
    "log-retention-prune",
    svc => svc.PruneAsync(CancellationToken.None),
    "0 3 * * *");

app.MapControllers();
app.MapRazorPages();
app.Run();

public partial class Program;
