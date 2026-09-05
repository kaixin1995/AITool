using AITool.Application.Proxy;
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

// 注册所有宿主共享的基础设施：控制器、内存缓存、异常过滤器。
builder.Services.AddCommonInfrastructure();

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

var app = builder.Build();

// 将开发者追踪存储的完成事件连接到事件发布器。
// Store 的 OnTraceCompleted 事件在追踪记录完成时触发，
// Publisher 接收后异步发布 developer-trace 事件到 Core 事件总线。
// 使用 fire-and-forget 模式，发布失败不影响代理主流程。
{
    var traceStore = app.Services.GetRequiredService<DeveloperInvocationTraceStore>();
    var tracePublisher = app.Services.GetRequiredService<CoreUnifiedProxyEventPublisher>();
    var tracePublishLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CoreUnifiedProxyEventPublish");
    traceStore.OnTraceCompleted += entry =>
    {
        // fire-and-forget：追踪事件发布是辅助链路，不应阻塞代理主流程
        _ = Task.Run(async () =>
        {
            try
            {
                await tracePublisher.PublishAsync(entry);
            }
            catch (Exception ex)
            {
                tracePublishLogger.LogWarning(ex, "发布开发者追踪事件失败，不影响代理主流程。TraceId={TraceId}", entry.TraceId);
            }
        });
    };
}

// 将熔断状态存储的首次熔断事件连接到事件发布器。
// RouteCircuitStateStore 的 OnCircuitOpened 事件在路由首次触发熔断时触发，
// Publisher 接收后异步发布 circuit-breaker 事件到 Core 事件总线。
// 使用 fire-and-forget 模式，发布失败不影响代理主流程。
{
    var circuitStore = app.Services.GetRequiredService<RouteCircuitStateStore>();
    var circuitPublisher = app.Services.GetRequiredService<CoreCircuitBreakerEventPublisher>();
    var circuitPublishLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CoreCircuitBreakerEventPublish");
    circuitStore.OnCircuitOpened += (sender, args) =>
    {
        // fire-and-forget：熔断事件发布是辅助链路，不应阻塞代理主流程
        _ = Task.Run(async () =>
        {
            try
            {
                await circuitPublisher.PublishAsync(args);
            }
            catch (Exception ex)
            {
                circuitPublishLogger.LogWarning(ex, "发布熔断状态变更事件失败，不影响代理主流程。RouteId={RouteId}", args.RouteId);
            }
        });
    };
}

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
