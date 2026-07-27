using System.Data.Common;
using System.Net.Http;
using AITool.Application.Codex;
using AITool.Application.Common;
using AITool.Application.Operations;
using AITool.Application.Proxy;
using AITool.Application.SiteCatalog;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Codex;
using AITool.Infrastructure.Health;
using AITool.Infrastructure.Operations;
using AITool.Infrastructure.OpenAI;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Infrastructure.Retention;
using AITool.Infrastructure.Scheduling;
using AITool.Web.Services;
using Hangfire;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using SqlSugar;
using NLog;
using NLog.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Host.UseNLog();

var startupLogger = LogManager.GetLogger("Startup");

var applicationVersion = "1.0.1.7";
builder.Services.AddSingleton(new AppVersionInfo(applicationVersion));

var serverPort = builder.Configuration.GetValue<int?>("Server:Port") ?? 5029;
builder.WebHost.UseUrls($"http://0.0.0.0:{serverPort}");

// 配置 Kestrel 连接与请求体限制，确保代理大请求体（长对话、base64 图片）和可预测的并发行为。
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxConcurrentConnections = 500;
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(130);
    options.Limits.MaxRequestBodySize = 100 * 1024 * 1024; // 100MB，覆盖多模态 base64 图片同时防止超大请求 OOM
});

// 启用响应压缩，压缩 API JSON 响应和静态资源。
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});

// 注册 Razor Pages，作为管理后台的页面框架。
builder.Services.AddRazorPages();

// 注册 API 控制器，用于代理转发端点。
builder.Services.AddControllers(options =>
{
    options.Filters.Add<HttpExceptionLoggingFilter>();
});
builder.Services.AddMemoryCache();
builder.Services.AddScoped<HttpExceptionLoggingFilter>();

// 注册 JWT 配置选项与 token 服务。
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.AddSingleton<JwtTokenService>();

// 认证：JWT 为主（/api/* 用 Bearer token，跨端通用），Cookie 为辅（现有 Razor Pages + Login 页）。
// 用 PolicyScheme 作为默认 scheme，按请求路径分派：/api/* 走 JWT，其余走 Cookie。
// 大爆炸切换移除 Razor Pages 后，Cookie 方案可一并移除。
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = "AIToolPolicy";
        options.DefaultChallengeScheme = "AIToolPolicy";
        options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddPolicyScheme("AIToolPolicy", "AITool 策略认证", options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            // /api/* 用 JWT；其余（Razor Pages、Hangfire）用 Cookie。
            return context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
                ? JwtBearerDefaults.AuthenticationScheme
                : CookieAuthenticationDefaults.AuthenticationScheme;
        };
    })
    .AddJwtBearer(options =>
    {
        var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                System.Text.Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
        // /api/* 未携带有效 token 时统一返回 401 JSON，不重定向（前端按 401 处理）。
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
    })
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
builder.Services.AddSingleton<AdminAuthService>();

// 数据库文件放在软件根目录下。
var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aitool.db");
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? $"Data Source={Path.GetFullPath(dbPath)}";
// 注册 SqlSugar 数据访问层（SqlSugarScope 单例 + AppDbContext 适配），连接级 PRAGMA 与原 EF 配置一致。
builder.Services.AddSqlSugar(connectionString);

// 注册代理转发配置，统一控制单路由超时和失败重试策略。
builder.Services.Configure<ProxyForwardingOptions>(
    builder.Configuration.GetSection(ProxyForwardingOptions.SectionName));

// 注册站点目录客户端，用于拉取远程站点模型列表。
builder.Services.AddHttpClient<ISiteCatalogClient, OpenAiSiteCatalogClient>();

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

// 注册代理主入口实体配置，配置 SocketsHttpHandler 连接池提高并发能力。
builder.Services.AddHttpClient<IProxyForwardService, ProxyForwardService>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 200,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30)
    });
builder.Services.AddScoped<ModelHealthRequestService>();

// 注册使用日志服务，记录每次代理调用的 Token 用量。
builder.Services.AddSingleton<ProxyUsageLogBatchWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProxyUsageLogBatchWriter>());
// Site 使用时间内存映射：日志入队时增量更新，Codex 巡检读它判断账号是否被使用，避免回查 DB。
builder.Services.AddSingleton<SiteUsageTracker>();
var conversationLogRootPath = builder.Environment.IsEnvironment("Testing")
    ? Path.Combine(Path.GetTempPath(), $"aitool-conversation-logs-{Guid.NewGuid():N}")
    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "conversation-logs");
