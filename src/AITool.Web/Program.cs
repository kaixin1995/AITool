using System.Data.Common;
using System.Net.Http;
using AITool.Application.Accounts;
using AITool.Application.Codex;
using AITool.Application.Common;
using AITool.Application.Google;
using AITool.Application.Kimi;
using AITool.Application.Operations;
using AITool.Application.Pricing;
using AITool.Application.Proxy;
using AITool.Application.SiteCatalog;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Codex;
using AITool.Infrastructure.Common;
using AITool.Infrastructure.Google;
using AITool.Infrastructure.Kimi;
using AITool.Infrastructure.Health;
using AITool.Infrastructure.Operations;
using AITool.Infrastructure.OpenAI;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Pricing;
using AITool.Infrastructure.Proxy;
using AITool.Infrastructure.Retention;
using AITool.Infrastructure.Scheduling;
using AITool.Web.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using SqlSugar;
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

// 版本号优先从程序集元数据（AssemblyInformationalVersion / AssemblyFileVersion / AssemblyVersion）读取（由 csproj 配置）
var applicationVersion = ReadApplicationVersion();
// 编译时间从程序集元数据（AssemblyMetadata）读取，构建时由 csproj 注入。
// 相比读取 dll 文件时间戳，这种方式在单文件/独立发布（Assembly.Location 为空）下依然可用。
var buildTime = ReadBuildTimestamp() ?? DateTimeOffset.UtcNow;
builder.Services.AddSingleton(new AppVersionInfo(applicationVersion, buildTime));

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

// 注册 Codex 上游客户端伪装配置（版本号等），便于不发版调整上游校验参数。
builder.Services.Configure<CodexUpstreamOptions>(
    builder.Configuration.GetSection(CodexUpstreamOptions.SectionName));

// 注册站点目录客户端，用于拉取远程站点模型列表。
builder.Services.AddHttpClient<ISiteCatalogClient, OpenAiSiteCatalogClient>();

// 注册 OAuth 客户端，用于 PKCE 授权、token 交换与刷新（当前提供 Codex 上游实现）。
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

// 注册当前 OAuth 提供程序的额度服务（30s 结果缓存防抖 + single-flight）。
builder.Services.AddHttpClient<CodexQuotaService>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddTransient<ICodexQuotaService>(sp => sp.GetRequiredService<CodexQuotaService>());
builder.Services.AddTransient<IAccountQuotaProvider>(sp => sp.GetRequiredService<CodexQuotaService>());

// 注册 Google OAuth 客户端（Antigravity 客户端身份，授权/交换/刷新/元信息探测）。
builder.Services.AddHttpClient<IGoogleOAuthClient, GoogleOAuthClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
});

// 注册 Google 上游模型清单拉取（Antigravity 走 fetchAvailableModels 动态清单）。
builder.Services.AddHttpClient<IGoogleModelFetcher, GoogleModelFetcher>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
});

// 注册 Google 账号额度服务（Antigravity fetchAvailableModels 每模型剩余比例窗口）。
builder.Services.AddHttpClient<GoogleAccountQuotaService>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddTransient<IAccountQuotaProvider>(sp => sp.GetRequiredService<GoogleAccountQuotaService>());

// 注册 Kimi OAuth 客户端（RFC 8628 设备授权、Token 交换与刷新）。
builder.Services.AddHttpClient<IKimiOAuthClient, KimiOAuthClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
});

// 注册 Kimi 上游模型拉取。
builder.Services.AddHttpClient<IKimiModelFetcher, KimiModelFetcher>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
});

// 注册 Kimi 账号额度查询（GET /coding/v1/usages），并纳入统一多窗口额度巡检。
builder.Services.AddHttpClient<KimiQuotaService>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddTransient<IAccountQuotaProvider>(sp => sp.GetRequiredService<KimiQuotaService>());

// 注册代理主入口实体配置，配置 SocketsHttpHandler 连接池提高并发能力。
// 连接池寿命与站点专属代理客户端（ProxyForwardService）对齐为 15 分钟：过短会在持续负载下频繁重建连接。
builder.Services.AddHttpClient<IProxyForwardService, ProxyForwardService>()
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        MaxConnectionsPerServer = 200,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
    });
    builder.Services.AddScoped<ModelHealthRequestService>();
// 站点密钥选择器：模型目录拉取、健康检测等站点级操作取用活动密钥。
builder.Services.AddScoped<AITool.Infrastructure.Sites.SiteKeySelector>();

