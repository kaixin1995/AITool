using AITool.Infrastructure.Persistence;
using Hangfire;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 注册 Razor Pages，作为管理后台的页面框架
builder.Services.AddRazorPages();

// 注册 EF Core SQLite 数据库上下文
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=aitool.db";
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(connectionString));

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
