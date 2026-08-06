using AITool.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Infrastructure.DependencyInjection;

/// <summary>
/// 所有宿主（Web、Admin、Core）共享的基础设施服务注册扩展方法。
/// <para>
/// 这些服务是框架级别的基础依赖：版本信息、内存缓存、异常过滤器、
/// 控制器基础配置等。任何一个宿主启动都需要它们。
/// </para>
/// </summary>
public static class CommonInfrastructureExtensions
{
    /// <summary>
    /// 注册所有宿主共享的基础设施服务。
    /// <para>
    /// 包括：控制器（含异常日志过滤器）、内存缓存、异常过滤器自身。
    /// AppVersionInfo 因包含版本号参数，由各宿主在调用本方法之前自行注册。
    /// </para>
    /// </summary>
    public static IServiceCollection AddCommonInfrastructure(
        this IServiceCollection services)
    {
        // 注册 API 控制器，统一挂载 HTTP 异常日志过滤器。
        services.AddControllers(options =>
        {
            options.Filters.Add<HttpExceptionLoggingFilter>();
        });

        services.AddMemoryCache();
        services.AddScoped<HttpExceptionLoggingFilter>();

        return services;
    }
}
