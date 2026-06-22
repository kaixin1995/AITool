using AITool.Application.Conversations;
using AITool.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AITool.Infrastructure.Conversations;

/// <summary>
/// 结构化对话记录服务，采用后台批量刷盘方式写入数据库。
/// <para>
/// 开关读取优化：用带 TTL 的内存缓存（5 秒）缓存 <c>ConversationLogEnabled</c>，
/// 避免每个代理请求都建 DI 作用域 + DbContext 查数据库。开关变更最多 5 秒后生效。
/// </para>
/// </summary>
public sealed class ConversationLogService : IConversationLogService
{
    /// <summary>
    /// 开关缓存的 TTL。在此期间内的代理请求直接读内存，不查数据库。
    /// </summary>
    private static readonly TimeSpan EnabledCacheTtl = TimeSpan.FromSeconds(5);

    private readonly ConversationLogBatchWriter _batchWriter;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ConversationLogService> _logger;

    /// <summary>
    /// 缓存的开关值。
    /// </summary>
    private bool _cachedEnabled = true;
    /// <summary>
    /// 缓存过期时间（UTC）。
    /// </summary>
    private DateTimeOffset _cacheExpiresAt = DateTimeOffset.MinValue;
    /// <summary>
    /// 保护缓存读写的锁。
    /// </summary>
    private readonly object _cacheLock = new();

    public ConversationLogService(
        ConversationLogBatchWriter batchWriter,
        IServiceScopeFactory scopeFactory,
        ILogger<ConversationLogService> logger)
    {
        _batchWriter = batchWriter;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task LogAsync(ConversationTurnEntry entry, CancellationToken cancellationToken = default)
    {
        if (!IsConversationLogEnabled())
        {
            return;
        }

        var accepted = await _batchWriter.EnqueueAsync(entry, cancellationToken);
        if (!accepted)
        {
            _logger.LogWarning("对话记录入队失败，请求已继续。SourceTool={SourceTool}, SessionId={SessionId}", entry.SourceTool, entry.SessionId);
        }
    }

    /// <summary>
    /// 读取对话记录开关（带 5 秒 TTL 内存缓存）。
    /// 缓存命中时直接返回内存值，不查数据库；过期后重建（最多每 5 秒查一次）。
    /// </summary>
    private bool IsConversationLogEnabled()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_cacheLock)
        {
            if (now < _cacheExpiresAt)
            {
                return _cachedEnabled;
            }
        }

        // 缓存过期，查数据库刷新。即使这里查不到设置也默认开启（与原逻辑一致）。
        bool enabled = true;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settings = dbContext.SystemRuntimeSettings
                .AsNoTracking()
                .FirstOrDefault(x => x.Id == 1);
            if (settings is not null)
            {
                enabled = settings.ConversationLogEnabled;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取对话记录开关失败，本次默认开启");
        }

        lock (_cacheLock)
        {
            _cachedEnabled = enabled;
            _cacheExpiresAt = DateTimeOffset.UtcNow + EnabledCacheTtl;
        }

        return enabled;
    }
}
