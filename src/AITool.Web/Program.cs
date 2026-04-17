using AITool.Application.Detection;
using AITool.Application.Routing;
using AITool.Application.SiteCatalog;
using AITool.Infrastructure.OpenAI;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Routing;
using AITool.Infrastructure.Scheduling;
using Hangfire;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 注册 Razor Pages，作为管理后台的页面框架
builder.Services.AddRazorPages();

// 注册 EF Core SQLite 数据库上下文
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=aitool.db";
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(connectionString));

// 注册站点目录客户端，用于拉取远程站点模型列表
builder.Services.AddHttpClient<ISiteCatalogClient, OpenAiSiteCatalogClient>();

// 注册模型探测服务，用于检测模型可用性
builder.Services.AddHttpClient<IModelProbeService, OpenAiModelProbeService>();

// 注册路由选择服务，用于根据优先级匹配代理路由
builder.Services.AddScoped<IRouteSelectionService, RouteSelectionService>();

// 注册 Hangfire 检测调度器
builder.Services.AddSingleton<HangfireDetectionScheduler>();

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

// 映射健康检查端点，作为集成测试的验证入口
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// 启用 Hangfire 仪表盘，仅限本地访问
app.UseHangfireDashboard("/hangfire");

// 映射 Razor Pages 路由
app.MapRazorPages();

app.Run();

// 暴露 Program 类供集成测试引用
public partial class Program;
