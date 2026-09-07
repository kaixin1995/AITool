using AITool.Core.Services;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Proxy;

namespace AITool.Core;

/// <summary>
/// Core 代理事件接线（构建完成后调用）：把追踪完成/熔断/路由回退三类事件连接到
/// Core 事件总线发布器。供 Core 宿主与 AllInOne 单进程宿主复用。
/// </summary>
public static class CoreProxyEventWiring
{
    public static void WireProxyEvents(IServiceProvider services)
    {
        // 将开发者追踪存储的完成事件连接到事件发布器。
        // Store 的 OnTraceCompleted 事件在追踪记录完成时触发，
        // Publisher 接收后异步发布 developer-trace 事件到 Core 事件总线。
        // 使用 fire-and-forget 模式，发布失败不影响代理主流程。
        {
            var traceStore = services.GetRequiredService<DeveloperInvocationTraceStore>();
            var tracePublisher = services.GetRequiredService<CoreUnifiedProxyEventPublisher>();
            var tracePublishLogger = services.GetRequiredService<ILoggerFactory>().CreateLogger("CoreUnifiedProxyEventPublish");
            traceStore.OnTraceCompleted += entry =>
            {
                // fire-and-forget：追踪事件发布是辅助链路，不应阻塞代理主流程
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await tracePublisher.PublishAsync(entry);
                    }
                    catch (Exception ex)
                    {
                        tracePublishLogger.LogWarning(ex, "发布开发者追踪事件失败，不影响代理主流程。TraceId={TraceId}", entry.TraceId);
                    }
                });
            };
        }

        // 将熔断状态存储的首次熔断事件连接到事件发布器。
        // RouteCircuitStateStore 的 OnCircuitOpened 事件在路由首次触发熔断时触发，
        // Publisher 接收后异步发布 circuit-breaker 事件到 Core 事件总线。
        // 使用 fire-and-forget 模式，发布失败不影响代理主流程。
        {
            var circuitStore = services.GetRequiredService<RouteCircuitStateStore>();
            var circuitPublisher = services.GetRequiredService<CoreCircuitBreakerEventPublisher>();
            var circuitPublishLogger = services.GetRequiredService<ILoggerFactory>().CreateLogger("CoreCircuitBreakerEventPublish");
            circuitStore.OnCircuitOpened += (sender, args) =>
            {
                // fire-and-forget：熔断事件发布是辅助链路，不应阻塞代理主流程
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await circuitPublisher.PublishAsync(args);
                    }
                    catch (Exception ex)
                    {
                        circuitPublishLogger.LogWarning(ex, "发布熔断状态变更事件失败，不影响代理主流程。RouteId={RouteId}", args.RouteId);
                    }
                });
            };
        }
    }
}