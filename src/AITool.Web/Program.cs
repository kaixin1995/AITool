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

var serverPort = builder.Configuration.GetValue<int?>("Server:Port") ?? 15029;
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

// 注册 API 控制器，用于代理转发端点 + 后台管理 API。
builder.Services.AddControllers(options =>
{
    options.Filters.Add<HttpExceptionLoggingFilter>();
});
builder.Services.AddMemoryCache();
builder.Services.AddScoped<HttpExceptionLoggingFilter>();

// 注册 JWT 配置选项与 token 服务。
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.AddScoped<JwtTokenService>();
// LoginRateLimitService: 登录暴力破解防护（IP 失败计数 + 锁定）
builder.Services.AddSingleton<LoginRateLimitService>();

// 认证：纯 JWT（SPA 分离后不再需要 Cookie）。
// /api/* 用 Bearer token 验证；代理端点 /v1/* 不走 ASP.NET 认证（自己用 AccessKey 校验）。
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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
builder.Services.AddSingleton<AdminAuthService>();

// Swagger：可通过 appsettings.json 的 Swagger:Enabled 配置控制。
// 未配置时默认所有环境可用，可设置为 false 关闭。
// Testing 环境始终关闭，避免集成测试注入 Swagger 服务。
var swaggerEnabled = !builder.Environment.IsEnvironment("Testing")
    && (builder.Configuration.GetValue<bool?>("Swagger:Enabled") ?? true);
if (swaggerEnabled)
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
        {
            Title = "AI Tool API",
            Version = "v1",
            Description = "AI-Tool 后台管理与代理 API 文档"
        });

        // 集成 JWT Bearer 认证：Swagger UI 顶部出现 Authorize 按钮，
        // 粘贴 access token 后调测受保护接口自动带 Bearer header。
        options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            Description = "粘贴 access token（不含 'Bearer ' 前缀）。登录后从 /api/auth/login 响应获取。"
        });
        options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
        {
            {
                new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                {
                    Reference = new Microsoft.OpenApi.Models.OpenApiReference
                    {
                        Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                        Id = "Bearer"
                    }
                },
                Array.Empty<string>()
            }
        });

        // 排除代理转发端点（/v1/*）：它们是 SSE 流式转发，请求体任意、响应是流，
        // Swagger UI 无法有效测试，且会污染文档列表。
        options.DocInclusionPredicate((docName, apiDesc) =>
        {
            apiDesc.ActionDescriptor.RouteValues.TryGetValue("controller", out var controller);
            return controller != "OpenAiProxy" && controller != "AnthropicProxy";
        });

        // 注入 XML 注释：控制器自身的注释 + Application/Infrastructure 层的 DTO 注释，
        // 让 Swagger 展示接口描述。XML 文件路径基于对应程序集的 dll 路径推断。
        var xmlFiles = new[]
        {
            // Web：控制器注释
            typeof(Program).Assembly.Location.Replace(".dll", ".xml"),
            // Application：DTO / Command / 操作类注释
            typeof(AITool.Application.Operations.ISystemRuntimeSettingsService).Assembly.Location.Replace(".dll", ".xml"),
            // Infrastructure：领域实体与基础设施类型注释
            typeof(AITool.Infrastructure.Persistence.AppDbContext).Assembly.Location.Replace(".dll", ".xml")
        };
        foreach (var path in xmlFiles.Where(File.Exists))
        {
            options.IncludeXmlComments(path);
        }
    });
}

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
// 站点密钥选择器：模型目录拉取、健康检测等站点级操作取用活动密钥。
builder.Services.AddScoped<AITool.Infrastructure.Sites.SiteKeySelector>();

