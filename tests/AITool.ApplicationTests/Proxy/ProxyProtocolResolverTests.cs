using AITool.Application.Proxy;
using FluentAssertions;

namespace AITool.ApplicationTests.Proxy;

/// <summary>
/// 验证站点协议能力推导和客户端协议选择逻辑集中在同一处。
/// </summary>
public sealed class ProxyProtocolResolverTests
{
    [Fact]
    public void ResolveSiteProtocolType_supports_legacy_responses_value()
    {
        ProxyProtocolResolver.ResolveSiteProtocolType(
                supportsOpenAi: true,
                supportsAnthropic: false,
                supportsResponses: false,
                legacyProtocolType: "Responses")
            .Should()
            .Be(ProxyProtocolResolver.Responses);
    }

    [Fact]
    public void ResolveProtocolForClient_keeps_native_responses_when_site_supports_it()
    {
        ProxyProtocolResolver.ResolveProtocolForClient(
                ProxyProtocolResolver.Responses,
                ProxyProtocolResolver.Responses,
                supportsOpenAi: true,
                supportsAnthropic: false,
                supportsResponses: true,
                legacyProtocolType: ProxyProtocolResolver.Responses)
            .Should()
            .Be(ProxyProtocolResolver.Responses);
    }

    [Fact]
    public void ResolveProtocolForClient_bridges_responses_to_openai_only_site()
    {
        ProxyProtocolResolver.ResolveProtocolForClient(
                ProxyProtocolResolver.Responses,
                ProxyProtocolResolver.OpenAi,
                supportsOpenAi: true,
                supportsAnthropic: false,
                supportsResponses: false)
            .Should()
            .Be(ProxyProtocolResolver.OpenAi);
    }

    [Fact]
    public void ResolveProtocolForClient_uses_responses_for_responses_only_site()
    {
        ProxyProtocolResolver.ResolveProtocolForClient(
                ProxyProtocolResolver.OpenAi,
                ProxyProtocolResolver.Responses,
                supportsOpenAi: false,
                supportsAnthropic: false,
                supportsResponses: false,
                legacyProtocolType: ProxyProtocolResolver.Responses)
            .Should()
            .Be(ProxyProtocolResolver.Responses);

        ProxyProtocolResolver.ResolveProtocolForClient(
                ProxyProtocolResolver.Anthropic,
                ProxyProtocolResolver.Responses,
                supportsOpenAi: false,
                supportsAnthropic: false,
                supportsResponses: false,
                legacyProtocolType: ProxyProtocolResolver.Responses)
            .Should()
            .Be(ProxyProtocolResolver.Responses);
    }

    [Fact]
    public void SupportsResponses_recognizes_explicit_and_legacy_capabilities()
    {
        ProxyProtocolResolver.SupportsResponses(
                supportsOpenAi: true,
                supportsAnthropic: false,
                supportsResponses: true)
            .Should()
            .BeTrue();

        ProxyProtocolResolver.SupportsResponses(
                supportsOpenAi: true,
                supportsAnthropic: false,
                supportsResponses: false,
                legacyProtocolType: ProxyProtocolResolver.Responses)
            .Should()
            .BeTrue();

        ProxyProtocolResolver.SupportsResponses(
                supportsOpenAi: true,
                supportsAnthropic: false,
                supportsResponses: false,
                legacyProtocolType: ProxyProtocolResolver.OpenAi)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void ResolveProtocolForClient_preserves_explicit_openai_capability_on_legacy_responses_site()
    {
        ProxyProtocolResolver.ResolveProtocolForClient(
                ProxyProtocolResolver.OpenAi,
                ProxyProtocolResolver.Responses,
                supportsOpenAi: true,
                supportsAnthropic: false,
                supportsResponses: false,
                legacyProtocolType: ProxyProtocolResolver.Responses)
            .Should()
            .Be(ProxyProtocolResolver.OpenAi);

        ProxyProtocolResolver.ResolveProtocolForClient(
                ProxyProtocolResolver.Responses,
                ProxyProtocolResolver.Responses,
                supportsOpenAi: true,
                supportsAnthropic: false,
                supportsResponses: false,
                legacyProtocolType: ProxyProtocolResolver.Responses)
            .Should()
            .Be(ProxyProtocolResolver.Responses);
    }
}
