using System.Data.Common;
using System.Text;
using AITool.Application.Common;
using AITool.Application.Operations;
using AITool.Application.Proxy;
using AITool.Application.SiteCatalog;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Health;
using AITool.Infrastructure.Hosting;
using AITool.Infrastructure.Operations;
using AITool.Infrastructure.OpenAI;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Infrastructure.Retention;
using AITool.Infrastructure.Scheduling;
using AITool.Web.Services;
using Hangfire;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NLog;
using NLog.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Host.UseNLog();

var startupLogger = LogManager.GetLogger("Startup");

var applicationVersion = "1.0.1.4-admin";
builder.Services.AddSingleton(new AppVersionInfo(applicationVersion));

var serverPort = builder.Configuration.GetValue<int?>("AdminServer:Port") ?? builder.Configuration.GetValue<int?>("Server:Port") ?? 5030;
builder.WebHost.UseUrls($"http://0.0.0.0:{serverPort}");

builder.Services.AddRazorPages();
builder.Services.AddControllers(options =>
{
    options.Filters.Add<HttpExceptionLoggingFilter>();
});
builder.Services.AddMemoryCache();
builder.Services.AddScoped<HttpExceptionLoggingFilter>();

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
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

var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aitool.db");
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? $"Data Source={Path.GetFullPath(dbPath)}";
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.Configure<ProxyForwardingOptions>(
    builder.Configuration.GetSection(ProxyForwardingOptions.SectionName));

builder.Services.AddHttpClient<ISiteCatalogClient, OpenAiSiteCatalogClient>();
builder.Services.AddHttpClient<IProxyForwardService, ProxyForwardService>();
builder.Services.AddScoped<ModelHealthRequestService>();

builder.Services.AddSingleton(new CoreRuntimeConfigFileOptions
{
    FilePath = builder.Environment.IsEnvironment("Testing")
        ? Path.Combine(Path.GetTempPath(), $"aitool-core-runtime-config-{Guid.NewGuid():N}.json")
        : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "core-runtime", "last-good-config.json")
});
builder.Services.AddSingleton<CoreRuntimeConfigProvider>();
builder.Services.AddSingleton<AITool.Application.CoreRuntime.ICoreRuntimeConfigProvider>(sp => sp.GetRequiredService<CoreRuntimeConfigProvider>());

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
builder.Services.AddSingleton<CoreUsageLogEventPublisher>();
builder.Services.AddSingleton<AITool.Infrastructure.Conversations.CoreConversationEventPublisher>();
builder.Services.AddSingleton<ProxyUsageLogBatchWriter>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ProxyUsageLogBatchWriter>());

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
builder.Services.AddSingleton<DeveloperInvocationTraceStore>();
builder.Services.AddSingleton<ModelConcurrencyLimiter>();
builder.Services.AddSingleton<IUsageLogService, UsageLogService>();
builder.Services.AddSingleton<AITool.Application.Conversations.IConversationLogService, AITool.Infrastructure.Conversations.ConversationLogService>();
builder.Services.AddSingleton<AITool.Infrastructure.Conversations.ConversationExtractionService>();

builder.Services.AddSingleton<RouteCircuitStateStore>();
builder.Services.AddSingleton<ProxyRequestMetadataCache>();
builder.Services.AddSingleton<ModelVendorCatalogService>();

builder.Services.AddScoped<ILogRetentionService, LogRetentionService>();
builder.Services.AddScoped<ISystemRuntimeSettingsService, SystemRuntimeSettingsService>();

builder.Services.AddSingleton<HangfireDetectionScheduler>();
builder.Services.AddSingleton<AnalyticsBackgroundQueryExecutor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AnalyticsBackgroundQueryExecutor>());

builder.Services.AddHangfire(config => config
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseInMemoryStorage());
builder.Services.AddHangfireServer();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
    await EnsureProxyUsageLogSchemaAsync(db);
    await EnsureConversationLogSchemaAsync(db);

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

    var settingsService = scope.ServiceProvider.GetRequiredService<ISystemRuntimeSettingsService>();
    var configProvider = scope.ServiceProvider.GetRequiredService<AITool.Application.CoreRuntime.ICoreRuntimeConfigProvider>();
    if (!await configProvider.TryLoadFromFileAsync())
    {
        startupLogger.Warn("Core 启动时未找到可恢复的 last-good-config，将等待 Admin 下发首个完整配置快照后进入 ready 状态。");
    }
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
Console.WriteLine($"AI Tool Admin 已启动：http://127.0.0.1:{serverPort}");
Console.WriteLine($"AI Tool Admin 已启动：http://{GetLocalIpAddress()}:{serverPort}");

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

