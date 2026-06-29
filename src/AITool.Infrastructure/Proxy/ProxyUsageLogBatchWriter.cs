using System.Threading.Channels;
using AITool.Application.UsageLogs;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 将代理日志写入降级为后台批量刷盘，避免主请求线程同步等待 SQLite 写锁。
/// </summary>
public sealed class ProxyUsageLogBatchWriter : BackgroundService
{
    /// <summary>
    /// 单次批量写入的最大日志条数
    /// </summary>
    private const int MaxBatchSize = 100;
    /// <summary>
    /// 后台刷盘的聚合等待间隔
    /// </summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(800);
    /// <summary>
    /// 有界通道，用于在生产者与后台消费者之间缓冲日志条目
    /// </summary>
    private readonly Channel<UsageLogEntry> _channel = Channel.CreateBounded<UsageLogEntry>(new BoundedChannelOptions(4096)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });
    /// <summary>
    /// 服务范围工厂，用于每次刷盘时创建独立的 DI 作用域
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;
    /// <summary>
    /// 日志记录器，用于记录批量写入异常和队列溢出警告
    /// </summary>
    private readonly ILogger<ProxyUsageLogBatchWriter> _logger;
    /// <summary>
    /// 直写模式标志，测试环境下跳过队列直接写入数据库
    /// </summary>
    private readonly bool _writeThroughMode;

    /// <summary>
    /// 注入服务范围工厂、日志记录器和主机环境信息
    /// </summary>
    public ProxyUsageLogBatchWriter(IServiceScopeFactory scopeFactory, ILogger<ProxyUsageLogBatchWriter> logger, IHostEnvironment hostEnvironment)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _writeThroughMode = hostEnvironment.IsEnvironment("Testing");
    }

    /// <summary>
    /// 代理链路只尝试入队，不等待数据库写入完成。
    /// </summary>
    public async ValueTask<bool> EnqueueAsync(UsageLogEntry entry, CancellationToken cancellationToken)
    {
        if (_writeThroughMode)
        {
            await FlushBatchAsync([entry], cancellationToken);
            return true;
        }

        if (_channel.Writer.TryWrite(entry))
        {
            return true;
        }

        _logger.LogWarning("代理日志队列已满，本次日志已让步丢弃");
        return false;
    }

    /// <summary>
    /// 后台主循环，持续从通道中读取日志条目并按批次刷盘
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var buffer = new List<UsageLogEntry>(MaxBatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var hasItem = await _channel.Reader.WaitToReadAsync(stoppingToken);
                if (!hasItem)
                {
                    continue;
                }

                buffer.Clear();
                while (buffer.Count < MaxBatchSize && _channel.Reader.TryRead(out var entry))
                {
                    buffer.Add(entry);
                }

                // 给同一批次一个很短的聚合窗口，减少小批量频繁刷盘。
                var delayTask = Task.Delay(FlushInterval, stoppingToken);
                while (buffer.Count < MaxBatchSize)
                {
                    var readTask = _channel.Reader.WaitToReadAsync(stoppingToken).AsTask();
                    var completed = await Task.WhenAny(readTask, delayTask);
                    if (completed == delayTask || !readTask.Result)
                    {
                        break;
                    }

                    while (buffer.Count < MaxBatchSize && _channel.Reader.TryRead(out var delayedEntry))
                    {
                        buffer.Add(delayedEntry);
                    }
                }

                await FlushBatchAsync(buffer, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "后台批量写入代理日志失败");
            }
        }

        // 服务优雅停止时尽量把队列里剩余的记录落盘，降低数据丢失窗口。
        await DrainRemainingEntriesAsync();
    }

    /// <summary>
    /// 服务优雅停止时尽量把队列里剩余的记录落盘，降低数据丢失窗口。
    /// </summary>
    private async Task DrainRemainingEntriesAsync()
    {
        var buffer = new List<UsageLogEntry>(MaxBatchSize);
        try
        {
            while (_channel.Reader.TryRead(out var entry))
            {
                buffer.Add(entry);
                if (buffer.Count < MaxBatchSize) continue;
                await FlushBatchAsync(buffer, CancellationToken.None);
                buffer.Clear();
            }
            if (buffer.Count > 0) await FlushBatchAsync(buffer, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "停止时排空代理日志队列失败，部分日志可能丢失");
        }
    }

    /// <summary>
    /// 将一批日志条目通过独立作用域写入数据库
    /// </summary>
    private async Task FlushBatchAsync(List<UsageLogEntry> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logs = batch.Select(entry => new ProxyUsageLog
        {
            RequestId = entry.RequestId,
            AccessKeyId = entry.AccessKeyId,
            ProtocolType = entry.ProtocolType,
            ForwardingMode = entry.ForwardingMode,
            RequestModel = entry.RequestModel,
            AttemptedModel = entry.AttemptedModel,
            TargetSiteId = entry.TargetSiteId,
            Status = entry.Status,
            Source = entry.Source,
            RetryCount = entry.RetryCount,
            AttemptIndex = entry.AttemptIndex,
            IsFinalResult = entry.IsFinalResult,
            FallbackTriggered = entry.FallbackTriggered,
            ErrorMessage = entry.ErrorMessage,
            InputTokens = entry.InputTokens,
            CachedTokens = entry.CachedTokens,
            OutputTokens = entry.OutputTokens,
            TotalTokens = entry.InputTokens + entry.CachedTokens + entry.OutputTokens,
            IsStreaming = entry.IsStreaming,
            IsStreamInterrupted = entry.IsStreamInterrupted,
            FirstTokenLatencyMs = entry.FirstTokenLatencyMs,
            StreamDurationMs = entry.StreamDurationMs,
            TotalDurationMs = entry.TotalDurationMs,
            ReasoningEffort = entry.ReasoningEffort,
            RequestedAt = entry.RequestedAt
        }).ToList();

        await dbContext.InsertRangeAsync(logs, cancellationToken);
    }
}
