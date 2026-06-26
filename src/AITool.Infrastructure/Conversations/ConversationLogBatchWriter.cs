using System.Threading.Channels;
using AITool.Application.Conversations;
using AITool.Domain.Proxy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Conversations;

/// <summary>
/// 后台批量写入结构化对话记录，避免主请求线程等待 SQLite 写锁。
/// </summary>
public sealed class ConversationLogBatchWriter : BackgroundService
{
    private const int MaxBatchSize = 100;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(800);
    /// <summary>
    /// 写入队列容量。每个 entry 含归一化后的用户输入与 AI 输出（最长约 24KB），
    /// 容量过大时若落盘跟不上会导致积压内存飙升（曾因 4096 + 无截断 diff 达到数百 MB）。
    /// 512 配合 800ms/100 条的刷新节奏足以平滑突发流量，DropWrite 保证不阻塞代理主链路。
    /// </summary>
    private const int ChannelCapacity = 512;
    private readonly Channel<ConversationTurnEntry> _channel = Channel.CreateBounded<ConversationTurnEntry>(new BoundedChannelOptions(ChannelCapacity)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly IConversationLogStore _conversationLogStore;
    private readonly ILogger<ConversationLogBatchWriter> _logger;
    private readonly bool _writeThroughMode;

    public ConversationLogBatchWriter(
        IConversationLogStore conversationLogStore,
        ILogger<ConversationLogBatchWriter> logger,
        IHostEnvironment hostEnvironment)
    {
        _conversationLogStore = conversationLogStore;
        _logger = logger;
        _writeThroughMode = hostEnvironment.IsEnvironment("Testing");
    }

    public async ValueTask<bool> EnqueueAsync(ConversationTurnEntry entry, CancellationToken cancellationToken)
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

        _logger.LogWarning("对话记录队列已满，本次记录已让步丢弃");
        return false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var buffer = new List<ConversationTurnEntry>(MaxBatchSize);

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
                _logger.LogError(ex, "后台批量写入对话记录失败");
            }
        }

        await DrainRemainingEntriesAsync();
    }

    /// <summary>
    /// 服务优雅停止时尽量把队列里剩余的记录落盘，降低批量写入导致的数据丢失窗口。
    /// </summary>
    private async Task DrainRemainingEntriesAsync()
    {
        var buffer = new List<ConversationTurnEntry>(MaxBatchSize);
        try
        {
            while (_channel.Reader.TryRead(out var entry))
            {
                buffer.Add(entry);
                if (buffer.Count < MaxBatchSize)
                {
                    continue;
                }

                await FlushBatchAsync(buffer, CancellationToken.None);
                buffer.Clear();
            }

            if (buffer.Count > 0)
            {
                await FlushBatchAsync(buffer, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停止服务时刷新剩余对话记录失败");
        }
    }

    private async Task FlushBatchAsync(List<ConversationTurnEntry> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        var logs = batch.Select(entry => new ConversationTurnLog
        {
            RequestId = entry.RequestId,
            CreatedAt = entry.CreatedAt,
            UserCreatedAt = entry.UserCreatedAt,
            SourceTool = entry.SourceTool,
            SessionId = entry.SessionId,
            ConversationGroupKey = entry.ConversationGroupKey,
            AccessKeyId = entry.AccessKeyId,
            RequestModel = entry.RequestModel,
            ProtocolType = entry.ProtocolType,
            RequestPath = entry.RequestPath,
            Source = entry.Source,
            UserInputText = GzipTextCompression.Compress(entry.UserInputText),
            AssistantOutputMarkdown = GzipTextCompression.Compress(entry.AssistantOutputMarkdown),
            InputTokens = entry.InputTokens,
            CachedTokens = entry.CachedTokens,
            OutputTokens = entry.OutputTokens,
            IsStreaming = entry.IsStreaming,
            Status = entry.Status,
            MetadataJson = entry.MetadataJson,
            ConversationTitle = entry.ConversationTitle
        }).ToList();

        await _conversationLogStore.AppendBatchAsync(logs, cancellationToken);
    }
}
