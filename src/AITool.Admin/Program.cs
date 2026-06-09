using AppVersionInfo = AITool.Infrastructure.Hosting.AppVersionInfo;
using HttpExceptionLoggingFilter = AITool.Infrastructure.Hosting.HttpExceptionLoggingFilter;
using HttpLogFormatter = AITool.Infrastructure.Hosting.HttpLogFormatter;
using AITool.Application.Common;
using AITool.Application.Operations;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Hosting;
using AITool.Infrastructure.Operations;
using AITool.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.Cookies;
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

// Admin 独立宿主当前阶段先保留最小页面与控制器框架，后续再逐步把真实 /Admin/* 页面迁进来。
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

// 当前数据库仍由 Admin 宿主使用，保证历史 UsageLogs、Conversations、Detection 等数据能够继续使用。
var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aitool.db");
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? $"Data Source={Path.GetFullPath(dbPath)}";
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(connectionString));

builder.Services.AddScoped<ISystemRuntimeSettingsService, SystemRuntimeSettingsService>();

// Admin 通过最小 Core 客户端与核心宿主通信。当前阶段先提供握手、full-sync、ack、replay 这几项最关键能力。
var coreBaseUrl = builder.Configuration["CoreServer:BaseUrl"] ?? $"http://127.0.0.1:{builder.Configuration.GetValue<int?>("CoreServer:Port") ?? 5029}/";
builder.Services.AddHttpClient<CoreAdminClient>(client =>
{
    client.BaseAddress = new Uri(coreBaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

startupLogger.Info(
    "Admin 宿主启动完成。Version={Version}, Environment={Environment}, Port={Port}, CoreBaseUrl={CoreBaseUrl}",
    applicationVersion,
    app.Environment.EnvironmentName,
    serverPort,
    coreBaseUrl);
Console.WriteLine($"AI Tool Admin 已启动：http://127.0.0.1:{serverPort}");

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapRazorPages();
app.Run();

static bool IsAdminRequest(HttpRequest request)
{
    return request.Path.StartsWithSegments("/Admin", StringComparison.OrdinalIgnoreCase)
        || request.Path.StartsWithSegments("/Login", StringComparison.OrdinalIgnoreCase);
}

public partial class Program;
