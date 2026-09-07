using AITool.Admin;
using AITool.Admin.Controllers.Proxy;
using AITool.Admin.Services;
using AITool.Application.Accounts;
using AITool.Application.Codex;
using AITool.Application.Common;
using AITool.Application.Google;
using AITool.Application.Kimi;
using AITool.Application.Pricing;
using AITool.Application.Proxy;
using AITool.Infrastructure.Codex;
using AITool.Infrastructure.Common;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.DependencyInjection;
using AITool.Infrastructure.Google;
using AITool.Infrastructure.Hosting;
using AITool.Infrastructure.Kimi;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Pricing;
using AITool.Infrastructure.Proxy;
using AITool.Infrastructure.Retention;
using AITool.Infrastructure.Scheduling;
using AITool.Infrastructure.Security;
using AppVersionInfo = AITool.Infrastructure.Hosting.AppVersionInfo;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NLog.Web;
using NLog;
using System.Text;

namespace AITool.Admin;

/// <summary>
/// Admin 宿主服务注册段（供 Admin 与 AllInOne 两种宿主复用，避免双份注册漂移）。
/// 边界：从 AddCommonInfrastructure 之后的全部 services 注册，不含宿主管道（middleware/use*）。
/// </summary>
public static class AdminProgramServices
{
    /// <param name="enableCrossHostServices">是否启用跨宿主服务（配置下发/事件拉取）。
    /// Admin 宿主 true；AllInOne 单进程形态 false（无跨宿主，跳过相关 HostedService 与状态探测）。</param>
    public static void AddAdminHostServices(this WebApplicationBuilder builder, string connectionString, bool enableCrossHostServices = true)
    {
builder.Services.AddCommonInfrastructure();

// 启用响应压缩，压缩 API JSON 响应（Analytics/UsageLogs/Invocations 列表）和静态资源。
// EnableForHttps=true 确保内网 HTTPS 部署也压缩。
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});

// 注册 Web + Admin 共享的管理后台基础设施：认证、数据库、后台服务。
builder.Services.AddAdminInfrastructure(connectionString);

// ===== JWT 认证（替换原 Cookie 方案，适配 Vue SPA 前端）=====
// 配置 JwtOptions 绑定 + 签发/刷新服务 + 登录暴力破解防护。
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddSingleton<LoginRateLimitService>();
// 管理后台认证服务（PBKDF2 密码哈希，兼容旧 MD5 透明升级）。
builder.Services.AddSingleton<AdminAuthService>();

// 认证：纯 JWT Bearer。/api/* 用 Bearer token 验证；代理端点 /v1/* 不走 ASP.NET 认证（AccessKey 自校验）。
// Testing 环境使用匿名方案放行所有请求，避免集成测试需要签发真实 JWT。
if (builder.Environment.IsEnvironment("Testing"))
{
    builder.Services
        .AddAuthentication("Testing")
        .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, AITool.Admin.TestingAuthHandler>("Testing", _ => { });
}
else
{
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
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
}
// 默认授权策略：所有未标 [AllowAnonymous] 的端点都要求已认证用户。
// 这样所有 Admin 控制器自动受 JWT 保护，无需逐个加 [Authorize]。
// AuthApiController 的 status/login/refresh/logout/setup 标了 [AllowAnonymous]，不受影响。
// 代理端点 /v1/* 在 Core 宿主，不在 Admin，不受此策略影响。
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

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
            Description = "AI-Tool 后台管理 API 文档"
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
    });
}

// Admin 侧开发者追踪内存存储，缓存从 Core 拉取的 developer-trace 事件摘要。
// Singleton 生命周期：内存数据跨请求保持，6 小时过期自动清理，最多 100 条。
builder.Services.AddSingleton<AdminDeveloperTraceStore>();
// Admin 侧 UnifiedProxy 事件消费器，统一消费 Core 代理产生的 UsageLog 和 DeveloperTrace 事件。
builder.Services.AddScoped<AdminUnifiedProxyEventIngestor>();