app.UseStaticFiles();
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

    var returnUrl = context.Request.PathBase + context.Request.Path + context.Request.QueryString;
    var loginUrl = string.IsNullOrWhiteSpace(returnUrl)
        ? "/Login"
        : $"/Login?returnUrl={Uri.EscapeDataString(returnUrl)}";
    context.Response.Redirect(loginUrl);
});

app.MapControllers();
app.MapRazorPages();
app.UseHangfireDashboard("/hangfire");
RecurringJob.AddOrUpdate<ILogRetentionService>(
    "log-retention-prune",
    svc => svc.PruneAsync(CancellationToken.None),
    "0 3 * * *");

app.Run();

static bool IsAdminRequest(HttpRequest request)
{
    return request.Path.StartsWithSegments("/Admin", StringComparison.OrdinalIgnoreCase)
        || request.Path.StartsWithSegments("/Login", StringComparison.OrdinalIgnoreCase)
        || request.Path.StartsWithSegments("/hangfire", StringComparison.OrdinalIgnoreCase);
}

static bool IsLoginPageRequest(HttpRequest request)
{
    return request.Path.StartsWithSegments("/Login", StringComparison.OrdinalIgnoreCase);
}

static string GetLocalIpAddress()
{
    try
    {
        var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
        var address = host.AddressList.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(ip));
        return address?.ToString() ?? "127.0.0.1";
    }
    catch
    {
        return "127.0.0.1";
    }
}

static async Task<string> TryReadRequestBodySafelyAsync(HttpRequest request, CancellationToken cancellationToken)
{
    try
    {
        if (request.Body is null || !request.Body.CanRead)
        {
            return string.Empty;
        }

        if (!request.Body.CanSeek)
        {
            request.EnableBuffering();
        }

        request.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var body = await reader.ReadToEndAsync(cancellationToken);
        request.Body.Seek(0, SeekOrigin.Begin);
        return body;
    }
    catch
    {
        return string.Empty;
    }
}

