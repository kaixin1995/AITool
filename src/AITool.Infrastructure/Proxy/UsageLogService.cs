using AITool.Application.UsageLogs;
using AITool.Domain.Proxy;
using AITool.Infrastructure.Persistence;

namespace AITool.Infrastructure.Proxy;

// 使用日志服务实现，将代理请求使用情况写入数据库
public sealed class UsageLogService : IUsageLogService
{
    private readonly AppDbContext _dbContext;

    public UsageLogService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // 将使用日志条目持久化到数据库
    public async Task LogAsync(UsageLogEntry entry, CancellationToken cancellationToken = default)
    {
        var log = new ProxyUsageLog
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
            OutputTokens = entry.OutputTokens,
            TotalTokens = entry.InputTokens + entry.OutputTokens
        };

        _dbContext.ProxyUsageLogs.Add(log);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
