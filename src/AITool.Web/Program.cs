using AITool.Application.Common;
using AITool.Application.Detection;
using AITool.Application.Operations;
using AITool.Application.Proxy;
using AITool.Application.Routing;
using AITool.Application.SiteCatalog;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Operations;
using AITool.Infrastructure.OpenAI;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Infrastructure.Retention;
using AITool.Infrastructure.Routing;
using AITool.Infrastructure.Scheduling;
using AITool.Web.Services;
using Hangfire;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 注册 Razor Pages，作为管理后台的页面框架
builder.Services.AddRazorPages();

// 注册 API 控制器，用于代理转发端点
builder.Services.AddControllers();
builder.Services.AddMemoryCache();

// 注册 EF Core SQLite 数据库上下文
// 数据库文件放在软件根目录下
var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aitool.db");
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? $"Data Source={Path.GetFullPath(dbPath)}";
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(connectionString));

// 注册代理转发配置，统一控制单路由超时和失败重试策略
builder.Services.Configure<ProxyForwardingOptions>(
    builder.Configuration.GetSection(ProxyForwardingOptions.SectionName));

// 注册站点目录客户端，用于拉取远程站点模型列表
builder.Services.AddHttpClient<ISiteCatalogClient, OpenAiSiteCatalogClient>();

// 注册模型探测服务，用于检测模型可用性
builder.Services.AddHttpClient<IModelProbeService, OpenAiModelProbeService>();

// 注册路由选择服务，用于根据优先级匹配代理路由
builder.Services.AddScoped<IRouteSelectionService, RouteSelectionService>();

// 注册代理转发服务，使用 HttpClient 转发请求到上游站点
builder.Services.AddHttpClient<IProxyForwardService, ProxyForwardService>();

// 注册使用日志服务，记录每次代理调用的 Token 用量
builder.Services.AddSingleton<ProxyUsageLogBatchWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProxyUsageLogBatchWriter>());
builder.Services.AddSingleton<IUsageLogService, UsageLogService>();

// 注册熔断状态存储，跟踪因连续失败而被临时屏蔽的站点
builder.Services.AddSingleton<RouteCircuitStateStore>();
builder.Services.AddSingleton<ProxyRequestMetadataCache>();

// 注册日志保留策略服务，定时清理过期日志
builder.Services.AddScoped<ILogRetentionService, LogRetentionService>();

// 注册系统运行时设置服务，统一管理持久化的超时、重试和日志保留配置
builder.Services.AddScoped<ISystemRuntimeSettingsService, SystemRuntimeSettingsService>();

// 注册 Hangfire 检测调度器
builder.Services.AddSingleton<HangfireDetectionScheduler>();
builder.Services.AddSingleton<AnalyticsBackgroundQueryExecutor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AnalyticsBackgroundQueryExecutor>());

// 注册 Hangfire 内存存储与仪表盘
builder.Services.AddHangfire(config => config
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseInMemoryStorage());
builder.Services.AddHangfireServer();

var app = builder.Build();

