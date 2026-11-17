using AITool.Application.Codex;
using AITool.Application.Pricing;
using AITool.Domain.Codex;
using AITool.Infrastructure.Persistence;
using SqlSugar;

namespace AITool.Admin.Services;

/// <summary>保存真实额度观测，并按账号实际命中的站点汇总日志。</summary>
public static class CodexQuotaPeriodService
{
    public static bool IsGeneral(string id) => id is "five-hour" or "weekly" or "monthly" or "window-1" or "window-2";

    public static async Task ObserveAsync(ISqlSugarClient db, CodexAccount account, CodexQuotaInfo info, DateTimeOffset observedAt, CancellationToken ct, IModelPricingService? pricing = null)
    {
        if (pricing is not null) await pricing.GetCatalogAsync(ct);
        foreach (var window in info.Windows.Where(w => IsGeneral(w.Id) && w.ResetAtUtc > observedAt && w.LimitWindowSeconds is > 0 and <= 31536000))
        {
            var reset = window.ResetAtUtc!.Value;
            var duration = window.LimitWindowSeconds!.Value;
            if ((reset - observedAt).TotalSeconds > duration + 120) continue;
            var periods = await db.Queryable<CodexQuotaPeriod>()
                .Where(x => x.AccountId == account.Id && x.WindowId == window.Id && x.EndAt == null)
                .OrderBy(x => x.LastObservedAt, OrderByType.Desc).ToListAsync(ct);
            var current = periods.FirstOrDefault();
            var uncertainStart = false;
            // 未开始消耗的窗口可能返回“查询时间 + 完整窗口”，首次使用时才固定截止时间。
            // 这种空闲倒计时前移不是额度重置；保留已知起点和日志区间，直至窗口真正到期。
            var idleResetDrift = current is not null && current.ResetAt > observedAt
                && Math.Abs(current.DurationSeconds - duration) <= 1
                && current.UsedPercent == 0 && window.UsedPercent is >= 0
                && reset > current.ResetAt
                && Math.Abs((reset.AddSeconds(-duration) - observedAt).TotalSeconds) <= 120;
            if (idleResetDrift)
            {
                current!.ResetAt = reset;
                current.BoundaryUncertain = true;
            }
            // 相对倒计时可能有数秒抖动；提前重置或周期长度变化则保留不确定边界。
            var percentReset = current?.UsedPercent is double previous && window.UsedPercent is double next && previous - next >= 5;
            if (current is not null && (Math.Abs((current.ResetAt - reset).TotalSeconds) > 120 || Math.Abs(current.DurationSeconds - duration) > 1 || percentReset))
            {
                current.EndAt = current.ResetAt <= observedAt ? current.ResetAt : observedAt;
                uncertainStart = current.ResetAt > observedAt || Math.Abs((reset.AddSeconds(-duration) - current.EndAt.Value).TotalSeconds) > 120;
                current.BoundaryUncertain |= uncertainStart;
                await AggregateAsync(db, current, observedAt, ct, pricing);
                await db.Updateable(current).ExecuteCommandAsync(ct);
                current = null;
            }
            if (current is null)
            {
                current = new CodexQuotaPeriod
                {
                    AccountId = account.Id, SiteId = account.LinkedSiteId, WindowId = window.Id,
                    DurationSeconds = duration, StartAt = uncertainStart ? observedAt : reset.AddSeconds(-duration), ResetAt = reset,
                    FirstObservedAt = observedAt,
                    BoundaryUncertain = uncertainStart
                };
                current.LastObservedAt = observedAt;
                current.Label = window.Label;
                current.UsedPercent = window.UsedPercent;
                await AggregateAsync(db, current, observedAt, ct, pricing);
                await db.Insertable(current).ExecuteCommandAsync(ct);
            }
            else
            {
                current.LastObservedAt = observedAt;
                current.Label = window.Label;
                current.UsedPercent = window.UsedPercent;
                await AggregateAsync(db, current, observedAt, ct, pricing);
                await db.Updateable(current).ExecuteCommandAsync(ct);
            }
        }
    }

