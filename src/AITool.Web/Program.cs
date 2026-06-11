using AITool.Application.Common;
using AITool.Application.Operations;
using AITool.Application.Proxy;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.DependencyInjection;
using AITool.Infrastructure.Hosting;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Infrastructure.Retention;
using AITool.Infrastructure.Scheduling;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using NLog;
using NLog.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Host.UseNLog();

var startupLogger = LogManager.GetLogger("Startup");

var applicationVersion = "1.0.1.4";
builder.Services.AddSingleton(new AppVersionInfo(applicationVersion));

var serverPort = builder.Configuration.GetValue<int?>("Server:Port") ?? 5029;
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

// Web 独有的管理后台认证服务。
builder.Services.AddSingleton<AdminAuthService>();

// 注册代理运行时核心链路服务。
builder.Services.AddSingleton(new CoreRuntimeConfigFileOptions
{
    FilePath = builder.Environment.IsEnvironment("Testing")
        ? Path.Combine(Path.GetTempPath(), $"aitool-core-runtime-config-{Guid.NewGuid():N}.json")
        : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "core-runtime", "last-good-config.json")
});
builder.Services.AddSingleton<CoreRuntimeConfigProvider>();
builder.Services.AddSingleton<AITool.Application.CoreRuntime.ICoreRuntimeConfigProvider>(sp => sp.GetRequiredService<CoreRuntimeConfigProvider>());

// 注册代理运行时核心链路服务：代理转发、并发控制、熔断、事件总线、批处理写入器等。
// Web 宿主传入 useCoreRuntimeConfigProviderForCache: false，使元数据缓存始终通过数据库查询获取数据。
var coreEventSpoolRootPath = builder.Environment.IsEnvironment("Testing")
    ? Path.Combine(Path.GetTempPath(), $"aitool-core-event-spool-{Guid.NewGuid():N}")
    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "core-runtime", "spool");
builder.Services.AddProxyRuntimeInfrastructure(
    builder.Configuration.GetSection(ProxyForwardingOptions.SectionName),
    coreEventSpoolRootPath,
    useCoreRuntimeConfigProviderForCache: false);

// 注册日志保留策略服务，定时清理过期日志。
builder.Services.AddScoped<ILogRetentionService, LogRetentionService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    await DatabaseSchemaMigrator.EnsureProxyUsageLogSchemaAsync(db);
    await DatabaseSchemaMigrator.EnsureConversationLogSchemaAsync(db);

    var scheduler = scope.ServiceProvider.GetRequiredService<HangfireDetectionScheduler>();
    try
    {
        await scheduler.ScheduleAllAsync(default);
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "启动时注册检测任务失败，将在下次启动时重试");
    }

    var settingsService = scope.ServiceProvider.GetRequiredService<ISystemRuntimeSettingsService>();
    var configProvider = scope.ServiceProvider.GetRequiredService<AITool.Application.CoreRuntime.ICoreRuntimeConfigProvider>();
    if (!await configProvider.TryLoadFromFileAsync())
    {
        startupLogger.Warn("Core 启动时未找到可恢复的 last-good-config，将等待 Admin 下发首个完整配置快照后进入 ready 状态。");
    }
    var circuitStore = scope.ServiceProvider.GetRequiredService<RouteCircuitStateStore>();
    var settings = await settingsService.GetOrCreateAsync();
    circuitStore.UpdateOptions(
        TimeSpan.FromMinutes(settings.CircuitBreakerRecoveryMinutes),
        settings.CircuitBreakerFailureThreshold);
}

startupLogger.Info(
    "系统启动完成。Version={Version}, Environment={Environment}, Port={Port}",
    applicationVersion,
    app.Environment.EnvironmentName,
    serverPort);
Console.WriteLine($"AI Tool 已启动：http://127.0.0.1:{serverPort}");
Console.WriteLine($"AI Tool 已启动：http://{LocalIpAddressHelper.GetLocalIpAddress()}:{serverPort}");

// 全局异常处理：捕获未处理异常并记录详细日志，返回统一 JSON 错误响应。
app.UseGlobalExceptionHandler(app.Environment);

app.UseStaticFiles();
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (app.Environment.IsEnvironment("Testing") || !AdminRequestMatcher.IsAdminRequest(context.Request) || AdminRequestMatcher.IsLoginPageRequest(context.Request))
    {
        await next();
        return;
    }

    if (context.User.Identity?.IsAuthenticated == true)
    {
        await next();
        return;
    }

    var authService = context.RequestServices.GetRequiredService<AdminAuthService>();
    var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
    var loginUrl = string.IsNullOrWhiteSpace(returnUrl)
        ? "/Login"
        : $"/Login?returnUrl={Uri.EscapeDataString(returnUrl)}";

    if (AdminRequestMatcher.IsAdminApiRequest(context.Request))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    if (AdminRequestMatcher.IsHangfireRequest(context.Request))
    {
        context.Response.Redirect(loginUrl);
        return;
    }

    if (authService.HasPasswordConfigured())
    {
        context.Response.Redirect(loginUrl);
        return;
    }

    context.Response.Redirect(loginUrl);
});

// 映射健康检查端点，作为集成测试的验证入口。
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// 启用 Hangfire 仪表盘，仅限本地访问。
app.UseHangfireDashboard("/hangfire");

// 注册日志清理定时任务，每天凌晨 3 点执行。
RecurringJob.AddOrUpdate<ILogRetentionService>(
    "log-retention-prune",
    svc => svc.PruneAsync(CancellationToken.None),
    "0 3 * * *");

// 映射 Razor Pages 路由。
app.MapRazorPages();

// 映射 API 控制器路由，用于代理转发端点。
app.MapControllers();

app.Run();

/// <summary>
/// 程序入口。
/// </summary>
public partial class Program;
