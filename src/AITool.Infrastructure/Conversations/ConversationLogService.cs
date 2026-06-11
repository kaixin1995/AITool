using AITool.Application.Conversations;
using AITool.Application.CoreRuntime;
using AITool.Infrastructure.CoreRuntime;
using AITool.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Conversations;

/// <summary>
/// 结构化对话记录服务，采用后台批量刷盘方式写入本地 JSONL 存储。
/// Web/Admin 宿主从数据库查询启用开关，Core 宿主从配置快照获取。
/// </summary>
public sealed class ConversationLogService : IConversationLogService
{
    private readonly ConversationLogBatchWriter _batchWriter;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CoreConversationEventPublisher _eventPublisher;
    private readonly ILogger<ConversationLogService> _logger;
    /// <summary>
    /// 数据库是否可用。Core 宿主不注册 AppDbContext，此字段为 false。
    /// </summary>
    private readonly bool _databaseAvailable;

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
        // 检测 AppDbContext 是否注册。Core 宿主没有数据库，使用配置快照替代。
        using var probeScope = scopeFactory.CreateScope();
        _databaseAvailable = probeScope.ServiceProvider.GetService<AppDbContext>() is not null;
    }

    /// <summary>
    /// 对话主链路先保留现有本地 JSONL 存储行为，再旁路发布一份 Core 事件。
    /// 这样后续接入 Admin 事件消费时，不会影响当前对话记录页面和历史数据保留策略。
    /// </summary>
    public async Task LogAsync(ConversationTurnEntry entry, CancellationToken cancellationToken = default)
    {
        // 检查对话日志是否启用。有数据库时从数据库读取，无数据库时从配置快照读取。
        if (!await IsConversationLogEnabledAsync(cancellationToken))
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

    /// <summary>
    /// 判断对话日志是否启用。
    /// Web/Admin 宿主从数据库查询 SystemRuntimeSettings.ConversationLogEnabled；
    /// Core 宿主从配置快照的 RuntimeSettings.ConversationLogEnabled 读取。
    /// </summary>
    private async Task<bool> IsConversationLogEnabledAsync(CancellationToken cancellationToken)
    {
        if (_databaseAvailable)
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settings = await dbContext.SystemRuntimeSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == 1, cancellationToken);
            return settings is null || settings.ConversationLogEnabled;
        }

        // Core 宿主没有数据库，从配置快照读取。
        using var coreScope = _scopeFactory.CreateScope();
        var configProvider = coreScope.ServiceProvider.GetService<ICoreRuntimeConfigProvider>();
        if (configProvider is null)
        {
            // 没有配置快照提供器时默认启用。
            return true;
        }

        var snapshot = configProvider.GetCurrent();
        return snapshot?.RuntimeSettings is null || snapshot.RuntimeSettings.ConversationLogEnabled;
    }
}
