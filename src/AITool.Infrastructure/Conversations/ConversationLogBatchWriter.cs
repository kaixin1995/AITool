using System.Threading.Channels;
using AITool.Application.Conversations;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly Channel<ConversationTurnEntry> _channel = Channel.CreateBounded<ConversationTurnEntry>(new BoundedChannelOptions(4096)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ConversationLogBatchWriter> _logger;
    private readonly bool _writeThroughMode;

    public ConversationLogBatchWriter(IServiceScopeFactory scopeFactory, ILogger<ConversationLogBatchWriter> logger, IHostEnvironment hostEnvironment)
    {
        _scopeFactory = scopeFactory;
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
    }

    private async Task FlushBatchAsync(List<ConversationTurnEntry> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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
            MetadataJson = entry.MetadataJson
        }).ToList();

        dbContext.ConversationTurnLogs.AddRange(logs);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
