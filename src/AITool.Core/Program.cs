using AITool.Application.Common;
using AITool.Application.Conversations;
using AITool.Application.Operations;
using AITool.Application.Proxy;
using AITool.Application.UsageLogs;
using AITool.Core.Services;
using AITool.Infrastructure.Conversations;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Hosting;
using AITool.Infrastructure.OpenAI;
using AITool.Infrastructure.Operations;
using AITool.Infrastructure.Proxy;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
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

// 注册 API 控制器，用于代理转发端点和 Core 管理端点。
// Core 宿主不注册 Razor Pages，不提供任何页面。
builder.Services.AddControllers(options =>
{
    options.Filters.Add<HttpExceptionLoggingFilter>();
});
builder.Services.AddMemoryCache();
builder.Services.AddScoped<HttpExceptionLoggingFilter>();

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

// 注册代理转发配置，统一控制单路由超时和失败重试策略。
builder.Services.Configure<ProxyForwardingOptions>(
    builder.Configuration.GetSection(ProxyForwardingOptions.SectionName));

// 注册代理主入口实体配置。
builder.Services.AddHttpClient<IProxyForwardService, ProxyForwardService>();

// 注册代理请求元数据缓存，缓存路由、密钥、并发限制等运行时数据。
// Core 宿主的缓存数据来源是 Admin 下发的配置快照，而非直接查询数据库。
// 显式传入 ICoreRuntimeConfigProvider，使缓存方法在快照可用时优先从快照读取。
builder.Services.AddSingleton<ProxyRequestMetadataCache>(sp =>
{
    var memoryCache = sp.GetRequiredService<IMemoryCache>();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    var configProvider = sp.GetRequiredService<AITool.Application.CoreRuntime.ICoreRuntimeConfigProvider>();
    return new ProxyRequestMetadataCache(memoryCache, scopeFactory, configProvider);
});
builder.Services.AddSingleton<AdminQueryMetadataService>();

// 注册并发控制与查询服务。
builder.Services.AddSingleton<ModelConcurrencyLimiter>();
builder.Services.AddSingleton<ModelConcurrencyQueryService>();

// 注册熔断状态存储，跟踪因连续失败而被临时屏蔽的站点。
builder.Services.AddSingleton<RouteCircuitStateStore>();

// 注册开发者调用追踪存储（代理运行时写入端）。
builder.Services.AddSingleton<DeveloperInvocationTraceStore>();
builder.Services.AddSingleton<DeveloperInvocationTraceQueryService>();

// 注册开发者追踪事件发布器，当追踪完成时将摘要发布到 Core 事件总线。
builder.Services.AddSingleton<CoreDeveloperTraceEventPublisher>();

// 注册路由回退事件发布器，当代理请求在路由间回退时发布 route-fallback 事件。
builder.Services.AddSingleton<CoreRouteFallbackEventPublisher>();

// 注册配置变更应用事件发布器，配置成功应用后向事件总线发送确认通知。
builder.Services.AddSingleton<CoreConfigAppliedEventPublisher>();

// 注册事件序列、事件总线与 spool，支撑 Core -> Admin 可靠事件推送。
builder.Services.AddSingleton<CoreEventSequenceProvider>();
builder.Services.AddSingleton<CoreAdminEventBus>();
builder.Services.AddSingleton(new CoreEventSpoolOptions
{
    RootPath = builder.Environment.IsEnvironment("Testing")
        ? Path.Combine(Path.GetTempPath(), $"aitool-core-event-spool-{Guid.NewGuid():N}")
        : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "core-runtime", "spool")
});
builder.Services.AddSingleton<CoreEventSpoolStore>();
builder.Services.AddHostedService<CoreEventSpoolBackgroundService>();

// 注册使用日志事件发布器和批处理写入器。
// Core 宿主发布事件到总线，后台写入器批量持久化到数据库。
builder.Services.AddSingleton<CoreUsageLogEventPublisher>();
builder.Services.AddSingleton<ProxyUsageLogBatchWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProxyUsageLogBatchWriter>());

// 注册对话日志批处理写入器和文件存储。
var conversationLogRootPath = builder.Environment.IsEnvironment("Testing")
    ? Path.Combine(Path.GetTempPath(), $"aitool-conversation-logs-{Guid.NewGuid():N}")
    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "conversation-logs");
builder.Services.AddSingleton(new ConversationLogFileOptions
{
    RootPath = conversationLogRootPath
});
builder.Services.AddSingleton<IConversationLogStore, FileConversationLogStore>();
builder.Services.AddSingleton<ConversationLogBatchWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConversationLogBatchWriter>());
builder.Services.AddSingleton<CoreConversationEventPublisher>();
builder.Services.AddSingleton<IConversationLogService, ConversationLogService>();
builder.Services.AddSingleton<ConversationExtractionService>();

// 注册使用日志服务，记录每次代理调用的 Token 用量。
builder.Services.AddSingleton<IUsageLogService, UsageLogService>();

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
Console.WriteLine($"AI Tool Core 已启动：http://{GetLocalIpAddress()}:{serverPort}");

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
                var requestBody = await TryReadRequestBodySafelyAsync(context.Request, context.RequestAborted);

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
/// 获取本机 IPv4 地址。
/// </summary>
static string GetLocalIpAddress()
{
    try
    {
        var addresses = System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName());
        var ipv4 = addresses.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(x));
        return ipv4?.ToString() ?? "127.0.0.1";
    }
    catch
    {
        return "127.0.0.1";
    }
}

/// <summary>
/// 安全读取请求体。
/// </summary>
static async Task<string> TryReadRequestBodySafelyAsync(HttpRequest request, CancellationToken cancellationToken)
{
    try
    {
        request.EnableBuffering();
        using var reader = new StreamReader(request.Body, leaveOpen: true);
        request.Body.Position = 0;
        var requestBody = await reader.ReadToEndAsync(cancellationToken);
        request.Body.Position = 0;
        return requestBody;
    }
    catch (OperationCanceledException)
    {
        return "<canceled>";
    }
    catch
    {
        return "<unavailable>";
    }
}

/// <summary>
/// 程序入口。
/// </summary>
public partial class Program;
