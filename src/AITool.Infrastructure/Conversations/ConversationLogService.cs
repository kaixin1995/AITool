using AITool.Application.Conversations;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Proxy;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Conversations;

/// <summary>
/// 结构化对话记录服务，采用后台批量刷盘方式写入本地 JSONL 存储。
/// ConversationLogEnabled 开关通过 ProxyRequestMetadataCache 读取（5 秒 TTL 缓存，
/// Admin 宿主从 DB 读取，Core 宿主从配置快照读取），不再每次创建 DB scope。
/// </summary>
public sealed class ConversationLogService : IConversationLogService
{
    private readonly ConversationLogBatchWriter _batchWriter;
    private readonly ProxyRequestMetadataCache _metadataCache;
    private readonly CoreConversationEventPublisher _eventPublisher;
    private readonly ILogger<ConversationLogService> _logger;

    /// <summary>
    /// 初始化结构化对话记录服务。
    /// </summary>
    public ConversationLogService(
        ConversationLogBatchWriter batchWriter,
        ProxyRequestMetadataCache metadataCache,
        CoreConversationEventPublisher eventPublisher,
        ILogger<ConversationLogService> logger)
    {
        _batchWriter = batchWriter;
        _metadataCache = metadataCache;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    /// <summary>
    /// 对话记录通过后台批量写入器刷盘，同时发布 Core 事件。
    /// </summary>
    public async Task LogAsync(ConversationTurnEntry entry, CancellationToken cancellationToken = default)
    {
        // 通过 ProxyRequestMetadataCache 读取 ConversationLogEnabled。
        // 该缓存 5 秒 TTL，Admin 宿主从 DB 读，Core 宿主从配置快照读。
        var runtimeSettings = await _metadataCache.GetRuntimeSettingsAsync(cancellationToken);
        if (!runtimeSettings.ConversationLogEnabled)
        {
            return;
        }

        var accepted = await _batchWriter.EnqueueAsync(entry, cancellationToken);
        if (!accepted)
        {
            _logger.LogWarning("对话记录入队失败，请求已继续。SourceTool={SourceTool}, SessionId={SessionId}", entry.SourceTool, entry.SessionId);
        }

        await _eventPublisher.PublishAsync(entry, cancellationToken);
    }
}
