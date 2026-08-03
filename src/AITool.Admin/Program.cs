using AppVersionInfo = AITool.Infrastructure.Hosting.AppVersionInfo;
using AITool.Application.Codex;
using AITool.Application.Common;
using AITool.Infrastructure.Codex;
using AITool.Infrastructure.Conversations;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.DependencyInjection;
using AITool.Infrastructure.Hosting;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Infrastructure.Retention;
using AITool.Admin.Services;
using Hangfire;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NLog;
using NLog.Web;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Host.UseNLog();

var startupLogger = LogManager.GetLogger("Startup");
var applicationVersion = "1.0.1.7-admin";
builder.Services.AddSingleton(new AppVersionInfo(applicationVersion));

var serverPort = builder.Configuration.GetValue<int?>("AdminServer:Port") ?? builder.Configuration.GetValue<int?>("Server:Port") ?? 5030;
builder.WebHost.UseUrls($"http://0.0.0.0:{serverPort}");

// 配置 Kestrel 连接与请求体限制，确保代理大请求体（长对话、base64 图片）和可预测的并发行为。
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxConcurrentConnections = 500;
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(130);
    // 代理请求体可能很大（含图片、长上下文），不限制请求体大小。
    options.Limits.MaxRequestBodySize = null;
});

// 注册所有宿主共享的基础设施：控制器、内存缓存、异常过滤器、对话日志存储。
var conversationLogRootPath = builder.Environment.IsEnvironment("Testing")
    ? Path.Combine(Path.GetTempPath(), $"aitool-conversation-logs-{Guid.NewGuid():N}")
    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "conversation-logs");
builder.Services.AddCommonInfrastructure(conversationLogRootPath);

// 启用响应压缩，压缩 API JSON 响应（Analytics/UsageLogs/Invocations 列表）和静态资源。
// EnableForHttps=true 确保内网 HTTPS 部署也压缩。
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});

// 注册 Web + Admin 共享的管理后台基础设施：Razor Pages、认证、数据库、Hangfire。
var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aitool.db");
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? $"Data Source={Path.GetFullPath(dbPath)}";
builder.Services.AddAdminInfrastructure(connectionString);

// ===== JWT 认证（替换原 Cookie 方案，适配 Vue SPA 前端）=====
// 配置 JwtOptions 绑定 + 签发/刷新服务 + 登录暴力破解防护。
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddSingleton<LoginRateLimitService>();
// 管理后台认证服务（PBKDF2 密码哈希，兼容旧 MD5 透明升级）。
builder.Services.AddSingleton<AdminAuthService>();

// 认证：纯 JWT Bearer。/api/* 用 Bearer token 验证；代理端点 /v1/* 不走 ASP.NET 认证（AccessKey 自校验）。
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
        // /api/* 未携带有效 token 时统一返回 401 JSON（前端按 401 + errorCode 处理）。
        options.Events = new JwtBearerEvents
        {
            OnChallenge = context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json; charset=utf-8";
                return context.Response.WriteAsJsonAsync(new
                {
                    success = false,
                    message = "未登录或登录已过期，请重新登录",
                    errorCode = "unauthenticated"
                });
            }
        };
    });
builder.Services.AddAuthorization();

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

// ===== Codex OAuth 账号管理功能（管理面，依赖 AppDbContext，仅在 Admin 宿主注册） =====
// 注册 Codex OAuth 客户端，用于 PKCE 授权、token 交换与刷新（复用连接池）。
builder.Services.AddHttpClient<ICodexOAuthClient, CodexOAuthClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
});
// 注册 Codex 静态模型目录（进程内只读）。
builder.Services.AddSingleton<ICodexModelCatalog, CodexModelCatalog>();
// 注册 Codex 动态模型拉取客户端（chatgpt.com/backend-api/codex/models）。
builder.Services.AddHttpClient<ICodexModelFetcher, CodexModelFetcher>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
});
// 注册 Codex 额度主动查询服务（30s 结果缓存防抖 + single-flight）。
builder.Services.AddHttpClient<ICodexQuotaService, CodexQuotaService>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
});
// 周期刷新 Codex 账号 OAuth token，写回隐藏 Site.ApiKey 并通过 AdminCacheInvalidationService 推送到 Core。
builder.Services.AddHostedService<CodexTokenRefreshService>();
// 周期恢复冷却到期的 Codex 账号（清除冷却，恢复 Site，若未被手动禁用）。
builder.Services.AddHostedService<CodexCooldownRecoveryService>();
// 注册 Codex 账号供给相关服务（站点级联删除工具 + 账号工厂）。
builder.Services.AddScoped<SiteCascadeDeleter>();
builder.Services.AddScoped<CodexAccountProvisioner>();
// Codex 额度被动冷却与重置服务。
builder.Services.AddScoped<ICodexQuotaCooldownService, CodexQuotaCooldownService>();
// Codex 手动重置 credits 服务（查询剩余次数/过期时间 + 消耗一张 credit 执行真实重置）。
builder.Services.AddHttpClient<ICodexResetCreditsService, CodexResetCreditsService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
// Codex 功能总开关过滤器（控制器级 gating）。
builder.Services.AddScoped<CodexFeatureToggleAttribute>();
// Codex 巡检开关过滤器（仅巡检相关 action 使用，关闭时返回 404）。
builder.Services.AddScoped<CodexInspectionToggleAttribute>();
// Codex 巡检后台服务（周期额度巡检 + 缓存策略 + 自动禁用）。单例，供 API 与后台共用状态。
builder.Services.AddSingleton<CodexInspectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CodexInspectionService>());

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

// 预热 SiteUsageTracker：从 DB 读每个 Site 最近一次使用时间，避免重启后历史丢失。
// Testing 环境跳过（无真实数据库，预热会抛异常）。
if (!app.Environment.IsEnvironment("Testing"))
{
    using var warmupScope = app.Services.CreateScope();
    var siteUsageTracker = warmupScope.ServiceProvider.GetRequiredService<SiteUsageTracker>();
    var warmupDbContext = warmupScope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        await siteUsageTracker.WarmupAsync(warmupDbContext);
    }
    catch (Exception ex)
    {
        initLogger.LogWarning(ex, "SiteUsageTracker 预热失败，不影响启动（运行后会逐步重建映射）");
    }
}

startupLogger.Info(
    "Admin 宿主启动完成。Version={Version}, Environment={Environment}, Port={Port}, CoreBaseUrl={CoreBaseUrl}",
    applicationVersion,
    app.Environment.EnvironmentName,
    serverPort,
    coreBaseUrl);
Console.WriteLine($"AI Tool Admin 已启动：http://127.0.0.1:{serverPort}");

// 全局异常处理：捕获未处理异常并记录详细日志，返回统一 JSON 错误响应。
app.UseGlobalExceptionHandler(app.Environment);

// 响应压缩必须在其他产生响应的中间件（静态文件、MVC）之前注册。
app.UseResponseCompression();

// 静态文件配置 Cache-Control 头，让浏览器缓存 CSS/JS/图片，重复访问零往返。
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // 静态文件带文件指纹（版本号）时缓存 1 天；无指纹时浏览器仍会条件请求。
        ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=86400";
    }
});
app.UseAuthentication();
app.UseAuthorization();

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
// SPA fallback：非 /api、非 /v1 的请求统一返回前端 index.html，由 Vue Router 接管路由。
// wwwroot 静态文件由上方的 UseStaticFiles 提供（Vite 构建产物输出到 Admin/wwwroot）。
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
