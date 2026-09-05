using AITool.Infrastructure.Scheduling;
using FluentAssertions;

namespace AITool.ApplicationTests.Scheduling;

/// <summary>
/// 日志保留清理调度的纯逻辑测试：每日触发窗口判定（替代 Hangfire "0 3 * * *"）。
/// </summary>
public sealed class LogRetentionPruneLogicTests
{
    [Fact]
    public void ShouldPrune_triggers_when_past_hour_and_not_run_today()
    {
        // 03:00 整点即触发（与 Cron "0 3 * * *" 语义一致）。
        var now = new DateTime(2026, 9, 4, 3, 0, 0);
        LogRetentionPruneService.ShouldPrune(now, lastPrunedLocalDate: null).Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ShouldPrune_does_not_trigger_before_prune_hour(int hour)
    {
        var now = new DateTime(2026, 9, 4, hour, 59, 59);
        LogRetentionPruneService.ShouldPrune(now, lastPrunedLocalDate: null).Should().BeFalse();
    }

    [Fact]
    public void ShouldPrune_skips_when_already_pruned_today()
    {
        // 当天 03:00 已执行，同日晚间的检查不再触发。
        var now = new DateTime(2026, 9, 4, 15, 0, 0);
        LogRetentionPruneService.ShouldPrune(now, lastPrunedLocalDate: now.Date).Should().BeFalse();
    }

    [Fact]
    public void ShouldPrune_runs_again_next_day()
    {
        // 昨天 03:00 执行过，今天 03:00 应再次触发（每日一次语义）。
        var now = new DateTime(2026, 9, 4, 3, 0, 0);
        LogRetentionPruneService.ShouldPrune(now, lastPrunedLocalDate: now.Date.AddDays(-1)).Should().BeTrue();
    }

    [Fact]
    public void ShouldPrune_catches_up_when_process_was_down_at_prune_hour()
    {
        // 凌晨 3 点进程不在运行（如夜间停机），当天晚些时候启动后的首次检查应补做，
        // 而不是无限顺延到次日（这是有意优于 Hangfire RecurringJob 的行为）。
        var now = new DateTime(2026, 9, 4, 21, 30, 0);
        LogRetentionPruneService.ShouldPrune(now, lastPrunedLocalDate: new DateTime(2026, 9, 3)).Should().BeTrue();
    }
}