// Admin 侧路由回退事件内存存储，缓存从 Core 拉取的 route-fallback 事件。
// Singleton 生命周期：内存数据跨请求保持，6 小时过期自动清理，最多 200 条。
builder.Services.AddSingleton<AdminRouteFallbackStore>();
// Admin 侧 RouteFallback 事件消费器，将 Core 代理产生的路由回退事件写入内存存储。
builder.Services.AddScoped<AdminRouteFallbackEventIngestor>();

// Admin 侧配置变更应用事件内存存储，缓存从 Core 拉取的 config-applied 事件。
// Singleton 生命周期：内存数据跨请求保持，24 小时过期自动清理，最多 100 条。
builder.Services.AddSingleton<AdminConfigAppliedStore>();
// Admin 侧 ConfigApplied 事件消费器，将 Core 配置变更确认事件写入内存存储。
builder.Services.AddScoped<AdminConfigAppliedEventIngestor>();

// Admin 侧熔断状态变更事件内存存储，缓存从 Core 拉取的 circuit-breaker 事件。
// Singleton 生命周期：内存数据跨请求保持，6 小时过期自动清理，最多 200 条。
builder.Services.AddSingleton<AdminCircuitBreakerStore>();
// Admin 侧 CircuitBreaker 事件消费器，将 Core 代理产生的熔断事件写入内存存储。
builder.Services.AddScoped<AdminCircuitBreakerEventIngestor>();

// Admin 侧事件 ack 状态持久化，将已确认序号写入本地文件，确保重启后不重复消费历史事件。
var ackMetaPath = builder.Environment.IsEnvironment("Testing")
    ? Path.Combine(Path.GetTempPath(), $"aitool-core-event-ack-{Guid.NewGuid():N}", "ack.meta")
    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "core-runtime", "ack.meta");
builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<CoreEventAckStateStore>>();
    return new CoreEventAckStateStore(ackMetaPath, logger);
});

// Admin 侧事件拉取核心逻辑，从 HostedService 中提取出来以便独立测试。
// HostedService 每个轮次创建新 scope 并通过 ActivatorUtilities 解析此服务。
builder.Services.AddScoped<CoreEventPullService>();
// 凭证事件摄取器（credential-refreshed/disabled 落库并触发配置同步）。
builder.Services.AddScoped<AdminCredentialEventIngestor>();

// Admin 侧缓存失效门面，通过 CoreAdminClient 向 Core 下发全量配置快照以刷新运行时缓存。
builder.Services.AddSingleton<CoreSyncStatusStore>();
// Core 状态探测器（仪表盘真实握手 + 8s 内存缓存）。
if (enableCrossHostServices)
{
    builder.Services.AddSingleton<AITool.Admin.Services.ICoreStatusProvider, CoreStatusProbe>();
}
else
{
    builder.Services.AddSingleton<AITool.Admin.Services.ICoreStatusProvider, AITool.Admin.Services.LocalModeStatusProbe>();
}
builder.Services.AddSingleton<ProxyRequestMetadataCache>(sp =>
{
    var memoryCache = sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
    var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    return new ProxyRequestMetadataCache(memoryCache, scopeFactory);
});
builder.Services.AddSingleton<AdminQueryMetadataService>();
builder.Services.AddScoped<AdminCacheInvalidationService>();

// Admin 侧并发控制门面（占位实现）。后续通过 CoreAdminClient 代理运行时并发限制变更。
builder.Services.AddSingleton<AdminConcurrencyControlService>();

// 模型厂商目录服务（可选，在模型库页面管理厂商规则时使用）。
builder.Services.AddSingleton<ModelVendorCatalogService>();