    public static async Task AggregateAsync(ISqlSugarClient db, CodexQuotaPeriod period, DateTimeOffset now, CancellationToken ct, IModelPricingService? pricing = null)
    {
        var end = period.EndAt ?? (period.ResetAt < now ? period.ResetAt : now);
        var rows = await db.Queryable<AITool.Domain.Proxy.ProxyUsageLog>()
            .Where(x => x.TargetSiteId == period.SiteId && x.RequestedAt >= period.StartAt && x.RequestedAt < end)
            .Select(x => new { x.AttemptedModel, x.RequestedAt, x.InputTokens, x.CachedTokens, x.OutputTokens, x.TotalTokens, x.Status })
            .ToListAsync(ct);
        period.InputTokens = rows.Sum(x => (long)x.InputTokens);
        period.CachedTokens = rows.Sum(x => (long)x.CachedTokens);
        period.OutputTokens = rows.Sum(x => (long)x.OutputTokens);
        period.RequestCount = rows.Count;
        period.SuccessCount = rows.Count(x => x.Status == "success");
        period.LastUsageAt = rows.Count > 0 ? rows.Max(x => x.RequestedAt) : null;
        period.UnknownCount = rows.Count(x => x.TotalTokens == 0);
        var pricedRows = rows.Select(row => new
        {
            row.AttemptedModel, row.TotalTokens, row.InputTokens, row.CachedTokens, row.OutputTokens,
            Cost = pricing is not null && row.TotalTokens != 0
                ? pricing.CalculateCostUsd(row.AttemptedModel, row.RequestedAt, row.InputTokens, row.CachedTokens, row.OutputTokens).CostUsd
                : null
        }).ToList();
        if (pricing is not null)
        {
            var known = pricedRows.Where(x => x.TotalTokens != 0).ToList();
            var unpriced = known.Count(x => !x.Cost.HasValue);
            period.EstimatedCostUsd = unpriced > 0 && unpriced == known.Count ? null : known.Sum(x => x.Cost ?? 0m);
            period.UnpricedCount = unpriced;
        }
        period.ModelsJson = System.Text.Json.JsonSerializer.Serialize(pricedRows.GroupBy(x => x.AttemptedModel).Select(g => new
        {
            model = g.Key, requests = g.Count(), totalTokens = g.Sum(x => (long)x.TotalTokens),
            inputTokens = g.Sum(x => (long)x.InputTokens), cachedTokens = g.Sum(x => (long)x.CachedTokens), outputTokens = g.Sum(x => (long)x.OutputTokens),
            unknownCount = g.Count(x => x.TotalTokens == 0),
            unpricedCount = pricing is null ? (int?)null : g.Count(x => x.TotalTokens != 0 && !x.Cost.HasValue),
            estimatedCostUsd = pricing is null || !g.Any(x => x.Cost.HasValue)
                ? (decimal?)null : g.Sum(x => x.Cost ?? 0m)
        }).OrderByDescending(x => x.totalTokens));
        period.AggregatedAt = now;
    }

    /// <summary>按额度观测时刻截取同一周期的调用日志，供比例估算使用，不改变保存的周期快照。</summary>
    public static async Task<CodexQuotaPeriod?> AggregateAtObservationAsync(ISqlSugarClient db, CodexQuotaPeriod period, DateTimeOffset now, CancellationToken ct, IModelPricingService? pricing = null)
    {
        if (period.EndAt is not null || period.LastObservedAt <= period.StartAt
            || period.LastObservedAt > now || period.LastObservedAt >= period.ResetAt) return null;
        var snapshot = new CodexQuotaPeriod
        {
            SiteId = period.SiteId, StartAt = period.StartAt, ResetAt = period.ResetAt,
            EndAt = period.LastObservedAt
        };
        await AggregateAsync(db, snapshot, now, ct, pricing);
        return snapshot;
    }
}
