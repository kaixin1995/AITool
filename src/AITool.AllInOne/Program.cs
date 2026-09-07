using AITool.Admin;
using AITool.Admin.Controllers.Admin;
using AITool.Admin.Services;
using AITool.AllInOne;
using AITool.Application.Proxy;
using AITool.Core;
using AITool.Infrastructure.Common;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.DependencyInjection;
using AITool.Infrastructure.Hosting;
using AITool.Infrastructure.Proxy;
using AITool.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using NLog;
using NLog.Web;

// ===================== 宿主 =====================
var builder = WebApplication.CreateBuilder(args);

// glibc malloc 调优（见 GlibcArenaLimiter；双宿主同款）。
GlibcArenaLimiter.TryApply(
    builder.Configuration.GetValue("NativeMemory:MallocArenaMax", 2),
    builder.Configuration.GetValue("NativeMemory:MallocTrimThresholdBytes", 64 * 1024),
    builder.Configuration.GetValue("NativeMemory:MallocMmapThresholdBytes", 128 * 1024));

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Host.UseNLog();

var startupLogger = LogManager.GetLogger("Startup");
var applicationVersion = ReadApplicationVersion();
var buildTime = ReadBuildTimestamp() ?? DateTimeOffset.UtcNow;
builder.Services.AddSingleton(new AppVersionInfo(applicationVersion, buildTime));

var serverPort = builder.Configuration.GetValue<int?>("AllInOneServer:Port") ?? builder.Configuration.GetValue<int?>("Server:Port") ?? 5030;
builder.WebHost.UseUrls($"http://0.0.0.0:{serverPort}");
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxConcurrentConnections = 1000;
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(130);
    options.Limits.MaxRequestBodySize = null;
});

// ===================== 服务 =====================
// 共享基础设施：控制器、内存缓存、异常过滤器、流式增强默认配置（心跳 15s / 可恢复缓冲开）。
builder.Services.AddCommonInfrastructure();
// 追加 Admin/Core 两宿主的控制器程序集，并排除单进程形态下的冲突控制器。
builder.Services.AddControllers()
    .AddApplicationPart(typeof(AITool.Admin.AdminProgramMarker).Assembly)
    .AddApplicationPart(typeof(AITool.Core.CoreProgramMarker).Assembly)
    .ConfigureApplicationPartManager(manager => manager.FeatureProviders.Add(new AllInOneControllerFilter()));

// Admin 管理面：认证、数据库、后台服务、OAuth 全家桶（与 Admin 宿主同一注册段）。
// enableCrossHostServices=false：单进程无跨宿主，跳过配置下发/事件拉取 HostedService
// （代理事件经进程内总线由 InProcessCoreEventConsumerHostedService 直接入库），
// 仪表盘状态使用本地模式实现（恒在线文案，不探测 Core）。
var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aitool.db");
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? $"Data Source={Path.GetFullPath(dbPath)}";
builder.AddAdminHostServices(connectionString, enableCrossHostServices: false);

// Core 代理面：转发/并发/熔断/追踪/事件总线；DB 配置模式（无配置快照），
// 事件不经磁盘 spool（进程内消费者直接消化）。
builder.Services.AddProxyRuntimeInfrastructure(
    builder.Configuration.GetSection(ProxyForwardingOptions.SectionName),
    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "core-runtime", "spool"),
    useCoreRuntimeConfigProviderForCache: false,
    enableEventSpooling: false);
// Core 独有服务（OAuth 刷新链/事件发布器/诊断抓包/内存维护）。
builder.AddCoreProxyHostServices();
// AllInOne：DB-backed 配置提供器（满足 Core 凭证刷新引擎的快照读写面，元数据缓存仍走 DB 模式）。
builder.Services.AddSingleton<AITool.Application.CoreRuntime.ICoreRuntimeConfigProvider, DbBackedRuntimeConfigProvider>();

// 事件进程内消费者：总线 → CoreEventPullService.ProcessEnvelopesAsync（与分离形态同消费逻辑）。
builder.Services.AddHostedService<InProcessCoreEventConsumerHostedService>();

var app = builder.Build();

// 代理事件接线：追踪完成/熔断事件 → Core 事件总线（AllInOne 由进程内消费者入库）。

// ===================== 管道 =====================
// 全局异常处理：捕获未处理异常并记录详细日志，返回统一 JSON 错误响应。
app.UseGlobalExceptionHandler(app.Environment);

// 响应压缩必须在其他产生响应的中间件（静态文件、MVC）之前注册。
app.UseResponseCompression();

// 静态文件（前端构建产物，复用 Admin 的 wwwroot 输出）。
if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = ctx =>
        {
            if (string.Equals(ctx.File.Name, "index.html", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Context.Response.Headers.CacheControl = "no-cache";
                return;
            }

            ctx.Context.Response.Headers.CacheControl = "public, max-age=86400";
        }
    });
}

// Swagger UI：由配置控制（默认开；Testing 下不注册 Swagger 服务，需同步跳过）。
if (!app.Environment.IsEnvironment("Testing") && (builder.Configuration.GetValue<bool?>("Swagger:Enabled") ?? true))
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "AI Tool API v1");
        options.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.None);
        options.DefaultModelsExpandDepth(-1);
    });
}

// 认证与授权（与 Admin 宿主同款 JWT 管道；代理端点 /v1/* 不走 ASP.NET 认证）。
app.UseAuthentication();
app.UseAuthorization();

// 健康检查端点匿名放行。
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.MapControllers();

// SPA fallback：非 /api、非 /v1、非 /health 的请求返回前端 index.html。
if (!app.Environment.IsEnvironment("Testing"))
{
    app.MapFallbackToFile("index.html", new StaticFileOptions
    {
        OnPrepareResponse = ctx =>
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
    });
}

startupLogger.Info("AI Tool AllInOne 宿主启动完成。Version={Version}, Environment={Environment}, Port={Port}", applicationVersion, app.Environment.EnvironmentName, serverPort);
Console.WriteLine($"AI Tool AllInOne 已启动：http://127.0.0.1:{serverPort}");

app.Run();

static string ReadApplicationVersion()
{
    var assembly = typeof(Program).Assembly;
    var infoVersionAttr = assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault();
    if (infoVersionAttr is not null)
    {
        var clean = infoVersionAttr.InformationalVersion.Split('+')[0].Trim();
        if (!string.IsNullOrWhiteSpace(clean))
        {
            return clean;
        }
    }

    return typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0.0";
}

static DateTimeOffset? ReadBuildTimestamp()
{
    var attr = typeof(Program).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>()
        .FirstOrDefault(a => string.Equals(a.Key, "BuildTimestamp", StringComparison.OrdinalIgnoreCase));
    if (attr is null || string.IsNullOrWhiteSpace(attr.Value))
    {
        return null;
    }

    return DateTimeOffset.TryParse(attr.Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var ts)
        ? ts
        : null;
}

/// <summary>
/// 程序入口。
/// </summary>
public partial class Program;