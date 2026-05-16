using AITool.Application.UsageLogs;

namespace AITool.Infrastructure.Proxy;

/// <summary>
/// 使用日志服务实现，将代理请求使用情况投递到后台批量写入队列。
/// </summary>
public sealed class UsageLogService : IUsageLogService
{
    /// <summary>
    /// 字段 _batchWriter。
    /// </summary>
    private readonly ProxyUsageLogBatchWriter _batchWriter;

    /// <summary>
    /// 初始化 UsageLogService。
    /// </summary>
    public UsageLogService(ProxyUsageLogBatchWriter batchWriter)
    {
        _batchWriter = batchWriter;
    }

    /// <summary>
    /// 主链路只负责入队，不在请求线程里同步等待 SQLite 写入完成。
    /// </summary>
    public async Task LogAsync(UsageLogEntry entry, CancellationToken cancellationToken = default)
    {
        await _batchWriter.EnqueueAsync(entry, cancellationToken);
    }
}
