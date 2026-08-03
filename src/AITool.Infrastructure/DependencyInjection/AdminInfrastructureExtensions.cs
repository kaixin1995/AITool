using AITool.Application.Operations;
using AITool.Application.Proxy;
using AITool.Application.SiteCatalog;
using AITool.Application.UsageLogs;
using AITool.Infrastructure.Hosting;
using AITool.Infrastructure.Health;
using AITool.Infrastructure.OpenAI;
using AITool.Infrastructure.Operations;
using AITool.Infrastructure.Persistence;
using AITool.Infrastructure.Proxy;
using AITool.Infrastructure.Scheduling;
using Hangfire;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Infrastructure.DependencyInjection;

/// <summary>
/// Web + Admin 宿主共享的管理后台基础设施服务注册扩展方法。
/// <para>
/// 这些服务与后台管理、数据库访问相关：Razor Pages、Cookie 认证、
/// EF Core 数据库上下文、Hangfire 调度器、站点目录客户端等。
/// Core 宿主不使用数据库也不提供管理页面，因此不调用本方法。
/// </para>
/// </summary>
public static class AdminInfrastructureExtensions
{
    /// <summary>
    /// 注册 Web 和 Admin 宿主共享的管理后台基础设施服务。
    /// <para>
    /// 包括：Razor Pages、Cookie 认证与授权、EF Core 数据库上下文、
    /// Hangfire 内存存储与调度器、站点目录客户端、系统运行时设置服务。
    /// </para>
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="connectionString">SQLite 数据库连接字符串。</param>
    public static IServiceCollection AddAdminInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        // Razor Pages 已移除（纯 SPA 前端）。静态文件服务由 Admin 宿主 Program.cs 的 UseStaticFiles 提供。

        // 认证授权由 Admin 宿主的 Program.cs 配置（JWT Bearer）。
        // Infrastructure 层不再绑定具体认证方案，保持与宿主解耦。
        // 代理端点 /v1/* 不走 ASP.NET 认证（自己用 AccessKey 校验）。

        // 注册 SqlSugar 数据库访问层（替代原 EF Core）。
        // SqlSugarSetup.AddSqlSugar 注册 SqlSugarScope（线程安全单例）+ AppDbContext（Scoped 适配）。
        // WAL/synchronous/cache_size/busy_timeout 等 PRAGMA 由 SqlSugarSetup.InitializeDatabase 在启动时执行。
        services.AddSqlSugar(connectionString);

        // 注册系统运行时设置服务，管理持久化的超时、重试和日志保留配置。
        services.AddScoped<ISystemRuntimeSettingsService, SystemRuntimeSettingsService>();

        // 注册站点目录客户端，用于拉取远程站点模型列表。
        services.AddHttpClient<ISiteCatalogClient, OpenAiSiteCatalogClient>();

        // 注册 Hangfire 内存存储与调度器。
        services.AddHangfire(config => config
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseInMemoryStorage());
        services.AddHangfireServer();
        services.AddSingleton<HangfireDetectionScheduler>();

        // 注册模型检测所需的转发与日志写入链路。
        // ProxyForwardService 是无状态 HttpClient 转发器（不依赖 Core 运行时配置快照、并发、熔断），
        // 可在管理后台宿主独立工作。UsageLog 通过批量写入器直接落库到 Admin 本地 SQLite。
        // 这同时修复了 Detection 页面点击无响应的问题：此前 ModelHealthRequestService 及其依赖均未注册，
        // GetRequiredService 抛出异常导致 /api/admin/detection/probe/* 全部返回 500。
        services.AddHttpClient<IProxyForwardService, ProxyForwardService>();
        // SiteUsageTracker 被 ProxyUsageLogBatchWriter 依赖（Codex 巡检读它判断账号活跃度）。
        services.AddSingleton<SiteUsageTracker>();
        services.AddSingleton<ProxyUsageLogBatchWriter>();
        services.AddHostedService(sp => sp.GetRequiredService<ProxyUsageLogBatchWriter>());
        services.AddSingleton<IUsageLogService, UsageLogService>();
        services.AddScoped<ModelHealthRequestService>();

        return services;
    }
}
