using System.Text;
using Microsoft.AspNetCore.Http;

namespace AITool.Web.Services;

/// <summary>
/// 为代理控制器在流中断或异常收尾时补发 SSE 终止符提供有界的、尽力而为的写入能力。
/// </summary>
internal static class SseTerminatorWriter
{
    internal static readonly TimeSpan DefaultWriteBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 尝试向客户端写入并刷新终止符。预算耗尽或请求取消时中止连接，
    /// 不等待可能忽略取消的响应流；在途操作结束前保留其令牌和独立的写入缓冲区。
    /// </summary>
    public static async Task TryWriteTerminatorAsync(
        this HttpResponse response,
        string payload,
        CancellationToken requestAborted,
        TimeSpan? budget = null)
    {
        if (string.IsNullOrEmpty(payload) || requestAborted.IsCancellationRequested)
        {
            return;
        }

        var writeBudget = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        Task? pending = null;
        var disposeBudget = true;
        try
        {
            writeBudget.CancelAfter(budget ?? DefaultWriteBudget);
            var token = writeBudget.Token;
            var stream = response.Body;
            var bytes = Encoding.UTF8.GetBytes(payload);

            // 流的异步方法也可能在返回 Task 之前同步阻塞，因此不要在请求线程上调用。
            pending = Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                await stream.WriteAsync(bytes.AsMemory(), token).ConfigureAwait(false);
            });
            await pending.WaitAsync(token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            pending = Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                await stream.FlushAsync(token).ConfigureAwait(false);
            });
            await pending.WaitAsync(token).ConfigureAwait(false);
        }
        catch
        {
            if (writeBudget.IsCancellationRequested)
            {
                // 中止底层连接，避免超时后继续向客户端输出；不能依赖流配合取消。
                try { response.HttpContext.Abort(); } catch { /* 尽力而为 */ }
            }

            if (pending is { IsCompleted: false })
            {
                // 不再访问响应或启动下一阶段；观察迟到的异常，并在操作结束后释放令牌。
                disposeBudget = false;
                _ = pending.ContinueWith(t =>
                {
                    _ = t.Exception;
                    writeBudget.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            else if (pending is not null)
            {
                _ = pending.Exception;
            }
        }
        finally
        {
            if (disposeBudget)
            {
                writeBudget.Dispose();
            }
        }
    }
}
