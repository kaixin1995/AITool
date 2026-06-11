using AITool.Application.Conversations;
using AITool.Infrastructure.Conversations;
using AITool.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Infrastructure.DependencyInjection;

/// <summary>
/// 所有宿主（Web、Admin、Core）共享的基础设施服务注册扩展方法。
/// <para>
/// 这些服务是框架级别的基础依赖：版本信息、内存缓存、异常过滤器、
/// 控制器基础配置、对话日志文件存储等。任何一个宿主启动都需要它们。
/// </para>
/// </summary>
public static class CommonInfrastructureExtensions
{
    /// <summary>
    /// 注册所有宿主共享的基础设施服务。
    /// <para>
    /// 包括：控制器（含异常日志过滤器）、内存缓存、异常过滤器自身、
    /// 对话日志文件存储与提取服务。AppVersionInfo 因包含版本号参数，
    /// 由各宿主在调用本方法之前自行注册。
    /// </para>
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="conversationLogRootPath">对话日志 JSONL 文件的存储根路径。</param>
    public static IServiceCollection AddCommonInfrastructure(
        this IServiceCollection services,
        string conversationLogRootPath)
    {
        // 注册 API 控制器，统一挂载 HTTP 异常日志过滤器。
        services.AddControllers(options =>
        {
            options.Filters.Add<HttpExceptionLoggingFilter>();
        });

        services.AddMemoryCache();
        services.AddScoped<HttpExceptionLoggingFilter>();

        // 注册对话日志文件存储选项。测试环境应传入随机临时目录以确保隔离。
        services.AddSingleton(new ConversationLogFileOptions
        {
            RootPath = conversationLogRootPath
        });
        services.AddSingleton<IConversationLogStore, FileConversationLogStore>();
        services.AddSingleton<ConversationExtractionService>();

        return services;
    }
}
