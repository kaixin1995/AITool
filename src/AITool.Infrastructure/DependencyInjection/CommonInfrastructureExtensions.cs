using AITool.Infrastructure.Hosting;
using AITool.Infrastructure.Proxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Infrastructure.DependencyInjection;

/// <summary>
/// 所有宿主（Admin、Core、AllInOne）共享的基础设施服务注册扩展方法。
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
    /// 包括：控制器（含异常日志过滤器）、内存缓存、异常过滤器自身，
    /// 以及代理流式增强的可选配置（心跳/可恢复缓冲——任何可能挂载 Core 代理
    /// 控制器的宿主都需可解析；Core 宿主稍后会以自己的配置覆盖注册）。
    /// AppVersionInfo 因包含版本号参数，由各宿主在调用本方法之前自行注册。
    /// </para>
    /// </summary>
    public static IServiceCollection AddCommonInfrastructure(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        // 注册 API 控制器，统一挂载 HTTP 异常日志过滤器。
        services.AddControllers(options =>
        {
            options.Filters.Add<HttpExceptionLoggingFilter>();
        });

        services.AddMemoryCache();
        services.AddScoped<HttpExceptionLoggingFilter>();

        // 流式增强默认配置：任何宿主均可解析（Core 宿主以自身注册覆盖，行为以其配置为准）。
        var config = configuration ?? new ConfigurationBuilder().Build();
        services.AddSingleton(new SseHeartbeatOptions(
            config.GetValue("ProxyForwarding:SseHeartbeatSeconds", 15)));
        services.AddSingleton(new StreamResumeOptions
        {
            Enabled = config.GetValue("ProxyForwarding:StreamResumeEnabled", true),
            MaxFrameBytesPerRequest = config.GetValue("ProxyForwarding:StreamResumeMaxFrameBytes", StreamResumeStore.DefaultMaxFrameBytesPerRequest),
            MaxActiveStreams = config.GetValue("ProxyForwarding:StreamResumeMaxActive", StreamResumeStore.DefaultMaxActiveStreams),
            RetainMinutes = config.GetValue("ProxyForwarding:StreamResumeRetainMinutes", 5)
        });
        services.AddSingleton(new StreamResumeStore(
            config.GetValue("ProxyForwarding:StreamResumeMaxFrameBytes", StreamResumeStore.DefaultMaxFrameBytesPerRequest),
            config.GetValue("ProxyForwarding:StreamResumeMaxActive", StreamResumeStore.DefaultMaxActiveStreams),
            TimeSpan.FromMinutes(config.GetValue("ProxyForwarding:StreamResumeRetainMinutes", 5))));

        return services;
    }
}