builder.Services.AddSingleton(new AITool.Infrastructure.Conversations.ConversationLogFileOptions
{
    RootPath = conversationLogRootPath
});
builder.Services.AddSingleton<AITool.Application.Conversations.IConversationLogStore, AITool.Infrastructure.Conversations.FileConversationLogStore>();
builder.Services.AddSingleton<AITool.Infrastructure.Conversations.ConversationLogBatchWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AITool.Infrastructure.Conversations.ConversationLogBatchWriter>());
// 定期压缩 LOH，回收大对象碎片，避免代理转发产生的大字符串碎片导致工作集居高不下。
builder.Services.AddHostedService<MemoryMaintenanceService>();
// 周期刷新 Codex 账号 OAuth token，写回隐藏 Site.ApiKey 并失效路由缓存。
builder.Services.AddHostedService<CodexTokenRefreshService>();
// 周期恢复冷却到期的 Codex 账号（清除冷却，恢复 Site，若未被手动禁用）。
builder.Services.AddHostedService<CodexCooldownRecoveryService>();
builder.Services.AddSingleton<DeveloperInvocationTraceStore>();
builder.Services.AddSingleton<ModelConcurrencyLimiter>();
builder.Services.AddSingleton<IUsageLogService, UsageLogService>();
builder.Services.AddSingleton<AITool.Application.Conversations.IConversationLogService, AITool.Infrastructure.Conversations.ConversationLogService>();
builder.Services.AddSingleton<AITool.Infrastructure.Conversations.ConversationExtractionService>();

// 注册熔断状态存储，跟踪因连续失败而被临时屏蔽的站点。
builder.Services.AddSingleton<RouteCircuitStateStore>();
builder.Services.AddSingleton<ProxyRequestMetadataCache>();
builder.Services.AddSingleton<ModelVendorCatalogService>();

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

// 注册日志保留策略服务，定时清理过期日志。
builder.Services.AddScoped<ILogRetentionService, LogRetentionService>();

// 注册系统运行时设置服务，统一管理持久化的超时、重试和日志保留配置。
builder.Services.AddScoped<ISystemRuntimeSettingsService, SystemRuntimeSettingsService>();

// 注册 Hangfire 检测调度器。
builder.Services.AddSingleton<HangfireDetectionScheduler>();
builder.Services.AddSingleton<AnalyticsBackgroundQueryExecutor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AnalyticsBackgroundQueryExecutor>());

// 注册 Hangfire 内存存储与仪表盘。
builder.Services.AddHangfire(config => config
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseInMemoryStorage());
builder.Services.AddHangfireServer();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var sqlSugarClient = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
    // CodeFirst 建表 + 补齐历史库缺失列（差量更新，只增不删）+ 持久化 PRAGMA（WAL、synchronous）。
    // 替代原 EF 的 EnsureCreated + 手写 ALTER TABLE 升级脚本：SqlSugar 的 InitTables 会自动补齐缺失列。
    SqlSugarSetup.InitializeDatabase(sqlSugarClient);

    // 预热 SiteUsageTracker：从 DB 读每个 Site 最近一次使用时间，避免重启后历史丢失。
    var siteUsageTracker = scope.ServiceProvider.GetRequiredService<SiteUsageTracker>();
    var warmupDbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await siteUsageTracker.WarmupAsync(warmupDbContext);

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

    // 预热代理热路径缓存（运行时设置），避免首个代理请求触发 DB 往返。测试环境跳过。
    var env = scope.ServiceProvider.GetService<IHostEnvironment>();
    if (env is null || !env.IsEnvironment("Testing"))
    {
        try
        {
            var cache = scope.ServiceProvider.GetService<ProxyRequestMetadataCache>();
            if (cache is not null)
            {
                await cache.GetRuntimeSettingsAsync(default);
            }
        }
        catch { }
    }

    var settingsService = scope.ServiceProvider.GetRequiredService<ISystemRuntimeSettingsService>();
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
Console.WriteLine($"AI Tool 已启动：http://{GetLocalIpAddress()}:{serverPort}");

// 启用静态文件服务，提供 wwwroot 下的 CSS/JS 等资源。
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

app.UseResponseCompression();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers["Cache-Control"] = "public, max-age=86400";
    }
});
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (app.Environment.IsEnvironment("Testing") || !IsAdminRequest(context.Request) || IsLoginPageRequest(context.Request))
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

    if (IsAdminApiRequest(context.Request))
    {
        // 后台 API 未认证统一返回 ApiResponse 格式的 JSON（与 JWT OnChallenge 一致），前端按 401 + errorCode 处理。
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new
        {
            success = false,
            message = "未登录或登录已过期，请重新登录",
            errorCode = "unauthenticated"
        });
        return;
    }

    if (IsHangfireRequest(context.Request))
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
/// 判断是否为后台请求。
/// </summary>
static bool IsAdminRequest(HttpRequest request)
{
    return IsAdminPageRequest(request) || IsAdminApiRequest(request) || IsHangfireRequest(request);
}

/// <summary>
/// 判断是否为后台页面请求。
/// </summary>
static bool IsAdminPageRequest(HttpRequest request)
{
    var path = request.Path;
    return path == "/" || path.StartsWithSegments("/Admin", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 判断是否为登录页请求。
/// </summary>
static bool IsLoginPageRequest(HttpRequest request)
{
    return request.Path == "/Login";
}

/// <summary>
/// 判断是否为后台接口请求。
/// </summary>
static bool IsAdminApiRequest(HttpRequest request)
{
    return request.Path.StartsWithSegments("/api/admin", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 判断是否为 Hangfire 请求。
/// </summary>
static bool IsHangfireRequest(HttpRequest request)
{
    return request.Path.StartsWithSegments("/hangfire", StringComparison.OrdinalIgnoreCase);
}

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