static async Task EnsureProxyUsageLogSchemaAsync(AppDbContext dbContext)
{
    await using var connection = dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
    {
        await connection.OpenAsync();
    }

    await using var command = connection.CreateCommand();
    command.CommandText = @"
CREATE TABLE IF NOT EXISTS ProxyUsageLogs (
    Id TEXT NOT NULL PRIMARY KEY,
    RequestId TEXT NOT NULL,
    AccessKeyId TEXT NOT NULL,
    ProtocolType TEXT NOT NULL,
    ForwardingMode TEXT NULL,
    RequestModel TEXT NOT NULL,
    AttemptedModel TEXT NOT NULL,
    TargetSiteId TEXT NOT NULL,
    Status TEXT NOT NULL,
    Source TEXT NOT NULL,
    RetryCount INTEGER NOT NULL,
    AttemptIndex INTEGER NOT NULL,
    IsFinalResult INTEGER NOT NULL,
    FallbackTriggered INTEGER NOT NULL,
    ErrorMessage TEXT NOT NULL,
    InputTokens INTEGER NOT NULL,
    CachedTokens INTEGER NOT NULL,
    OutputTokens INTEGER NOT NULL,
    TotalTokens INTEGER NOT NULL,
    IsStreaming INTEGER NOT NULL,
    IsStreamInterrupted INTEGER NOT NULL DEFAULT 0,
    FirstTokenLatencyMs INTEGER NOT NULL,
    StreamDurationMs INTEGER NOT NULL,
    TotalDurationMs INTEGER NOT NULL,
    ReasoningEffort TEXT NOT NULL DEFAULT '',
    RequestedAt TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_ProxyUsageLogs_RequestedAt ON ProxyUsageLogs (RequestedAt);
CREATE INDEX IF NOT EXISTS IX_ProxyUsageLogs_RequestId ON ProxyUsageLogs (RequestId);
";
    await command.ExecuteNonQueryAsync();

    if (!await ColumnExistsAsync(connection, "ProxyUsageLogs", "CachedTokens"))
    {
        command.CommandText = "ALTER TABLE ProxyUsageLogs ADD COLUMN CachedTokens INTEGER NOT NULL DEFAULT 0";
        await command.ExecuteNonQueryAsync();
    }

    if (!await ColumnExistsAsync(connection, "ProxyUsageLogs", "IsStreamInterrupted"))
    {
        command.CommandText = "ALTER TABLE ProxyUsageLogs ADD COLUMN IsStreamInterrupted INTEGER NOT NULL DEFAULT 0";
        await command.ExecuteNonQueryAsync();
    }

    if (!await ColumnExistsAsync(connection, "ProxyUsageLogs", "ReasoningEffort"))
    {
        command.CommandText = "ALTER TABLE ProxyUsageLogs ADD COLUMN ReasoningEffort TEXT NOT NULL DEFAULT ''";
        await command.ExecuteNonQueryAsync();
    }

    if (!await ColumnExistsAsync(connection, "ProxyUsageLogs", "ForwardingMode"))
    {
        command.CommandText = "ALTER TABLE ProxyUsageLogs ADD COLUMN ForwardingMode TEXT NULL";
        await command.ExecuteNonQueryAsync();
    }
}

static async Task EnsureConversationLogSchemaAsync(AppDbContext dbContext)
{
    await using var connection = dbContext.Database.GetDbConnection();
    if (connection.State != System.Data.ConnectionState.Open)
    {
        await connection.OpenAsync();
    }

    await using var command = connection.CreateCommand();
    command.CommandText = @"
CREATE TABLE IF NOT EXISTS ConversationTurnLogs (
    Id TEXT NOT NULL PRIMARY KEY,
    RequestId TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    UserCreatedAt TEXT NULL,
    SourceTool TEXT NOT NULL,
    SessionId TEXT NOT NULL,
    ConversationGroupKey TEXT NOT NULL,
    AccessKeyId TEXT NOT NULL,
    RequestModel TEXT NOT NULL,
    ProtocolType TEXT NOT NULL,
    RequestPath TEXT NOT NULL,
    Source TEXT NOT NULL,
    UserInputText TEXT NOT NULL,
    AssistantOutputMarkdown TEXT NOT NULL,
    InputTokens INTEGER NOT NULL,
    CachedTokens INTEGER NOT NULL,
    OutputTokens INTEGER NOT NULL,
    IsStreaming INTEGER NOT NULL,
    Status TEXT NOT NULL,
    MetadataJson TEXT NOT NULL,
    ConversationTitle TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS IX_ConversationTurnLogs_CreatedAt ON ConversationTurnLogs (CreatedAt);
CREATE INDEX IF NOT EXISTS IX_ConversationTurnLogs_RequestId ON ConversationTurnLogs (RequestId);
CREATE INDEX IF NOT EXISTS IX_ConversationTurnLogs_ConversationGroupKey ON ConversationTurnLogs (ConversationGroupKey);
CREATE INDEX IF NOT EXISTS IX_ConversationTurnLogs_SourceTool_SessionId_CreatedAt ON ConversationTurnLogs (SourceTool, SessionId, CreatedAt);
";
    await command.ExecuteNonQueryAsync();

    if (await ColumnExistsAsync(connection, "ConversationTurnLogs", "AssistantOutputPlainText"))
    {
        command.CommandText = "ALTER TABLE ConversationTurnLogs DROP COLUMN AssistantOutputPlainText;";
        await command.ExecuteNonQueryAsync();
    }

    if (!await ColumnExistsAsync(connection, "ConversationTurnLogs", "UserCreatedAt"))
    {
        command.CommandText = "ALTER TABLE ConversationTurnLogs ADD COLUMN UserCreatedAt TEXT NULL;";
        await command.ExecuteNonQueryAsync();
    }

    if (!await ColumnExistsAsync(connection, "ConversationTurnLogs", "ConversationTitle"))
    {
        command.CommandText = "ALTER TABLE ConversationTurnLogs ADD COLUMN ConversationTitle TEXT NOT NULL DEFAULT '';";
        await command.ExecuteNonQueryAsync();
    }
}

static async Task<bool> ColumnExistsAsync(DbConnection connection, string tableName, string columnName)
{
    await using var command = connection.CreateCommand();
    command.CommandText = $"PRAGMA table_info({tableName})";
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        if (string.Equals(reader[1]?.ToString(), columnName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    return false;
}

public partial class Program;
