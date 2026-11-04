using Microsoft.AspNetCore.Http;

namespace AITool.Web.Services;

/// <summary>
/// 为代理控制器在流中断或异常收尾时补发 SSE 终止符提供有界的、尽力而为的写入能力。
/// </summary>
internal static class SseTerminatorWriter
{
    internal static readonly TimeSpan DefaultWriteBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 尝试向客户端写入并刷新终止符（如 data: [DONE]\n\n 或 event: message_stop...）。
    /// 链接客户端请求的 RequestAborted 令牌并施加有界超时预算：
    /// 客户端已断开时立即放弃，客户端存活但停读时至多等待预算耗尽，安静吞异常收口。
    /// </summary>
    public static async Task TryWriteTerminatorAsync(
        this HttpResponse response,
        string payload,
        CancellationToken requestAborted,
        TimeSpan? budget = null)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return;
        }

        using var writeBudget = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        writeBudget.CancelAfter(budget ?? DefaultWriteBudget);
        try
        {
            await response.WriteAsync(payload, writeBudget.Token);
            await response.Body.FlushAsync(writeBudget.Token);
        }
        catch
        {
            /* 终止符尽力而为：客户端已断开或预算耗尽时安静收口 */
        }
    }
}
