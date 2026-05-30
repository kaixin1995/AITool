using AITool.Application.Conversations;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Conversations;

/// <summary>
/// 结构化对话记录服务，采用后台批量刷盘方式写入数据库。
/// </summary>
public sealed class ConversationLogService : IConversationLogService
{
    private readonly ConversationLogBatchWriter _batchWriter;
    private readonly ILogger<ConversationLogService> _logger;

    public ConversationLogService(ConversationLogBatchWriter batchWriter, ILogger<ConversationLogService> logger)
    {
        _batchWriter = batchWriter;
        _logger = logger;
    }

    public async Task LogAsync(ConversationTurnEntry entry, CancellationToken cancellationToken = default)
    {
        var accepted = await _batchWriter.EnqueueAsync(entry, cancellationToken);
        if (!accepted)
        {
            _logger.LogWarning("对话记录入队失败，请求已继续。SourceTool={SourceTool}, SessionId={SessionId}", entry.SourceTool, entry.SessionId);
        }
    }
}
