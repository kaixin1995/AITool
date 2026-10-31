using AITool.Domain.Proxy;
using AITool.Web.Services;
using FluentAssertions;
using Xunit;

namespace AITool.IntegrationTests.Services;

public sealed class RouteAttemptTrackerTests
{
    [Fact]
    public void Direct_and_referenced_equivalent_leaf_are_attempted_once_per_request()
    {
        var direct = Leaf();
        var referenced = direct.Clone();
        referenced.RouteId = Guid.NewGuid();
        referenced.CircuitKey = Guid.NewGuid();
        referenced.FallbackGroupId = Guid.NewGuid();
        referenced.SourceRouteEntryId = Guid.NewGuid();
        referenced.SourceRouteEntryName = "pool-source";
        referenced.SourcePoolId = Guid.NewGuid();
        var tracker = new RouteAttemptTracker([direct, referenced]);

        tracker.CanAttempt(direct).Should().BeTrue();
        tracker.CanAttempt(referenced).Should().BeTrue("checking eligibility does not claim a leaf");
        tracker.TryStart(direct).Should().BeTrue();
        tracker.CanAttempt(referenced).Should().BeFalse();
        tracker.TryStart(referenced).Should().BeFalse();
        new RouteAttemptTracker([direct, referenced]).TryStart(referenced).Should().BeTrue("dedup is request-local");
    }

    [Theory]
    [InlineData("headers")]
    [InlineData("compatibility")]
    [InlineData("reasoning")]
    [InlineData("credential")]
    [InlineData("site-key")]
    [InlineData("upstream-model")]
    [InlineData("endpoint")]
    [InlineData("proxy")]
    [InlineData("emulation")]
    public void Different_effective_forwarding_configuration_is_not_suppressed(string difference)
    {
        var first = Leaf();
        var second = first.Clone();
        second.FallbackGroupId = Guid.NewGuid();
        switch (difference)
        {
            case "headers": second.ExtraHeaders["X-Route"] = "other"; break;
            case "compatibility": second.CompatibilityRules = [new CompatibilityRule { Op = "strip", Target = "metadata" }]; break;
            case "reasoning": second.OverrideReasoningEffort = "high"; break;
            case "credential": second.ApiKey = "different-test-secret"; break;
            case "site-key": second.SiteKeyId = Guid.NewGuid(); break;
            case "upstream-model": second.UpstreamModelName = "different-alias"; break;
            case "endpoint": second.EndpointPathMode = "VersionedRoot"; break;
            case "proxy": second.EgressProxyUrl = "http://isolated.invalid:8080"; break;
            case "emulation": second.ClientEmulation = "CodexCli"; break;
        }
        var tracker = new RouteAttemptTracker([first, second]);

        tracker.TryStart(first).Should().BeTrue();
        tracker.CanAttempt(second).Should().BeTrue();
        tracker.TryStart(second).Should().BeTrue();
    }

    [Fact]
    public void Header_name_casing_and_insertion_order_do_not_change_identity()
    {
        var first = Leaf();
        first.ExtraHeaders = new(StringComparer.OrdinalIgnoreCase) { ["X-B"] = "2", ["X-A"] = "1" };
        var second = first.Clone();
        second.ExtraHeaders = new(StringComparer.OrdinalIgnoreCase) { ["x-a"] = "1", ["x-b"] = "2" };
        var tracker = new RouteAttemptTracker([first, second]);

        tracker.TryStart(first).Should().BeTrue();
        tracker.TryStart(second).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unavailable_earlier_leaf_does_not_claim_equivalent_available_sibling(bool parentSchedule)
    {
        var unavailable = Leaf();
        var available = unavailable.Clone();
        const string allDay = "[{\"start\":\"00:00\",\"end\":\"23:59\"}]";
        if (parentSchedule)
        {
            unavailable.ParentAvailabilityMode = "Unavailable";
            unavailable.ParentTimeRangesJson = allDay;
        }
        else
        {
            unavailable.AvailabilityMode = "Unavailable";
            unavailable.TimeRangesJson = allDay;
        }
        var tracker = new RouteAttemptTracker([unavailable, available]);

        tracker.CanAttempt(unavailable).Should().BeFalse();
        tracker.TryStart(unavailable).Should().BeFalse();
        tracker.CanAttempt(available).Should().BeTrue();
        tracker.TryStart(available).Should().BeTrue();
    }

    [Fact]
    public void Skipped_candidate_does_not_claim_equivalent_sibling_before_acquiring_concurrency()
    {
        var skipped = Leaf();
        var sibling = skipped.Clone();
        var tracker = new RouteAttemptTracker([skipped, sibling]);

        tracker.CanAttempt(skipped).Should().BeTrue();
        // A blocked/unsupported/full-concurrency candidate never reaches TryStart in the controller.
        tracker.CanAttempt(sibling).Should().BeTrue();
        tracker.TryStart(sibling).Should().BeTrue();
        tracker.CanAttempt(skipped).Should().BeFalse();
    }

    [Fact]
    public void Legacy_direct_only_routes_keep_repeated_attempts()
    {
        var first = Leaf();
        first.FallbackGroupId = null;
        var duplicate = first.Clone();
        var tracker = new RouteAttemptTracker([first, duplicate]);

        tracker.TryStart(first).Should().BeTrue();
        tracker.CanAttempt(duplicate).Should().BeTrue();
        tracker.TryStart(duplicate).Should().BeTrue();
        tracker.TryStart(first).Should().BeTrue();
    }

    private static CachedProxyRouteTarget Leaf() => new()
    {
        RouteId = Guid.NewGuid(), CircuitKey = Guid.NewGuid(), SiteId = Guid.NewGuid(),
        FallbackGroupId = Guid.NewGuid(), SiteModelName = "remote-model", UpstreamModelName = "model-alias",
        BaseUrl = "https://isolated.invalid", ApiKey = "test-secret", ProtocolType = "OpenAI", SupportsOpenAi = true,
        ExtraHeaders = new(StringComparer.OrdinalIgnoreCase) { ["X-Route"] = "one" }
    };
}
