using AITool.Application.Sites;
using FluentAssertions;

namespace AITool.ApplicationTests.Sites;

/// <summary>
/// 验证站点接口路径模式对上游接口路径拼接的影响。
/// </summary>
public sealed class SiteEndpointPathResolverTests
{
    [Fact]
    public void ResolvePath_StandardRoot_AddsV1Prefix()
    {
        SiteEndpointPathResolver.ResolvePath(SiteEndpointPathResolver.StandardRoot, "chat/completions")
            .Should().Be("/v1/chat/completions");

        SiteEndpointPathResolver.ResolvePath(null, "messages")
            .Should().Be("/v1/messages");
    }

    [Fact]
    public void ResolvePath_VersionedBase_DoesNotAddV1Prefix()
    {
        SiteEndpointPathResolver.ResolvePath(SiteEndpointPathResolver.VersionedBase, "chat/completions")
            .Should().Be("/chat/completions");

        SiteEndpointPathResolver.ResolvePath(SiteEndpointPathResolver.VersionedBase, "messages")
            .Should().Be("/messages");
    }

    [Fact]
    public void BuildUrl_VersionedBase_UsesExistingVersionPath()
    {
        SiteEndpointPathResolver.BuildUrl("https://api.z.ai/api/coding/paas/v4", SiteEndpointPathResolver.VersionedBase, "responses")
            .Should().Be("https://api.z.ai/api/coding/paas/v4/responses");
    }
}
