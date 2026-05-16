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
    /// 字段 MaxBatchSize。
    /// </summary>
    private const int MaxBatchSize = 100;
    /// <summary>
    /// 方法 FromMilliseconds。
    /// </summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(800);
    /// <summary>
    /// 方法 BoundedChannelOptions。
    /// </summary>
    private readonly Channel<UsageLogEntry> _channel = Channel.CreateBounded<UsageLogEntry>(new BoundedChannelOptions(4096)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });
    /// <summary>
    /// 字段 _scopeFactory。
    /// </summary>
    private readonly IServiceScopeFactory _scopeFactory;
    /// <summary>
    /// 字段 _logger。
    /// </summary>
    private readonly ILogger<ProxyUsageLogBatchWriter> _logger;
    /// <summary>
    /// 字段 _writeThroughMode。
    /// </summary>
    private readonly bool _writeThroughMode;

    /// <summary>
    /// 初始化 ProxyUsageLogBatchWriter。
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
    /// 方法 ExecuteAsync。
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
    }

    /// <summary>
    /// 方法 FlushBatchAsync。
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
            ReasoningEffort = entry.ReasoningEffort
        }).ToList();

        dbContext.ProxyUsageLogs.AddRange(logs);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