// 自动执行数据库迁移
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // 补丁：为已有数据库添加缺失列，兼容历史 SQLite 文件
    try
    {
        var conn = db.Database.GetDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();

        EnsureColumn(cmd, "SiteModelMappings", "IsEnabled", "ALTER TABLE SiteModelMappings ADD COLUMN IsEnabled INTEGER NOT NULL DEFAULT 1");
        EnsureColumn(cmd, "ProxyAccessKeys", "PlainKey", "ALTER TABLE ProxyAccessKeys ADD COLUMN PlainKey TEXT NOT NULL DEFAULT ''");
        EnsureColumn(cmd, "ProxyRouteRules", "UpstreamModelName", "ALTER TABLE ProxyRouteRules ADD COLUMN UpstreamModelName TEXT NOT NULL DEFAULT ''");
        EnsureColumn(cmd, "ProxyRouteRules", "ModelPriority", "ALTER TABLE ProxyRouteRules ADD COLUMN ModelPriority INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(cmd, "ProxyRouteRules", "InstancePriority", "ALTER TABLE ProxyRouteRules ADD COLUMN InstancePriority INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(cmd, "ProxyUsageLogs", "RequestId", "ALTER TABLE ProxyUsageLogs ADD COLUMN RequestId TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'");
        EnsureColumn(cmd, "ProxyUsageLogs", "AttemptedModel", "ALTER TABLE ProxyUsageLogs ADD COLUMN AttemptedModel TEXT NOT NULL DEFAULT ''");
        EnsureColumn(cmd, "ProxyUsageLogs", "AttemptIndex", "ALTER TABLE ProxyUsageLogs ADD COLUMN AttemptIndex INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(cmd, "ProxyUsageLogs", "IsFinalResult", "ALTER TABLE ProxyUsageLogs ADD COLUMN IsFinalResult INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(cmd, "ProxyUsageLogs", "FallbackTriggered", "ALTER TABLE ProxyUsageLogs ADD COLUMN FallbackTriggered INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(cmd, "ProxyUsageLogs", "ErrorMessage", "ALTER TABLE ProxyUsageLogs ADD COLUMN ErrorMessage TEXT NOT NULL DEFAULT ''");
        EnsureColumn(cmd, "ProxyUsageLogs", "CachedTokens", "ALTER TABLE ProxyUsageLogs ADD COLUMN CachedTokens INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(cmd, "ProxyUsageLogs", "IsStreaming", "ALTER TABLE ProxyUsageLogs ADD COLUMN IsStreaming INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(cmd, "ProxyUsageLogs", "IsStreamInterrupted", "ALTER TABLE ProxyUsageLogs ADD COLUMN IsStreamInterrupted INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(cmd, "ProxyUsageLogs", "FirstTokenLatencyMs", "ALTER TABLE ProxyUsageLogs ADD COLUMN FirstTokenLatencyMs INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(cmd, "ProxyUsageLogs", "StreamDurationMs", "ALTER TABLE ProxyUsageLogs ADD COLUMN StreamDurationMs INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(cmd, "ProxyUsageLogs", "TotalDurationMs", "ALTER TABLE ProxyUsageLogs ADD COLUMN TotalDurationMs INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(cmd, "ProxyUsageLogs", "ReasoningEffort", "ALTER TABLE ProxyUsageLogs ADD COLUMN ReasoningEffort TEXT NOT NULL DEFAULT ''");
        EnsureIndex(cmd, "IX_ProxyAccessKeys_AccessKeyHash_IsEnabled", "CREATE INDEX IX_ProxyAccessKeys_AccessKeyHash_IsEnabled ON ProxyAccessKeys (AccessKeyHash, IsEnabled)");
        EnsureIndex(cmd, "IX_ProxyRouteRules_ExternalModelName_IsEnabled_ModelPriority_InstancePriority_Priority", "CREATE INDEX IX_ProxyRouteRules_ExternalModelName_IsEnabled_ModelPriority_InstancePriority_Priority ON ProxyRouteRules (ExternalModelName, IsEnabled, ModelPriority, InstancePriority, Priority)");

        if (!TableExists(cmd, "ProxyRouteEntries"))
        {
            cmd.CommandText = "CREATE TABLE ProxyRouteEntries (Id TEXT NOT NULL CONSTRAINT PK_ProxyRouteEntries PRIMARY KEY, EntryName TEXT NOT NULL, CreatedAt TEXT NOT NULL)";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "CREATE UNIQUE INDEX IX_ProxyRouteEntries_EntryName ON ProxyRouteEntries (EntryName)";
            cmd.ExecuteNonQuery();
        }

        if (TableExists(cmd, "SystemRuntimeSettings"))
        {
            EnsureColumn(cmd, "SystemRuntimeSettings", "ProxyRequestTimeoutSeconds", "ALTER TABLE SystemRuntimeSettings ADD COLUMN ProxyRequestTimeoutSeconds INTEGER NOT NULL DEFAULT 60");
            EnsureColumn(cmd, "SystemRuntimeSettings", "ProxyRetryCount", "ALTER TABLE SystemRuntimeSettings ADD COLUMN ProxyRetryCount INTEGER NOT NULL DEFAULT 1");
            EnsureColumn(cmd, "SystemRuntimeSettings", "UsageLogRetentionDays", "ALTER TABLE SystemRuntimeSettings ADD COLUMN UsageLogRetentionDays INTEGER NOT NULL DEFAULT 7");
            EnsureColumn(cmd, "SystemRuntimeSettings", "DetectionLogRetentionDays", "ALTER TABLE SystemRuntimeSettings ADD COLUMN DetectionLogRetentionDays INTEGER NOT NULL DEFAULT 7");
            EnsureColumn(cmd, "SystemRuntimeSettings", "LastUsageLogPrunedAt", "ALTER TABLE SystemRuntimeSettings ADD COLUMN LastUsageLogPrunedAt TEXT NULL");
            EnsureColumn(cmd, "SystemRuntimeSettings", "LastUsageLogPrunedCount", "ALTER TABLE SystemRuntimeSettings ADD COLUMN LastUsageLogPrunedCount INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(cmd, "SystemRuntimeSettings", "LastDetectionLogPrunedAt", "ALTER TABLE SystemRuntimeSettings ADD COLUMN LastDetectionLogPrunedAt TEXT NULL");
            EnsureColumn(cmd, "SystemRuntimeSettings", "LastDetectionLogPrunedCount", "ALTER TABLE SystemRuntimeSettings ADD COLUMN LastDetectionLogPrunedCount INTEGER NOT NULL DEFAULT 0");
        }
    }
    catch (Exception ex)
    {
        var patchLogger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        patchLogger.LogWarning(ex, "修补 SQLite 缺失列失败");
    }

    // 启动时将已启用的检测任务注册到 Hangfire，首次运行或表不存在时跳过
    try
    {
        var scheduler = scope.ServiceProvider.GetRequiredService<HangfireDetectionScheduler>();
        await scheduler.ScheduleAllAsync(default);
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "启动时注册检测任务失败，将在下次启动时重试");
    }
}

// 启用静态文件服务，提供 wwwroot 下的 CSS/JS 等资源
app.UseStaticFiles();

// 映射健康检查端点，作为集成测试的验证入口
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// 启用 Hangfire 仪表盘，仅限本地访问
app.UseHangfireDashboard("/hangfire");

// 注册日志清理定时任务，每天凌晨 3 点执行
RecurringJob.AddOrUpdate<ILogRetentionService>(
    "log-retention-prune",
    svc => svc.PruneAsync(CancellationToken.None),
    "0 3 * * *");

// 映射 Razor Pages 路由
app.MapRazorPages();

// 映射 API 控制器路由，用于代理转发端点
app.MapControllers();

app.Run();

static bool TableExists(System.Data.Common.DbCommand command, string tableName)
{
    command.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{tableName}'";
    return Convert.ToInt64(command.ExecuteScalar()) > 0;
}

static void EnsureColumn(System.Data.Common.DbCommand command, string tableName, string columnName, string alterSql)
{
    command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name='{columnName}'";
    var count = Convert.ToInt64(command.ExecuteScalar());
    if (count == 0)
    {
        command.CommandText = alterSql;
        command.ExecuteNonQuery();
    }
}

static void EnsureIndex(System.Data.Common.DbCommand command, string indexName, string createSql)
{
    command.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='{indexName}'";
    var count = Convert.ToInt64(command.ExecuteScalar());
    if (count == 0)
    {
        command.CommandText = createSql;
        command.ExecuteNonQuery();
    }
}

// 暴露 Program 类供集成测试引用
public partial class Program;
