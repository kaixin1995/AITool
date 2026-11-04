using AITool.Application.Accounts;

namespace AITool.Web.Services;

/// <summary>额度自动禁用账号的持久化恢复复查日程；不保存账号凭证。</summary>
public interface IQuotaRecheckScheduleStore
{
    /// <summary>Clock used to evaluate lease expiry by the transactional state writer.</summary>
    DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <summary>
    /// 仅额度自动禁用事件调用。到期即查：距上次检查已超过随机间隔（含从未检查）的账号
    /// 立即以 0–60 秒错峰复查（额度窗口可能早已重置）；未到期则按「上次检查 + 40–60 分钟随机」排程，
    /// 最迟约一小时内必有下一次复查（防上游提前重置长期无人发现）。已存在的日程不被重复禁用推迟。
    /// </summary>
    Task EnsureScheduledAsync(string providerKey, Guid accountId, DateTimeOffset? lastQuotaCheckedAt, CancellationToken cancellationToken);

    /// <summary>在提供程序枚举之前捕获日程版本；枚举失败时丢弃观察结果，不能用于清理。</summary>
    Task<QuotaRecheckObservation> BeginReconciliationAsync(string providerKey, CancellationToken cancellationToken)
        => Task.FromResult(new QuotaRecheckObservation(providerKey, new Dictionary<Guid, string>()));

    /// <summary>兼容无观察版本的调用；持久化实现只补齐日程，不授权删除已有行。</summary>
    Task<IReadOnlyList<QuotaRecheckScheduleInfo>> ReconcileAsync(string providerKey, IReadOnlyList<AccountQuotaTarget> eligibleAccounts, CancellationToken cancellationToken);

    /// <summary>仅在枚举成功后调用。只清理枚举前已观察且版本未变、无有效认领的非资格日程。</summary>
    Task<IReadOnlyList<QuotaRecheckScheduleInfo>> ReconcileAsync(string providerKey, IReadOnlyList<AccountQuotaTarget> eligibleAccounts,
        QuotaRecheckObservation observation, CancellationToken cancellationToken)
        => ReconcileAsync(providerKey, eligibleAccounts, cancellationToken);

    /// <summary>原子领取并预置故障恢复期限；force 仅跳过到期判断，不能抢占有效认领。</summary>
    Task<QuotaRecheckLease?> TryAcquireAsync(string providerKey, Guid accountId, CancellationToken cancellationToken, bool force = false);

    /// <summary>仅持有当前认领令牌的执行者能续排或移除日程；失败也从完成时重新随机计时。</summary>
    Task CompleteAsync(QuotaRecheckLease lease, bool keepScheduled, CancellationToken cancellationToken);
}

public sealed record QuotaRecheckObservation(string ProviderKey, IReadOnlyDictionary<Guid, string> Generations);

public sealed record QuotaRecheckScheduleInfo(string ProviderKey, Guid AccountId, DateTimeOffset NextCheckAt);

public sealed record QuotaRecheckLease(string ProviderKey, Guid AccountId, Guid Token);

public interface IQuotaRecheckIntervalSource
{
    TimeSpan NextInterval();
    TimeSpan NextCatchUpDelay();
}
