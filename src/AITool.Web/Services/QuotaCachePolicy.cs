using AITool.Application.Codex;

namespace AITool.Web.Services;

/// <summary>
/// 额度缓存复用策略：判断巡检时是否可沿用上次额度快照，避免对未被使用的账号重复打上游。
/// 移植自 codex-patrol QuotaCachePolicy.TryReuseQuota，并新增「每隔 N 小时强制真实刷新」的 TTL 兜底
/// （codex-patrol 缺失此检查，会导致未被使用的账号无限期使用缓存）。
/// </summary>
public static class QuotaCachePolicy
{
    /// <summary>周窗口的秒数常量（604800）。</summary>
    private const int WeekSeconds = 604_800;

    /// <summary>
    /// 尝试复用已有的额度快照。满足全部条件返回 true。
    /// 条件：①有历史快照且成功 ②有窗口数据 ③至少一个窗口有重置时间
    ///       ④无窗口已到重置时间 ⑤距上次刷新未超过 maxCacheHours（TTL 兜底）
    ///       ⑥账号自上次刷新后未被使用（hasRecentUsage=false）。
    /// </summary>
    /// <param name="existing">已有的额度快照（可为 null）。</param>
    /// <param name="lastRefreshedAt">上次真实刷新时间（来自 CodexAccount.LastQuotaCheckedAt）。</param>
    /// <param name="hasRecentUsage">账号自上次刷新后是否有新调用（查 ProxyUsageLogs 判定）。</param>
    /// <param name="maxCacheHours">额度缓存最大小时数，超过强制真实刷新。</param>
    /// <param name="nowUtc">当前 UTC 时间。</param>
    /// <param name="reason">未命中缓存时的原因说明。</param>
    public static bool TryReuseQuota(
        CodexQuotaInfo? existing,
        DateTimeOffset? lastRefreshedAt,
        bool hasRecentUsage,
        int maxCacheHours,
        DateTimeOffset nowUtc,
        out string reason)
    {
        reason = string.Empty;

        if (existing is null || !existing.Success)
        {
            reason = "无可用历史额度缓存";
            return false;
        }

        if (existing.Windows.Count == 0)
        {
            reason = "历史额度窗口为空";
            return false;
        }

        // 缺少重置时间无法判断有效期
        if (!existing.Windows.Any(w => w.ResetAtUtc.HasValue))
        {
            reason = "历史额度缺少重置时间";
            return false;
        }

        // 任一窗口已到期 → 需重新请求
        var expiredWindow = existing.Windows.FirstOrDefault(w => w.ResetAtUtc.HasValue && w.ResetAtUtc <= nowUtc);
        if (expiredWindow is not null)
        {
            reason = $"额度窗口 {expiredWindow.Label} 已到重置时间";
            return false;
        }

        // TTL 兜底：距上次刷新超过 maxCacheHours → 强制真实刷新（codex-patrol 缺失的检查）
        if (lastRefreshedAt.HasValue)
        {
            var age = nowUtc - lastRefreshedAt.Value;
            if (age.TotalHours >= maxCacheHours)
            {
                reason = $"缓存已超过 {maxCacheHours} 小时最大刷新间隔";
                return false;
            }
        }
        else
        {
            // 无刷新时间记录，保守真实刷新一次
            reason = "无上次刷新时间记录";
            return false;
        }

        // 上次刷新后有新调用 → 额度可能变化，真实刷新
        if (hasRecentUsage)
        {
            reason = "上次额度刷新后存在新的调用记录";
            return false;
        }

        // 走到这里 hasRecentUsage 必为 false（上面已拦截），返回纯原因描述，由调用方统一加「命中缓存：」前缀。
        reason = "上次刷新后无新调用，且未到额度重置时间";
        return true;
    }
}