// 注册使用日志服务，记录每次代理调用的 Token 用量。
builder.Services.AddSingleton<ProxyUsageLogBatchWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProxyUsageLogBatchWriter>());
// Site 使用时间内存映射：日志入队时增量更新，账号额度巡检读它判断账号是否被使用，避免回查 DB。
builder.Services.AddSingleton<SiteUsageTracker>();
// 定期压缩 LOH，回收大对象碎片，避免代理转发产生的大字符串碎片导致工作集居高不下。
builder.Services.AddHostedService<MemoryMaintenanceService>();
// 周期刷新当前 OAuth 提供程序账号 token，写回隐藏 Site.ApiKey 并失效路由缓存。
builder.Services.AddHostedService<CodexTokenRefreshService>();
// 周期刷新 Google 账号（Antigravity）token（有效期约 1 小时，提前 10 分钟刷新）。
builder.Services.AddHostedService<GoogleTokenRefreshService>();
// 周期刷新 Kimi 账号 token。
builder.Services.AddHostedService<KimiTokenRefreshService>();
// 周期恢复冷却到期的当前 OAuth 提供程序账号（清除冷却，恢复 Site，若未被手动禁用）。
builder.Services.AddHostedService<CodexCooldownRecoveryService>();
builder.Services.AddSingleton<DeveloperInvocationTraceStore>();
builder.Services.AddSingleton<IProxyDiagnosticService, ProxyDiagnosticService>();
builder.Services.AddSingleton<ModelConcurrencyLimiter>();
builder.Services.AddSingleton<IUsageLogService, UsageLogService>();

// 注册熔断状态存储，跟踪因连续失败而被临时屏蔽的站点。
builder.Services.AddSingleton<RouteCircuitStateStore>();
builder.Services.AddSingleton<ProxyRequestMetadataCache>();
builder.Services.AddSingleton<ModelVendorCatalogService>();
// 请求头模板方案（本地 JSON 文件 client-header-profiles.json，脱离数据库存储）。
builder.Services.AddSingleton<IHeaderProfileCatalogService, HeaderProfileCatalogService>();
// 模型价格表（本地 JSON，查询时动态计价，不落数据库）。
builder.Services.AddSingleton<IModelPricingService, ModelPricingService>();

// 注册 OAuth 账号供给相关服务（当前为 Codex 凭证实现）。
builder.Services.AddScoped<SiteCascadeDeleter>();
builder.Services.AddScoped<CodexAccountProvisioner>();
// Google 账号供给（Antigravity 隐藏 Site + 模型映射）。
builder.Services.AddScoped<GoogleAccountProvisioner>();
// Kimi 账号供给（隐藏 Site + 模型映射）。
builder.Services.AddScoped<KimiAccountProvisioner>();
// 实时代理命中 Codex 上游 401 时立即刷新凭证并同步隐藏站点。
builder.Services.AddScoped<CodexCredentialRefreshService>();
// 实时代理命中 Google 上游 401 时立即刷新凭证并同步隐藏站点。
builder.Services.AddScoped<GoogleCredentialRefreshService>();
// 实时代理命中 Kimi 上游 401 时立即刷新凭证并同步隐藏站点。
builder.Services.AddScoped<KimiCredentialRefreshService>();
// Codex 额度被动冷却与重置服务。
builder.Services.AddScoped<ICodexQuotaCooldownService, CodexQuotaCooldownService>();
// Codex 手动重置 credits 服务（查询剩余次数/过期时间 + 消耗一张 credit 执行真实重置）。
builder.Services.AddHttpClient<ICodexResetCreditsService, CodexResetCreditsService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
// OAuth 功能总开关过滤器（控制器级 gating）。
builder.Services.AddScoped<OAuthFeatureToggleAttribute>();
// 通用账号巡检开关过滤器（仅巡检相关 action 使用，关闭时返回 404）。
builder.Services.AddScoped<AccountInspectionToggleAttribute>();
// 通用账号额度巡检后台服务（周期巡检 + 缓存策略 + 自动禁用）。
builder.Services.AddSingleton<AccountQuotaInspectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AccountQuotaInspectionService>());

// 注册日志保留策略服务，定时清理过期日志。
builder.Services.AddScoped<ILogRetentionService, LogRetentionService>();

// 注册系统运行时设置服务，统一管理持久化的超时、重试和日志保留配置。
builder.Services.AddScoped<ISystemRuntimeSettingsService, SystemRuntimeSettingsService>();

