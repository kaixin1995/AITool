using AITool.Application.Proxy;
using AITool.Core.Services;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.DependencyInjection;
using AITool.Infrastructure.Hosting;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using NLog;
using NLog.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Host.UseNLog();

var startupLogger = LogManager.GetLogger("Startup");

// Core 宿主版本号，与 Web 宿主区分。
var applicationVersion = "1.0.1.4-core";
builder.Services.AddSingleton(new AppVersionInfo(applicationVersion));

// Core 宿主默认监听 5029 端口（代理主端口），与 Admin 的 5030 端口分开。
var serverPort = builder.Configuration.GetValue<int?>("CoreServer:Port") ?? builder.Configuration.GetValue<int?>("Server:Port") ?? 5029;
builder.WebHost.UseUrls($"http://0.0.0.0:{serverPort}");

// 注册所有宿主共享的基础设施：控制器、内存缓存、异常过滤器、对话日志存储。
var conversationLogRootPath = builder.Environment.IsEnvironment("Testing")
    ? Path.Combine(Path.GetTempPath(), $"aitool-conversation-logs-{Guid.NewGuid():N}")
    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "conversation-logs");
builder.Services.AddCommonInfrastructure(conversationLogRootPath);

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

// 注册开发者调用追踪查询服务（Core 独有，用于管理端点查询追踪记录）。
builder.Services.AddSingleton<DeveloperInvocationTraceQueryService>();

// 注册开发者追踪事件发布器，当追踪完成时将摘要发布到 Core 事件总线（Core 独有）。
builder.Services.AddSingleton<CoreDeveloperTraceEventPublisher>();

// 注册路由回退事件发布器，当代理请求在路由间回退时发布 route-fallback 事件（Core 独有）。
builder.Services.AddSingleton<CoreRouteFallbackEventPublisher>();

// 注册熔断状态变更事件发布器，当路由因连续失败达到阈值被首次熔断时发布 circuit-breaker 事件（Core 独有）。
builder.Services.AddSingleton<CoreCircuitBreakerEventPublisher>();

var app = builder.Build();

// 将开发者追踪存储的完成事件连接到事件发布器。
// Store 的 OnTraceCompleted 事件在追踪记录完成时触发，
// Publisher 接收后异步发布 developer-trace 事件到 Core 事件总线。
// 使用 fire-and-forget 模式，发布失败不影响代理主流程。
{
    var traceStore = app.Services.GetRequiredService<DeveloperInvocationTraceStore>();
    var tracePublisher = app.Services.GetRequiredService<CoreDeveloperTraceEventPublisher>();
    var tracePublishLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CoreDeveloperTraceEventPublish");
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

// 全局异常处理。
if (!app.Environment.IsEnvironment("Testing"))
{
    app.UseExceptionHandler(exceptionApp =>
    {
        exceptionApp.Run(async context =>
        {
            var feature = context.Features.Get<IExceptionHandlerFeature>();
            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            if (feature?.Error is OperationCanceledException)
            {
                return;
            }

            if (feature?.Error is not null)
            {
                var requestBody = await RequestBodyReader.TryReadRequestBodySafelyAsync(context.Request, context.RequestAborted);

                logger.LogError(feature.Error,
                    "未处理异常\nPath={Path}\nMethod={Method}\nTraceId={TraceId}\nQueryString={QueryString}\nRequestBody={RequestBody}",
                    context.Request.Path,
                    context.Request.Method,
                    context.TraceIdentifier,
                    context.Request.QueryString.HasValue ? context.Request.QueryString.Value : string.Empty,
                    HttpLogFormatter.FormatBody(requestBody));
            }

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsJsonAsync(new { message = "服务器内部异常" });
            }
        });
    });
}

// 启用 CORS，确保 Admin 宿主的前端页面可以跨域调用 Core 代理端点。
// 必须在 MapControllers 之前注册，否则 CORS 头不会被写入响应。
app.UseCors("AdminCors");

// Core 宿主仅映射 API 控制器，不映射 Razor Pages。
// 代理端点 /v1/* 和 Core 管理端点 /api/core/* 由控制器提供。
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapControllers();

app.Run();

/// <summary>
/// 程序入口。
/// </summary>
public partial class Program;
