using AITool.Domain.Codex;
using AITool.Web.Services;
using FluentAssertions;

namespace AITool.IntegrationTests.Services;

public sealed class CodexQuotaPeriodHistoryTests
{
    private static readonly DateTimeOffset Reset = DateTimeOffset.Parse("2026-11-13T17:44:11+08:00");
    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly Guid SiteId = Guid.NewGuid();

    private static List<CodexQuotaPeriod> History()
    {
        var first = Reset.AddMinutes(45);
        var second = first.AddHours(1);
        var active = second.AddHours(1);
        CodexQuotaPeriod Sliding(DateTimeOffset start, DateTimeOffset? end, double used) => new()
        {
            AccountId = AccountId, SiteId = SiteId, WindowId = "weekly", DurationSeconds = 604800,
            StartAt = start, ResetAt = start.AddDays(7), EndAt = end,
            FirstObservedAt = start, LastObservedAt = start, UsedPercent = used, BoundaryUncertain = true
        };
        return
        [
            new CodexQuotaPeriod
            {
                AccountId = AccountId, SiteId = SiteId, WindowId = "weekly", DurationSeconds = 604800,
                StartAt = Reset.AddDays(-7), ResetAt = Reset, EndAt = Reset,
                FirstObservedAt = Reset.AddDays(-6), LastObservedAt = Reset.AddMinutes(-5), UsedPercent = 99,
                RequestCount = 1232, InputTokens = 297223472, EstimatedCostUsd = 123m
            },
            Sliding(first, second, 0), Sliding(second, active, 0), Sliding(active, null, 11)
        ];
    }

    [Fact]
    public void Links_current_to_real_previous_without_modifying_or_discarding_saved_records()
    {
        var history = History();
        // A call can start before the first nonzero quota observation; retain this fragment's usage as well.
        history[2].RequestCount = 1;
        history[2].InputTokens = 569091;
        var originalStarts = history.Select(p => p.StartAt).ToArray();
        var links = CodexQuotaPeriodHistory.PreviousPeriodLinks(history.AsEnumerable().Reverse().ToList());

        links.Should().ContainSingle().Which.Should().Be(new KeyValuePair<Guid, Guid>(history[3].Id, history[0].Id));
        history.Select(p => p.StartAt).Should().Equal(originalStarts);
        history[0].RequestCount.Should().Be(1232);
        history[0].EstimatedCostUsd.Should().Be(123m);
        history[2].InputTokens.Should().Be(569091);
        history.Should().HaveCount(4);
    }

    [Theory]
    [InlineData("nonzero")]
    [InlineData("not-sliding")]
    [InlineData("not-uncertain")]
    [InlineData("different-site")]
    [InlineData("different-account")]
    [InlineData("different-window")]
    [InlineData("different-duration")]
    [InlineData("gap")]
    [InlineData("anchor-not-natural")]
    [InlineData("missing-anchor")]
    [InlineData("single-fragment")]
    [InlineData("cross-cycle")]
    public void Does_not_cross_real_resets_or_incomplete_or_unrelated_history(string condition)
    {
        var history = History();
        switch (condition)
        {
            case "nonzero": history[1].UsedPercent = 1; break;
            case "not-sliding": history[1].ResetAt = history[1].ResetAt.AddMinutes(10); break;
            case "not-uncertain": history[1].BoundaryUncertain = false; break;
            case "different-site": history[1].SiteId = Guid.NewGuid(); break;
            case "different-account": history[1].AccountId = Guid.NewGuid(); break;
            case "different-window": history[1].WindowId = "five-hour"; break;
            case "different-duration": history[1].DurationSeconds = 18000; break;
            case "gap": history[1].EndAt = history[1].EndAt!.Value.AddMinutes(-5); break;
            case "anchor-not-natural": history[0].EndAt = Reset.AddMinutes(-10); break;
            case "missing-anchor": history.RemoveAt(0); break;
            case "single-fragment": history.RemoveAt(1); break;
            case "cross-cycle":
                foreach (var p in history.Skip(1))
                {
                    p.StartAt = p.StartAt.AddDays(7); p.ResetAt = p.ResetAt.AddDays(7);
                    p.FirstObservedAt = p.FirstObservedAt.AddDays(7); p.LastObservedAt = p.LastObservedAt.AddDays(7);
                    if (p.EndAt is { } end) p.EndAt = end.AddDays(7);
                }
                break;
        }
        CodexQuotaPeriodHistory.PreviousPeriodLinks(history).Should().BeEmpty();
    }

    [Fact]
    public void Keeps_previous_link_after_repaired_observer_moves_idle_reset_forward()
    {
        var history = History();
        var current = history[^1];
        current.LastObservedAt = current.FirstObservedAt.AddHours(12);
        current.ResetAt = current.LastObservedAt.AddDays(7);

        CodexQuotaPeriodHistory.PreviousPeriodLinks(history).Should()
            .ContainSingle().Which.Should().Be(new KeyValuePair<Guid, Guid>(current.Id, history[0].Id));
    }

    [Fact]
    public void Links_natural_previous_after_first_observation_gap_without_legacy_fragments()
    {
        var history = History();
        history.RemoveRange(1, 2);
        var current = history[^1];
        current.StartAt = current.FirstObservedAt = current.LastObservedAt = Reset.AddMinutes(45);
        current.ResetAt = current.StartAt.AddDays(7);
        var start = current.StartAt;

        CodexQuotaPeriodHistory.PreviousPeriodLinks(history).Should()
            .ContainSingle().Which.Should().Be(new KeyValuePair<Guid, Guid>(current.Id, history[0].Id));
        current.StartAt.Should().Be(start);
    }

    [Theory]
    [InlineData("too-long-gap")]
    [InlineData("early-reset")]
    [InlineData("reset-before-observations")]
    [InlineData("reset-after-observations")]
    public void Does_not_link_new_observation_with_unproven_natural_boundary(string condition)
    {
        var history = History();
        history.RemoveRange(1, 2);
        var current = history[^1];
        current.StartAt = current.FirstObservedAt = current.LastObservedAt = Reset.AddMinutes(45);
        current.ResetAt = current.StartAt.AddDays(7);
        switch (condition)
        {
            case "too-long-gap":
                current.StartAt = current.FirstObservedAt = current.LastObservedAt = Reset.AddHours(3);
                current.ResetAt = current.StartAt.AddDays(7);
                break;
            case "early-reset": history[0].EndAt = Reset.AddMinutes(-10); break;
            case "reset-before-observations": current.ResetAt = current.ResetAt.AddMinutes(-10); break;
            case "reset-after-observations": current.ResetAt = current.ResetAt.AddMinutes(10); break;
        }
        CodexQuotaPeriodHistory.PreviousPeriodLinks(history).Should().BeEmpty();
    }
}
