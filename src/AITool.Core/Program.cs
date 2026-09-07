using AITool.Application.Proxy;
using AITool.Core;
using AITool.Core.Services;
using AITool.Infrastructure.Common;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.DependencyInjection;
using AITool.Infrastructure.Hosting;
using AITool.Infrastructure.Proxy;
using NLog;
using NLog.Web;

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

// Core 宿主版本号：优先读程序集元数据，编译时间从 AssemblyMetadata "BuildTimestamp" 读取（构建期注入）。
var applicationVersion = ReadApplicationVersion();
var buildTime = ReadBuildTimestamp() ?? DateTimeOffset.UtcNow;
builder.Services.AddSingleton(new AppVersionInfo(applicationVersion, buildTime));

// Core 宿主默认监听 5029 端口（代理主端口），与 Admin 的 5030 端口分开。
var serverPort = builder.Configuration.GetValue<int?>("CoreServer:Port") ?? builder.Configuration.GetValue<int?>("Server:Port") ?? 5029;
builder.WebHost.UseUrls($"http://0.0.0.0:{serverPort}");

// 配置 Kestrel 连接与请求体限制，确保代理大请求体（长对话、base64 图片）和可预测的并发行为。
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxConcurrentConnections = 1000;
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(130);
    // 代理请求体可能很大（含图片、长上下文），不限制请求体大小。
    options.Limits.MaxRequestBodySize = null;
});

// 注册所有宿主共享的基础设施：控制器、内存缓存、异常过滤器、
// 流式增强默认配置（传入配置使 SseHeartbeatSeconds/StreamResume* 键生效）。
builder.Services.AddCommonInfrastructure(builder.Configuration);

// 注册 CORS 策略，允许 Admin 宿主（5030）的前端 JavaScript 跨域调用 Core 代理端点。
// 双宿主部署时 Admin 页面和 Core API 分属不同端口，浏览器需要 CORS 头才能正常通信。
builder.Services.AddCors(options =>
{
    options.AddPolicy("AdminCors", policy =>
    {
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? ["http://127.0.0.1:5030", "http://localhost:5030"];
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// Core 宿主使用配置快照，不依赖数据库。
// 运行时配置从 Admin 通过全量同步下发到本地文件，启动时可从 last-good-config 恢复。
builder.Services.AddSingleton(new CoreRuntimeConfigFileOptions
{
    FilePath = builder.Environment.IsEnvironment("Testing")
        ? Path.Combine(Path.GetTempPath(), $"aitool-core-runtime-config-{Guid.NewGuid():N}.json")
        : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "core-runtime", "last-good-config.json")
});
builder.Services.AddSingleton<CoreRuntimeConfigProvider>();
builder.Services.AddSingleton<AITool.Application.CoreRuntime.ICoreRuntimeConfigProvider>(sp => sp.GetRequiredService<CoreRuntimeConfigProvider>());

// SSE 心跳与可恢复缓冲选项由 AddCommonInfrastructure(builder.Configuration) 注册
// （读取 ProxyForwarding:SseHeartbeatSeconds / StreamResume* 配置键）。

// 注册代理运行时核心链路服务：代理转发、并发控制、熔断、事件总线、批处理写入器等。
// Core 宿主传入 useCoreRuntimeConfigProviderForCache: true，使元数据缓存优先从配置快照读取。
var coreEventSpoolRootPath = builder.Environment.IsEnvironment("Testing")
    ? Path.Combine(Path.GetTempPath(), $"aitool-core-event-spool-{Guid.NewGuid():N}")
    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "core-runtime", "spool");
builder.Services.AddProxyRuntimeInfrastructure(
    builder.Configuration.GetSection(ProxyForwardingOptions.SectionName),
    coreEventSpoolRootPath,
    useCoreRuntimeConfigProviderForCache: true);

// 注册并发控制查询服务（Core 独有，用于管理端点查询当前并发状态）。
// Core 宿主独有服务注册（抽取至 CoreProgramServices，供 AllInOne 复用）。
builder.AddCoreProxyHostServices();

var app = builder.Build();

// 代理事件接线（追踪完成/熔断/路由回退 → Core 事件总线），抽取至 CoreProxyEventWiring。
AITool.Core.CoreProxyEventWiring.WireProxyEvents(app.Services);

// Core 宿主启动时尝试从本地文件恢复上次的配置快照。
// 如果没有可恢复配置，保持 not-ready 状态，等待 Admin 下发首个完整快照。
using (var scope = app.Services.CreateScope())
{
    var configProvider = scope.ServiceProvider.GetRequiredService<AITool.Application.CoreRuntime.ICoreRuntimeConfigProvider>();
    if (!await configProvider.TryLoadFromFileAsync())
    {
        startupLogger.Warn("Core 启动时未找到可恢复的 last-good-config，将等待 Admin 下发首个完整配置快照后进入 ready 状态。");
    }
    else
    {
        // 恢复成功后，用快照中的熔断参数初始化 RouteCircuitStateStore，
        // 确保熔断阈值和恢复时长与 Admin 侧配置一致，而非使用构造器默认值。
        var restoredSnapshot = configProvider.GetCurrent();
        if (restoredSnapshot?.RuntimeSettings is not null)
        {
            var circuitStore = scope.ServiceProvider.GetRequiredService<RouteCircuitStateStore>();
            circuitStore.UpdateOptions(
                TimeSpan.FromMinutes(restoredSnapshot.RuntimeSettings.CircuitBreakerRecoveryMinutes),
                restoredSnapshot.RuntimeSettings.CircuitBreakerFailureThreshold);
            startupLogger.Info(
                "已从恢复快照初始化熔断参数：Threshold={Threshold}, RecoveryMinutes={RecoveryMinutes}",
                restoredSnapshot.RuntimeSettings.CircuitBreakerFailureThreshold,
                restoredSnapshot.RuntimeSettings.CircuitBreakerRecoveryMinutes);
        }
    }
}

startupLogger.Info(
    "Core 宿主启动完成。Version={Version}, Environment={Environment}, Port={Port}",
    applicationVersion,
    app.Environment.EnvironmentName,
    serverPort);
Console.WriteLine($"AI Tool Core 已启动：http://127.0.0.1:{serverPort}");
Console.WriteLine($"AI Tool Core 已启动：http://{LocalIpAddressHelper.GetLocalIpAddress()}:{serverPort}");

// 全局异常处理：捕获未处理异常并记录详细日志，返回统一 JSON 错误响应。
app.UseGlobalExceptionHandler(app.Environment);

// 启用 CORS，确保 Admin 宿主的前端页面可以跨域调用 Core 代理端点。
// 必须在 MapControllers 之前注册，否则 CORS 头不会被写入响应。
app.UseCors("AdminCors");

// 跨宿主共享密钥鉴权：/api/core/* 管理端点要求 X-Core-Auth（Testing 环境放行；
// 未配置密钥时退化为仅本机回环可访问）。/v1/*、/health 不经过本中间件。
app.UseMiddleware<AITool.Infrastructure.Security.CoreApiAuthMiddleware>();

// Core 宿主仅映射 API 控制器，不映射 Razor Pages。
// 代理端点 /v1/* 和 Core 管理端点 /api/core/* 由控制器提供。
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();

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
