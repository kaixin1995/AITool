using AITool.Application.Kimi;
using FluentAssertions;
using Xunit;

namespace AITool.ApplicationTests.Kimi;

/// <summary>
/// Kimi 账号常量、默认模型与端点解析测试。
/// </summary>
public sealed class KimiOAuthBasicsTests
{
    [Fact]
    public void Constants_contain_correct_endpoints_and_client_id()
    {
        KimiConstants.OAuthHost.Should().Be("https://auth.kimi.com");
        KimiConstants.DeviceAuthorizationEndpoint.Should().Be("https://auth.kimi.com/api/oauth/device_authorization");
        KimiConstants.TokenEndpoint.Should().Be("https://auth.kimi.com/api/oauth/token");
        KimiConstants.ClientId.Should().Be("17e5f671-d194-4dfb-9706-5516cb48c098");
        KimiConstants.ManagedSource.Should().Be("kimi_oauth");
        KimiConstants.ApiBaseUrl.Should().Be("https://api.kimi.com/coding");
    }

    [Fact]
    public void DefaultModels_contain_standard_kimi_models()
    {
        KimiConstants.DefaultModels.Should().NotBeEmpty();
        // DefaultModels 存放对外公开名（与 CLIProxyAPI 注册表一致），上游 ID 由 KimiModelNormalizer 换算。
        KimiConstants.DefaultModels.Should().Contain(m => m.Slug == "kimi-k2.5");
        KimiConstants.DefaultModels.Should().Contain(m => m.Slug == "kimi-k3");
        KimiConstants.DefaultModels.Should().Contain(m => m.Slug == "kimi-k2.7-code");
        KimiModelNormalizer.NormalizeUpstreamModel("kimi-k2.5").Should().Be("k2.5");
        KimiModelNormalizer.NormalizeUpstreamModel("kimi-k2.7-code").Should().Be("kimi-for-coding");
        // 每个公开名换算出的上游 ID 都能反查回公开名（往返一致）。
        foreach (var (slug, _) in KimiConstants.DefaultModels)
        {
            var upstream = KimiModelNormalizer.NormalizeUpstreamModel(slug);
            KimiModelNormalizer.PublicModelNameFromUpstream(upstream).Should().Be(slug);
        }
    }
}
