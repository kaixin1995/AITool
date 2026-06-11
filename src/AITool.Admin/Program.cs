using AppVersionInfo = AITool.Infrastructure.Hosting.AppVersionInfo;
using HttpExceptionLoggingFilter = AITool.Infrastructure.Hosting.HttpExceptionLoggingFilter;
using HttpLogFormatter = AITool.Infrastructure.Hosting.HttpLogFormatter;
using AITool.Application.Common;
using AITool.Application.Operations;
using AITool.Application.SiteCatalog;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Hosting;
using AITool.Infrastructure.OpenAI;
using AITool.Infrastructure.Operations;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Scheduling;
using AITool.Admin.Services;
using Hangfire;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
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

// Admin 独立宿主当前阶段先保留最小页面与控制器框架，后续再逐步把真实 /Admin/* 页面迁进来。
builder.Services.AddRazorPages();
builder.Services.AddControllers(options =>
{
    options.Filters.Add<HttpExceptionLoggingFilter>();
});
builder.Services.AddMemoryCache();
builder.Services.AddScoped<HttpExceptionLoggingFilter>();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Login";
        options.AccessDeniedPath = "/Login";
        options.Cookie.Name = "AITool.AdminAuth";
        options.SlidingExpiration = true;
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                if (IsAdminRequest(context.Request))
                {
                    var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
                    var loginUrl = string.IsNullOrWhiteSpace(returnUrl)
                        ? "/Login"
                        : $"/Login?returnUrl={Uri.EscapeDataString(returnUrl)}";
                    context.Response.Redirect(loginUrl);
                    return Task.CompletedTask;
                }

                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

// 当前数据库仍由 Admin 宿主使用，保证历史 UsageLogs、Conversations、Detection 等数据能够继续使用。
var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aitool.db");
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? $"Data Source={Path.GetFullPath(dbPath)}";
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddScoped<ISystemRuntimeSettingsService, SystemRuntimeSettingsService>();

// 对话记录查询所需的服务。Admin 侧仅注册只读查询链路，不包含写入侧的 ConversationLogBatchWriter / ConversationLogService。
// 测试环境使用随机临时目录作为 JSONL 根路径，确保每个测试工厂实例拥有隔离的文件存储，不会互相污染。
var conversationLogRootPath = builder.Environment.IsEnvironment("Testing")
    ? Path.Combine(Path.GetTempPath(), $"aitool-conversation-logs-{Guid.NewGuid():N}")
    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "conversation-logs");
builder.Services.AddSingleton(new AITool.Infrastructure.Conversations.ConversationLogFileOptions
{
    RootPath = conversationLogRootPath
});
builder.Services.AddSingleton<AITool.Application.Conversations.IConversationLogStore, AITool.Infrastructure.Conversations.FileConversationLogStore>();
builder.Services.AddSingleton<AITool.Infrastructure.Conversations.ConversationExtractionService>();

// Admin 侧 UsageLog 事件消费器，将 Core 代理产生的使用日志事件写入 Admin 数据库。
builder.Services.AddScoped<AdminUsageLogEventIngestor>();

// Admin 侧 ConversationTurn 事件消费器，将 Core 代理产生的对话记录事件写入 Admin 本地 JSONL 存储。
builder.Services.AddScoped<AITool.Infrastructure.Conversations.AdminConversationTurnEventIngestor>();

// Admin 侧开发者追踪内存存储，缓存从 Core 拉取的 developer-trace 事件摘要。
// Singleton 生命周期：内存数据跨请求保持，6 小时过期自动清理，最多 100 条。
builder.Services.AddSingleton<AdminDeveloperTraceStore>();
// Admin 侧 DeveloperTrace 事件消费器，将 Core 代理产生的追踪事件写入内存存储。
builder.Services.AddScoped<AdminDeveloperTraceEventIngestor>();

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
builder.Services.AddSingleton(sp => {
    var logger = sp.GetRequiredService<ILogger<CoreEventAckStateStore>>();
    return new CoreEventAckStateStore(ackMetaPath, logger);
});

// Admin 侧事件拉取核心逻辑，从 HostedService 中提取出来以便独立测试。
// HostedService 每个轮次创建新 scope 并通过 ActivatorUtilities 解析此服务。
builder.Services.AddScoped<CoreEventPullService>();

// Admin 侧缓存失效门面，通过 CoreAdminClient 向 Core 下发全量配置快照以刷新运行时缓存。
builder.Services.AddScoped<AdminCacheInvalidationService>();

// Admin 侧并发控制门面（占位实现）。后续通过 CoreAdminClient 代理运行时并发限制变更。
builder.Services.AddSingleton<AdminConcurrencyControlService>();

// 站点目录客户端，用于从远程站点获取可用模型列表。
builder.Services.AddHttpClient<ISiteCatalogClient, OpenAiSiteCatalogClient>();

// Hangfire 内存存储与定时检测调度器。
builder.Services.AddHangfire(config => config
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseInMemoryStorage());
builder.Services.AddHangfireServer();
builder.Services.AddSingleton<HangfireDetectionScheduler>();

// 模型厂商目录服务（可选，在模型库页面管理厂商规则时使用）。
builder.Services.AddSingleton<ModelVendorCatalogService>();

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

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // 启动时注册所有已启用的定时检测任务到 Hangfire
    var scheduler = scope.ServiceProvider.GetRequiredService<HangfireDetectionScheduler>();
    try
    {
        await scheduler.ScheduleAllAsync(default);
    }
    catch (Exception ex)
    {
        startupLogger.Warn(ex, "启动时注册定时检测任务失败，将在后台重试");
    }
}

startupLogger.Info(
    "Admin 宿主启动完成。Version={Version}, Environment={Environment}, Port={Port}, CoreBaseUrl={CoreBaseUrl}",
    applicationVersion,
    app.Environment.EnvironmentName,
    serverPort,
    coreBaseUrl);
Console.WriteLine($"AI Tool Admin 已启动：http://127.0.0.1:{serverPort}");

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapRazorPages();
app.Run();

static bool IsAdminRequest(HttpRequest request)
{
    return request.Path.StartsWithSegments("/Admin", StringComparison.OrdinalIgnoreCase)
        || request.Path.StartsWithSegments("/Login", StringComparison.OrdinalIgnoreCase);
}

public partial class Program;
