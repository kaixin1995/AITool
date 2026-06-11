using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AITool.Admin.Services;

/// <summary>
/// Admin 侧定时事件拉取后台服务。
/// 周期性地创建 <see cref="CoreEventPullService"/> 实例执行拉取 → 消费 → 确认流程。
/// <para>
/// 同时通过 SSE 长连接实时接收 Core 的事件通知，收到通知后立即触发拉取，
/// 将事件处理延迟从最大 10 秒（定时轮询）降低到亚秒级（实时推送）。
/// SSE 断线时自动重连，期间继续依赖定时轮询作为回退。
/// </para>
/// <para>
/// 使用独立的 DI scope 确保每次轮询使用新的数据库上下文和 HTTP 连接。
/// 核心拉取逻辑委托给 <see cref="CoreEventPullService"/>，便于独立测试。
/// </para>
/// </summary>
public sealed class CoreEventPullHostedService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<CoreEventPullHostedService> _logger;

    /// <summary>
    /// SSE 通知触发拉取后，最小等待间隔，避免密集事件导致连续拉取过于频繁。
    /// </summary>
    private static readonly TimeSpan MinPullInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 拉取周期，每 10 秒执行一次（作为 SSE 的回退）。
    /// </summary>
    private static readonly TimeSpan PullInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 启动后首次拉取前的等待时间，确保 Core 宿主可能已经先启动完成。
    /// </summary>
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// SSE 连接失败后的重连间隔，避免频繁重连消耗资源。
    /// </summary>
    private static readonly TimeSpan SseReconnectDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 初始化事件拉取后台服务。
    /// </summary>
    public CoreEventPullHostedService(
        IServiceProvider serviceProvider,
        ILogger<CoreEventPullHostedService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// 后台服务主循环：同时运行定时轮询和 SSE 实时推送两条通道。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后等待一小段时间，确保 Core 宿主可能已经启动完成
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogInformation("Core 事件拉取服务已启动");

        // SSE 通知信号：收到通知时立即触发拉取
        var pullSignal = new SemaphoreSlim(0);

        // 同时启动两条通道：定时轮询（回退）和 SSE 实时推送
        var pollingTask = RunPollingLoopAsync(pullSignal, stoppingToken);
        var sseTask = RunSseListenerAsync(pullSignal, stoppingToken);

        // 等待任意一个完成（通常只有在 stoppingToken 取消时才发生）
        await Task.WhenAll(pollingTask, sseTask);

        _logger.LogInformation("Core 事件拉取服务已停止");
    }

    /// <summary>
    /// 定时轮询循环，作为 SSE 的回退机制。
    /// 即使 SSE 连接中断，定时轮询仍然保证事件最终被拉取。
    /// </summary>
    private async Task RunPollingLoopAsync(SemaphoreSlim pullSignal, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // 等待信号或定时器，取先到者
                var signalOrTimeout = await Task.WhenAny(
                    pullSignal.WaitAsync(stoppingToken),
                    Task.Delay(PullInterval, stoppingToken));

                await PullOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 正常关闭
            }
            catch (Exception ex)
            {
                // 单轮处理失败不影响下一轮
                _logger.LogWarning(ex, "Core 事件拉取处理异常，将在下个周期重试");
                try
                {
                    await Task.Delay(PullInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// SSE 实时监听循环。
    /// 连接 Core 的 SSE 端点，收到新事件通知后释放信号触发即时拉取。
    /// 断线后自动重连，重连期间不影响定时轮询。
    /// </summary>
    private async Task RunSseListenerAsync(SemaphoreSlim pullSignal, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _serviceProvider.CreateAsyncScope();
                var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

                // 创建 SSE 专用 HttpClient，使用无超时配置
                var coreBaseUrl = GetCoreBaseUrl(scope.ServiceProvider);
                using var sseClient = httpClientFactory.CreateClient("CoreSSE");
                sseClient.BaseAddress = new Uri(coreBaseUrl, UriKind.Absolute);
                sseClient.Timeout = Timeout.InfiniteTimeSpan;

                _logger.LogDebug("正在连接 Core SSE 事件通知流：{BaseUrl}api/core/events/stream", coreBaseUrl);

                using var request = new HttpRequestMessage(HttpMethod.Get, "api/core/events/stream");
                using var response = await sseClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    stoppingToken);

                response.EnsureSuccessStatusCode();

                _logger.LogInformation("Core SSE 事件通知流已连接，进入实时监听模式");

                using var stream = await response.Content.ReadAsStreamAsync(stoppingToken);
                using var reader = new StreamReader(stream);

                while (!stoppingToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(stoppingToken);
                    if (line is null)
                    {
                        // 流结束，Core 侧关闭了连接
                        _logger.LogDebug("Core SSE 流关闭，将自动重连");
                        break;
                    }

                    // 跳过空行和注释行（心跳）
                    if (string.IsNullOrEmpty(line) || line.StartsWith(':'))
                    {
                        continue;
                    }

                    // 解析 SSE data 行
                    if (line.StartsWith("data: ", StringComparison.Ordinal))
                    {
                        // 收到通知，释放信号触发即时拉取
                        if (pullSignal.CurrentCount == 0)
                        {
                            pullSignal.Release();
                        }

                        // 最小间隔控制，避免密集通知导致连续拉取
                        await Task.Delay(MinPullInterval, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // 正常关闭
                return;
            }
            catch (Exception ex)
            {
                // SSE 连接失败，等待后重连，不影响定时轮询
                _logger.LogDebug(ex, "Core SSE 连接异常，{ReconnectDelay}秒后重连", SseReconnectDelay.TotalSeconds);
            }

            // 重连前等待
            try
            {
                await Task.Delay(SseReconnectDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 执行一次事件拉取。
    /// 创建独立的 DI scope，确保数据库上下文和 HTTP 连接不跨周期复用。
    /// </summary>
    private async Task PullOnceAsync(CancellationToken stoppingToken)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var pullService = ActivatorUtilities.CreateInstance<CoreEventPullService>(
            scope.ServiceProvider);

        await pullService.PullAndProcessAsync(stoppingToken);
    }

    /// <summary>
    /// 获取 Core 宿主基础地址。
    /// </summary>
    private static string GetCoreBaseUrl(IServiceProvider serviceProvider)
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        return configuration["CoreServer:BaseUrl"]
            ?? $"http://127.0.0.1:{configuration.GetValue<int?>("CoreServer:Port") ?? 5029}/";
    }
}
