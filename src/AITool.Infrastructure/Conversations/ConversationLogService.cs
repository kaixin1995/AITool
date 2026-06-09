using AITool.Application.Conversations;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Conversations;

/// <summary>
/// 结构化对话记录服务，采用后台批量刷盘方式写入数据库。
/// </summary>
public sealed class ConversationLogService : IConversationLogService
{
    private readonly ConversationLogBatchWriter _batchWriter;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CoreConversationEventPublisher _eventPublisher;
    private readonly ILogger<ConversationLogService> _logger;

    /// <summary>
    /// 初始化结构化对话记录服务。
    /// </summary>
    public ConversationLogService(
        ConversationLogBatchWriter batchWriter,
        IServiceScopeFactory scopeFactory,
        CoreConversationEventPublisher eventPublisher,
        ILogger<ConversationLogService> logger)
    {
        _batchWriter = batchWriter;
        _scopeFactory = scopeFactory;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    /// <summary>
    /// 对话主链路先保留现有本地存储行为，再旁路发布一份 Core 事件。
    /// 这样后续接入 Admin 事件消费时，不会影响当前对话记录页面和历史数据保留策略。
    /// </summary>
    public async Task LogAsync(ConversationTurnEntry entry, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settings = await dbContext.SystemRuntimeSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (settings is not null && !settings.ConversationLogEnabled)
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