// 注册使用日志服务，记录每次代理调用的 Token 用量。
builder.Services.AddSingleton<ProxyUsageLogBatchWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProxyUsageLogBatchWriter>());
// Site 使用时间内存映射：日志入队时增量更新，Codex 巡检读它判断账号是否被使用，避免回查 DB。
builder.Services.AddSingleton<SiteUsageTracker>();
// 定期压缩 LOH，回收大对象碎片，避免代理转发产生的大字符串碎片导致工作集居高不下。
builder.Services.AddHostedService<MemoryMaintenanceService>();
// 周期刷新 Codex 账号 OAuth token，写回隐藏 Site.ApiKey 并失效路由缓存。
builder.Services.AddHostedService<CodexTokenRefreshService>();
// 周期恢复冷却到期的 Codex 账号（清除冷却，恢复 Site，若未被手动禁用）。
builder.Services.AddHostedService<CodexCooldownRecoveryService>();
builder.Services.AddSingleton<DeveloperInvocationTraceStore>();
builder.Services.AddSingleton<ModelConcurrencyLimiter>();
builder.Services.AddSingleton<IUsageLogService, UsageLogService>();

// 注册熔断状态存储，跟踪因连续失败而被临时屏蔽的站点。
builder.Services.AddSingleton<RouteCircuitStateStore>();
builder.Services.AddSingleton<ProxyRequestMetadataCache>();
builder.Services.AddSingleton<ModelVendorCatalogService>();

// 注册 Codex 账号供给相关服务（站点级联删除工具 + 账号工厂）。
builder.Services.AddScoped<SiteCascadeDeleter>();
builder.Services.AddScoped<CodexAccountProvisioner>();
// 实时代理命中 Codex 上游 401 时立即刷新凭证并同步隐藏站点。
builder.Services.AddScoped<CodexCredentialRefreshService>();
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
        // 已 hash 的资源（assets/）长期缓存；index.html 不缓存，确保发版即时生效。
        var path = ctx.Context.Request.Path.Value ?? string.Empty;
        ctx.Context.Response.Headers["Cache-Control"] = path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase)
            ? "public, max-age=31536000, immutable"
            : "no-cache";
    }
});
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();

// Swagger UI：由 swaggerEnabled 控制（配置 Swagger:Enabled，默认 true）。
// 必须位于 SPA fallback（MapFallbackToFile）之前，否则 /swagger 会被当作前端路由返回 index.html。
if (swaggerEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "AI Tool API v1");
        options.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.None); // 默认折叠分组
        options.DefaultModelsExpandDepth(-1); // 隐藏 schema 模型区，减少冗余
    });
}

app.Use(async (context, next) =>
{
    // SPA 分离后：只有 /api/admin/* 和 /hangfire 需要服务端鉴权拦截。
    // 其余路径（/sites、/login 等前端路由）交给 SPA fallback + 前端 router 处理。
    if (app.Environment.IsEnvironment("Testing") || (!IsAdminApiRequest(context.Request) && !IsHangfireRequest(context.Request)))
    {
        await next();
        return;
    }

    if (context.User.Identity?.IsAuthenticated == true)
    {
        await next();
        return;
    }

    // 未认证的后台 API：返回 401 JSON（前端拦截器统一处理）。
    if (IsAdminApiRequest(context.Request))
    {
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

    // 未认证的 Hangfire 仪表盘：重定向到前端登录页。
    var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
    context.Response.Redirect($"/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
});

// 映射健康检查端点，作为集成测试的验证入口。
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("Health");

// 启用 Hangfire 仪表盘，仅限本地访问。
app.UseHangfireDashboard("/hangfire");

// 注册日志清理定时任务，每天凌晨 3 点执行。
RecurringJob.AddOrUpdate<ILogRetentionService>(
    "log-retention-prune",
    svc => svc.PruneAsync(CancellationToken.None),
    "0 3 * * *");

// 映射 API 控制器路由，用于代理转发端点 + 后台管理 API。
app.MapControllers();

// SPA fallback：非 /api、/v1、/health、/hangfire 的请求全部返回 index.html，
// 交给前端 Vue Router 处理（history 模式）。MapFallbackToFile 会自动排除已映射的端点。
if (!app.Environment.IsEnvironment("Testing"))
{
    app.MapFallbackToFile("index.html");
}

app.Run();

/// <summary>
/// 判断是否为后台接口请求（/api/admin 前缀）。
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