// 以下 3 个服务原本只在 Core 宿主的 AddProxyRuntimeInfrastructure 注册，
// 但 Admin 宿主的多个 ApiController（ModelsApi/DeveloperInvocationsApi/SystemSettingsApi）
// 通过构造函数注入它们，必须在 Admin 也注册才能 DI 解析成功。
// 不调用 AddProxyRuntimeInfrastructure 是因为那会带来 Core 专用的代理转发/spool 等副作用。
builder.Services.AddSingleton<ModelConcurrencyLimiter>();
builder.Services.AddSingleton<RouteCircuitStateStore>();
builder.Services.AddSingleton<DeveloperInvocationTraceStore>();

// 注册日志保留策略服务，定时清理过期日志。
builder.Services.AddScoped<ILogRetentionService, LogRetentionService>();

// ===== Codex OAuth 账号管理功能（管理面，依赖 AppDbContext，仅在 Admin 宿主注册） =====
// 注册 Codex OAuth 客户端，用于 PKCE 授权、token 交换与刷新（复用连接池）。
builder.Services.AddHttpClient<ICodexOAuthClient, CodexOAuthClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(20);
});
// 注册 Codex 静态模型目录（进程内只读）。
builder.Services.AddSingleton<ICodexModelCatalog, CodexModelCatalog>();
// Codex 上游客户端版本配置化（模型拉取 UA / client_version 参数可按上游演进调整，780ac7b）。
builder.Services.Configure<CodexUpstreamOptions>(builder.Configuration.GetSection(CodexUpstreamOptions.SectionName));
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
// 在实时代理请求命中 Codex 上游 401 时，立即刷新账号凭证并同步隐藏站点。
// 仅在 Admin 注册（依赖 AppDbContext 直接读写数据库）。
builder.Services.AddScoped<CodexCredentialRefreshService>();
// 周期刷新 Codex 账号 OAuth token，写回隐藏 Site.ApiKey 并通过 AdminCacheInvalidationService 推送到 Core。
builder.Services.AddHostedService<CodexTokenRefreshService>();
// 周期恢复冷却到期的 Codex 账号（清除冷却，恢复 Site，若未被手动禁用）。
builder.Services.AddHostedService<CodexCooldownRecoveryService>();
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

// —— Google（Antigravity）账号托管五件套 ——
builder.Services.AddHttpClient<IGoogleOAuthClient, GoogleOAuthClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient<IGoogleModelFetcher, GoogleModelFetcher>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient<GoogleAccountQuotaService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddTransient<IAccountQuotaProvider>(sp => sp.GetRequiredService<GoogleAccountQuotaService>());
builder.Services.AddHostedService<GoogleTokenRefreshService>();
builder.Services.AddScoped<GoogleAccountProvisioner>();
builder.Services.AddScoped<GoogleCredentialRefreshService>();

// —— Kimi（Moonshot）账号托管五件套 ——
builder.Services.AddHttpClient<IKimiOAuthClient, KimiOAuthClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient<IKimiModelFetcher, KimiModelFetcher>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient<KimiQuotaService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddTransient<IAccountQuotaProvider>(sp => sp.GetRequiredService<KimiQuotaService>());
builder.Services.AddHostedService<KimiTokenRefreshService>();
builder.Services.AddScoped<KimiAccountProvisioner>();
builder.Services.AddScoped<KimiCredentialRefreshService>();

// —— SQL 迁移执行服务（仅执行服务器 sql-migrations 目录脚本，密码确认 + 事务 + dry-run） ——
builder.Services.AddScoped<SqlMigrationRunnerService>();

// OAuth 账号功能总开关过滤器（控制器级 gating，Codex/Google/Kimi 统一）。
builder.Services.AddScoped<OAuthFeatureToggleAttribute>();
// 账号巡检开关过滤器（仅巡检相关 action 使用，关闭时返回 404）。
builder.Services.AddScoped<AccountInspectionToggleAttribute>();
// 账号额度统一巡检后台服务（聚合 Codex/Google/Kimi 的 IAccountQuotaProvider：周期额度巡检 + 缓存策略 + 自动禁用）。
// 单例，供 API 与后台共用状态（手动巡检 RunManualAsync 与后台循环跑同一实例）。
builder.Services.AddSingleton<AccountQuotaInspectionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AccountQuotaInspectionService>());

