using AITool.Infrastructure.CoreRuntime;
using Microsoft.AspNetCore.Mvc;

namespace AITool.Core.Controllers.Core;

/// <summary>
/// Core 事件 SSE 实时推送端点。
/// Admin 宿主通过此端点建立长连接，当 Core 有新事件写入 spool 后，
/// 立即收到通知并触发 PullAndProcessAsync 拉取，避免固定 10 秒轮询延迟。
/// <para>
/// SSE 推送的仅是"有新事件"的轻量信号（携带最新序号），不传输完整事件载荷。
/// Admin 收到通知后仍然通过 replay 端点拉取完整事件数据，保证数据完整性和可靠性。
/// </para>
/// </summary>
[ApiController]
[Route("api/core/events")]
public sealed class CoreEventStreamController : ControllerBase
{
    private readonly CoreAdminEventBus _eventBus;
    private readonly ILogger<CoreEventStreamController> _logger;

    /// <summary>
    /// 初始化 SSE 事件流控制器。
    /// </summary>
    public CoreEventStreamController(
        CoreAdminEventBus eventBus,
        ILogger<CoreEventStreamController> logger)
    {
        _eventBus = eventBus;
        _logger = logger;
    }

    /// <summary>
    /// SSE 事件流端点。
    /// 返回 text/event-stream 响应，当 Core 有新事件写入 spool 时推送通知。
    /// <para>
    /// 推送格式遵循 SSE 标准：以 "data:" 开头，两个换行符结尾。
    /// 每条消息是一个 JSON 对象，包含 latestSequenceId 字段表示当前最新事件序号。
    /// </para>
    /// <para>
    /// 同时每 30 秒发送一次心跳注释行（": heartbeat"），
    /// 防止中间代理或负载均衡器因空闲超时关闭连接。
    /// </para>
    /// <para>
    /// SSE 连接支持多个 Admin 实例同时订阅，每个连接独立监听通知通道。
    /// 通知通道使用 DropOldest 策略，慢消费者不会阻塞其他客户端。
    /// </para>
    /// </summary>
    [HttpGet("stream")]
    public async Task Stream(CancellationToken cancellationToken)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";
        // 禁用响应缓冲，确保每条 SSE 消息立即推送到客户端
        Response.Headers.Append("X-Accel-Buffering", "no");

        // 使用 HttpContext.RequestAborted 作为客户端断连的信号，
        // 同时也接受服务端关闭信号 cancellationToken
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, HttpContext.RequestAborted);
        var token = linkedCts.Token;

        _logger.LogDebug("Admin SSE 客户端已连接，开始推送事件通知");

        // 为当前 SSE 连接创建独立的订阅通道
        // 使用 TryRead 循环而非 ReadAllAsync，避免消费共享通知通道中的数据
        using var subscription = _eventBus.Subscribe();

        try
        {
            while (!token.IsCancellationRequested)
            {
                // 等待新事件通知，带超时以定期发送心跳
                var sequenceId = await WaitForNotificationAsync(subscription, token);

                if (sequenceId.HasValue)
                {
                    // SSE data 行：通知 Admin 有新事件可拉取
                    await Response.WriteAsync(
                        $"data: {{\"latestSequenceId\":{sequenceId.Value}}}\n\n",
                        cancellationToken: token);
                    await Response.Body.FlushAsync(token);
                }
                else
                {
                    // 超时无数据，发送心跳保持连接
                    await Response.WriteAsync(": heartbeat\n\n", cancellationToken: token);
                    await Response.Body.FlushAsync(token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Admin 断开连接或服务端关闭，正常结束
            _logger.LogDebug("Admin SSE 客户端断开连接");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SSE 事件流异常结束");
        }
    }

    /// <summary>
    /// 等待新事件通知，最多等待 30 秒。
    /// 超时返回 null 表示需要发送心跳，收到通知返回最新序号。
    /// </summary>
    private static async Task<long?> WaitForNotificationAsync(
        CoreAdminEventBus.SseSubscription subscription,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            return await subscription.WaitNextAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 仅超时取消，主取消令牌未触发
            return null;
        }
    }
}
