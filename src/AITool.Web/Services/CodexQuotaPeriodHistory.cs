using AITool.Domain.Codex;

namespace AITool.Web.Services;

/// <summary>识别旧版把空闲滑动重置时间切成短周期的记录，仅为展示建立上期关联。</summary>
public static class CodexQuotaPeriodHistory
{
    private const double ToleranceSeconds = 120;
    private static readonly TimeSpan MaxObservationGap = TimeSpan.FromHours(2);

    /// <summary>
    /// 不删除记录、不修改边界或已保存的汇总。只有完整的空闲滑动链及自然结束的历史锚点
    /// 都在读取结果中时，才允许跨过这些短片段关联到真正的上个周期；修复后的新记录
    /// 没有短片段时，也允许在自然结束锚点后两小时内首次观测到新周期。
    /// </summary>
    public static IReadOnlyDictionary<Guid, Guid> PreviousPeriodLinks(IReadOnlyCollection<CodexQuotaPeriod> periods)
    {
        var links = new Dictionary<Guid, Guid>();
        foreach (var group in periods.GroupBy(p => (p.AccountId, p.SiteId, p.WindowId, p.DurationSeconds)))
        {
            var ordered = group.OrderBy(p => p.StartAt).ToList();
            for (var index = 0; index < ordered.Count; index++)
            {
                var current = ordered[index];
                if (current.EndAt != null || !HasSlidingCurrent(current)) continue;
                var first = current;
                var fragments = 0;
                var previousIndex = index - 1;
                while (previousIndex >= 0)
                {
                    var candidate = ordered[previousIndex];
                    if (!IsIdleFragment(candidate) || !Close(candidate.EndAt!.Value, first.StartAt)) break;
                    fragments++;
                    first = candidate;
                    previousIndex--;
                }
                // A single early reset or a missing history segment is not enough evidence to repair an association.
                if (fragments == 1 || previousIndex < 0) continue;
                var previous = ordered[previousIndex];
                if (previous.EndAt is not { } end || !Close(end, previous.ResetAt) || previous.StartAt >= end) continue;
                var gap = first.StartAt - end;
                var span = current.FirstObservedAt - end;
                if (gap < TimeSpan.Zero || gap > MaxObservationGap || span <= TimeSpan.Zero || span.TotalSeconds >= current.DurationSeconds) continue;
                links[current.Id] = previous.Id;
            }
        }
        return links;
    }

    private static bool IsIdleFragment(CodexQuotaPeriod period)
        => period.UsedPercent == 0 && HasSlidingStart(period) && period.EndAt is { } end
           && end > period.StartAt && end < period.ResetAt
           && end - period.StartAt <= MaxObservationGap
           && period.LastObservedAt >= period.FirstObservedAt
           && period.LastObservedAt < end;

    private static bool HasSlidingStart(CodexQuotaPeriod period)
        => period.BoundaryUncertain && period.DurationSeconds is > 0 and <= 31536000
           && Close(period.StartAt, period.FirstObservedAt)
           && Close(period.ResetAt.AddSeconds(-period.DurationSeconds), period.FirstObservedAt);

    private static bool HasSlidingCurrent(CodexQuotaPeriod period)
    {
        if (!period.BoundaryUncertain || period.DurationSeconds is not (> 0 and <= 31536000)
            || !Close(period.StartAt, period.FirstObservedAt)
            || period.LastObservedAt < period.FirstObservedAt) return false;
        // The repaired observer keeps the first observation while an idle reset continues to move forward.
        var inferredStart = period.ResetAt.AddSeconds(-period.DurationSeconds);
        return inferredStart >= period.FirstObservedAt.AddSeconds(-ToleranceSeconds)
               && inferredStart <= period.LastObservedAt.AddSeconds(ToleranceSeconds);
    }

    private static bool Close(DateTimeOffset left, DateTimeOffset right)
        => Math.Abs((left - right).TotalSeconds) <= ToleranceSeconds;
}