// 客户端特征模拟：请求头模板档案（client-header-profiles.json 本地文件存储，不落数据库）。
builder.Services.AddSingleton<IHeaderProfileCatalogService, HeaderProfileCatalogService>();
// 代理诊断抓包（文件型转储）：开发者工具「诊断抓包」页的数据源；Core 宿主写同一目录实现跨宿主可见。
builder.Services.AddSingleton<IProxyDiagnosticService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<ProxyDiagnosticService>>();
    var metadataCache = sp.GetService<ProxyRequestMetadataCache>();
    return new ProxyDiagnosticService(logger, metadataCache);
});
// 模型定价目录（model-pricing.json 本地文件存储，按 LastWriteTime 自动刷新快照）。
builder.Services.AddSingleton<IModelPricingService, ModelPricingService>();
// 管理端长任务统一托管队列（SQL 迁移执行等耗时操作的取消/进度管理）。
builder.Services.AddSingleton<AdminBackgroundTaskQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AdminBackgroundTaskQueue>());
// 可视化分析后台查询执行器（有界 Channel 单消费者 + 版本化结果缓存，避免并发长查询打爆 SQLite）。
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

// Admin 通过最小 Core 客户端与核心宿主通信。当前阶段先提供握手、full-sync、ack、replay 这几项最关键能力。
var coreBaseUrl = builder.Configuration["CoreServer:BaseUrl"] ?? $"http://127.0.0.1:{builder.Configuration.GetValue<int?>("CoreServer:Port") ?? 5029}/";
// 跨宿主共享密钥：注入器必须显式注册（AddHttpMessageHandler<T> 泛型重载在部分运行时不会自动登记类型）。
builder.Services.AddTransient<CoreAuthHeaderHandler>();
builder.Services.AddHttpClient<CoreAdminClient>(client =>
{
    client.BaseAddress = new Uri(coreBaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(30);
})
.AddHttpMessageHandler<CoreAuthHeaderHandler>();
// SSE 专用 HttpClient，用于 CoreEventPullHostedService 实时监听 Core 事件通知流。
// SSE 是无限流，不能用默认的 30 秒超时，必须设置无限超时。
builder.Services.AddHttpClient("CoreSSE")
    .AddHttpMessageHandler<CoreAuthHeaderHandler>();
// Admin /v1 中继（Relay:Enabled 默认关）：客户端就近连 Admin，跨境链路集中在 Admin↔Core；
// 调用方切换指向 Admin 的 5030 端口即可，断线续传由中继透明处理。
builder.Services.AddSingleton(new V1RelayOptions
{
    Enabled = builder.Configuration.GetValue("Relay:Enabled", false),
    HeartbeatSeconds = builder.Configuration.GetValue("ProxyForwarding:SseHeartbeatSeconds", 15),
    MaxReconnects = builder.Configuration.GetValue("Relay:MaxReconnects", 3)
});
builder.Services.AddHttpClient("RelayCoreClient", client =>
{
    client.BaseAddress = new Uri(coreBaseUrl, UriKind.Absolute);
    client.Timeout = Timeout.InfiniteTimeSpan; // 流式长连接由中继循环自行管理
})
.AddHttpMessageHandler<CoreAuthHeaderHandler>();

// Admin 启动后自动将数据库配置同步到 Core 宿主。
// 如果 Core 尚未就绪，会按指数退避重试，最多 5 次。
// Admin 定时从 Core 拉取事件（replay）、消费入库（ingest）、提交确认（ack）。
// 构成完整的事件消费闭环：Core 产生事件 → spool 兜底 → Admin 拉取 → 入库 → 确认。
// AllInOne 单进程形态（enableCrossHostServices=false）两者均无跨宿主语义，不注册。
if (enableCrossHostServices)
{
    builder.Services.AddHostedService<CoreConfigSyncHostedService>();
    builder.Services.AddHostedService<CoreEventPullHostedService>();
}
    }
}
