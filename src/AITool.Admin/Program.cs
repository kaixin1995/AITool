using AppVersionInfo = AITool.Infrastructure.Hosting.AppVersionInfo;
using AITool.Application.Accounts;
using AITool.Application.Codex;
using AITool.Application.Common;
using AITool.Application.Google;
using AITool.Application.Kimi;
using AITool.Application.Pricing;
using AITool.Application.Proxy;
using AITool.Infrastructure.Codex;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.DependencyInjection;
using AITool.Infrastructure.Google;
using AITool.Infrastructure.Hosting;
using AITool.Infrastructure.Kimi;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Pricing;
using AITool.Infrastructure.Security;
using AITool.Infrastructure.Proxy;
using AITool.Infrastructure.Retention;
using AITool.Admin.Services;
using AITool.Admin.Controllers.Proxy;
using AITool.Admin;
using AITool.Infrastructure.Common;
using AITool.Infrastructure.Scheduling;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NLog;
using NLog.Web;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// glibc malloc 调优必须抢在 Kestrel/后台服务产生原生分配之前应用（见 GlibcArenaLimiter 注释）。
// 全部由 appsettings NativeMemory 节配置，换机器部署不依赖任何环境变量。
GlibcArenaLimiter.TryApply(
    builder.Configuration.GetValue("NativeMemory:MallocArenaMax", 2),
    builder.Configuration.GetValue("NativeMemory:MallocTrimThresholdBytes", 64 * 1024),
    builder.Configuration.GetValue("NativeMemory:MallocMmapThresholdBytes", 128 * 1024));

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Host.UseNLog();

var startupLogger = LogManager.GetLogger("Startup");
// 版本号优先从程序集元数据读取（csproj 的 Version 属性），编译时间从 AssemblyMetadata "BuildTimestamp" 读取（构建期注入）。
var applicationVersion = ReadApplicationVersion();
var buildTime = ReadBuildTimestamp() ?? DateTimeOffset.UtcNow;
builder.Services.AddSingleton(new AppVersionInfo(applicationVersion, buildTime));

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

// 全部 Admin 宿主服务注册（抽取至 AdminProgramServices，供 AllInOne 复用）。
var adminDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aitool.db");
var adminConnectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? $"Data Source={Path.GetFullPath(adminDbPath)}";
// 注册所有宿主共享的基础设施（控制器/缓存/异常过滤器/流式增强默认配置）+ Admin 服务段。
builder.Services.AddCommonInfrastructure();
builder.AddAdminHostServices(adminConnectionString);

// 供启动日志与 Swagger 判断复用的配置快照（与 AdminProgramServices 内注册条件保持一致）。
var swaggerEnabled = !builder.Environment.IsEnvironment("Testing")
    && (builder.Configuration.GetValue<bool?>("Swagger:Enabled") ?? true);
var coreBaseUrl = builder.Configuration["CoreServer:BaseUrl"] ?? $"http://127.0.0.1:{builder.Configuration.GetValue<int?>("CoreServer:Port") ?? 5029}/";

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

// Swagger UI：由 swaggerEnabled 控制（配置 Swagger:Enabled，默认 true）。
// 必须位于 SPA fallback（MapFallbackToFile）之前，否则 /swagger 会被当作前端路由返回 index.html。
if (swaggerEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "AI Tool API v1");
        options.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.None);
        options.DefaultModelsExpandDepth(-1); // 隐藏 schema 模型区，减少冗余
    });
}

// 静态文件配置 Cache-Control 头，让浏览器缓存 CSS/JS/图片，重复访问零往返。
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // index.html 必须协商缓存（no-cache）：入口文件引用带内容哈希的 js/css，
        // 若被浏览器强缓存，发布新版本后用户会长时间停留在旧前端（已两次踩坑）。
        if (string.Equals(ctx.File.Name, "index.html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache";
            return;
        }
        // 静态文件带文件指纹（版本号）时缓存 1 天；无指纹时浏览器仍会条件请求。
        ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=86400";
    }
});
app.UseAuthentication();
app.UseAuthorization();

// 映射健康检查端点，作为集成测试的验证入口。
// 健康检查端点必须允许匿名访问（负载均衡/k8s 探针不带 JWT），否则 FallbackPolicy 会返回 401。
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.MapControllers();
// SPA fallback：非 /api、非 /v1 的请求统一返回前端 index.html，由 Vue Router 接管路由。
// wwwroot 静态文件由上方的 UseStaticFiles 提供（Vite 构建产物输出到 Admin/wwwroot）。
// index.html 必须协商缓存（no-cache）：入口文件引用带内容哈希的 js/css，
// 若被浏览器启发式缓存，发布新版本后用户会长时间停留在旧前端（已两次踩坑）。
// 哈希命中的 assets 本身保持默认强缓存语义，不受影响。
app.MapFallbackToFile("index.html", new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.CacheControl = "no-cache";
    }
});
app.Run();

// 版本号优先从程序集元数据（AssemblyInformationalVersion / AssemblyFileVersion / AssemblyVersion）读取（由 csproj 配置）。
static string ReadApplicationVersion()
{
    var assembly = typeof(Program).Assembly;
    var infoVersionAttr = assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault();

    if (!string.IsNullOrWhiteSpace(infoVersionAttr?.InformationalVersion))
    {
        // 去除可能的 git commit hash 后缀 (例如 1.0.1.10+abc1234)
        var cleanVersion = infoVersionAttr.InformationalVersion.Split('+')[0].Trim();
        if (!string.IsNullOrWhiteSpace(cleanVersion))
        {
            return cleanVersion;
        }
    }

    var fileVersionAttr = assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyFileVersionAttribute), false)
        .OfType<System.Reflection.AssemblyFileVersionAttribute>()
        .FirstOrDefault();

    if (!string.IsNullOrWhiteSpace(fileVersionAttr?.Version))
    {
        return fileVersionAttr.Version.Trim();
    }

    var asmVersion = assembly.GetName().Version;
    return asmVersion is not null ? asmVersion.ToString() : "1.0.0.0";
}

// 从主程序集元数据读取编译时间戳（csproj 构建时注入的 AssemblyMetadata "BuildTimestamp"）。
// 单文件/独立发布下程序集无独立 dll 文件，读取文件时间戳会失效，故用元数据方案。
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

public partial class Program;
