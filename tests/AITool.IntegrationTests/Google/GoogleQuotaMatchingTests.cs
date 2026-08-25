using AITool.Web.Controllers.Admin;
using FluentAssertions;
using Xunit;

namespace AITool.IntegrationTests.Google;

public sealed class GoogleQuotaMatchingTests
{
    [Theory]
    [InlineData("gemini-3.7-flash-tiered", "gemini-3.7-flash-high", true)]
    [InlineData("gemini-3.7-flash-tiered", "gemini-3.7-flash", true)]
    [InlineData("gemini-3.6-flash-tiered", "gemini-3.6-flash-medium", true)]
    [InlineData("claude-sonnet-4-6", "claude-sonnet-4-6-thinking", true)]
    [InlineData("claude-opus-4-6-thinking", "claude-opus-4-6", true)]
    [InlineData("gemini-3.1-pro-high", "gemini-3.1-pro-low", true)]
    [InlineData("gpt-oss-120b-medium", "gpt-oss-120b", true)]
    [InlineData("gemini-2.5-flash-thinking", "gemini-2.5-flash", true)]
    [InlineData("gemini-3.7-flash-tiered", "claude-sonnet-4-6", false)]
    [InlineData("gemini-2.5-pro", "gemini-3.5-flash", false)]
    public void IsModelMatchingQuotaWindow_MatchesExpectedPairs(string windowId, string modelName, bool expected)
    {
        var matched = GoogleAccountsApiController.IsModelMatchingQuotaWindow(windowId, modelName);
        matched.Should().Be(expected);
    }
}