// 注册 SQL 迁移脚本执行器（调试工具页 SQL 迁移 Tab）：只执行服务器 sql-migrations 目录下已放置的 .sql 文件。
builder.Services.AddScoped<SqlMigrationRunnerService>();

// 检测任务秒级调度服务（BackgroundService 轮询，最小 10s 间隔 + 随机抖动，替代 Hangfire Cron 分钟级）。
// 单例 + Hosted 同实例注册：控制器（立即执行端点）与后台循环共用同一实例与内存下次触发时间表。
builder.Services.AddSingleton<DetectionTaskSchedulerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DetectionTaskSchedulerService>());
// 管理后台长任务统一由宿主托管，避免控制器请求结束后遗留不可追踪的 fire-and-forget 任务。
builder.Services.AddSingleton<AdminBackgroundTaskQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AdminBackgroundTaskQueue>());
// 统计查询执行器配专用限容 MemoryCache（按条目数上限，超限 LRU 淘汰）：
// 与 AddMemoryCache 的共享实例隔离，避免 SizeLimit 波及其他不带 Size 的缓存使用方。
builder.Services.AddSingleton(sp => new AnalyticsBackgroundQueryExecutor(
    new Microsoft.Extensions.Caching.Memory.MemoryCache(
        new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions
        {
            SizeLimit = AnalyticsBackgroundQueryExecutor.MaxCacheEntries
        })));
builder.Services.AddHostedService(sp => sp.GetRequiredService<AnalyticsBackgroundQueryExecutor>());

// 日志保留清理调度（每天本地 03:00 后触发一次，替代 Hangfire RecurringJob，见 LogRetentionPruneService 注释）。
builder.Services.AddHostedService<LogRetentionPruneService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var sqlSugarClient = scope.ServiceProvider.GetRequiredService<ISqlSugarClient>();
    var dbInitLogger = scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("SqlSugarSetup");
    // CodeFirst 建表 + 补齐历史库缺失列（差量更新，只增不删）+ 持久化 PRAGMA（WAL、synchronous）。
    // 替代原 EF 的 EnsureCreated + 手写 ALTER TABLE 升级脚本：SqlSugar 的 InitTables 会自动补齐缺失列。
    SqlSugarSetup.InitializeDatabase(sqlSugarClient, dbInitLogger);

    // 预热 SiteUsageTracker：从 DB 读每个 Site 最近一次使用时间，避免重启后历史丢失。
    var siteUsageTracker = scope.ServiceProvider.GetRequiredService<SiteUsageTracker>();
    var warmupDbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await siteUsageTracker.WarmupAsync(warmupDbContext);

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
                var requestBody = await HttpLogFormatter.ReadRequestBodyPreviewAsync(context.Request, context.RequestAborted);

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
    // SPA 分离后：只有 /api/admin/* 需要服务端鉴权拦截。
    // 其余路径（/sites、/login 等前端路由）交给 SPA fallback + 前端 router 处理。
    if (app.Environment.IsEnvironment("Testing") || !IsAdminApiRequest(context.Request))
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
    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    context.Response.ContentType = "application/json; charset=utf-8";
    await context.Response.WriteAsJsonAsync(new
    {
        success = false,
        message = "未登录或登录已过期，请重新登录",
        errorCode = "unauthenticated"
    });
});

// 映射健康检查端点，作为集成测试的验证入口。
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithTags("Health");

// 映射 API 控制器路由，用于代理转发端点 + 后台管理 API。
app.MapControllers();

// SPA fallback：非 /api、/v1、/health 的请求全部返回 index.html，
// 交给前端 Vue Router 处理（history 模式）。MapFallbackToFile 会自动排除已映射的端点。
if (!app.Environment.IsEnvironment("Testing"))
{
    app.MapFallbackToFile("index.html");
}

app.Run();

// 从主程序集读取版本号（csproj 中定义的 Version / InformationalVersion / FileVersion / AssemblyVersion）。
// 优先 InformationalVersion（去掉 commit hash 附加信息），次选 FileVersion，再选 AssemblyName.Version，兜底 1.0.0.0。
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
// 找不到或解析失败时返回 null，由调用方回退到当前时间。
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

// 判断是否为后台接口请求（/api/admin 前缀）。
static bool IsAdminApiRequest(HttpRequest request)
{
    return request.Path.StartsWithSegments("/api/admin", StringComparison.OrdinalIgnoreCase);
}

// 获取本机 IPv4 地址。
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
/// 程序入口。
/// </summary>
public partial class Program;
