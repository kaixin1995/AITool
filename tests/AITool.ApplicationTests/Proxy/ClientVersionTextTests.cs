using AITool.Infrastructure.Proxy;
using FluentAssertions;

namespace AITool.ApplicationTests.Proxy;

/// <summary>
/// ClientVersionText 版本提取/比较/替换的契约：
/// 供「请求头模板 AI 查最新版」（对比 + 替换 UA 版本段）与 Codex 客户端版本解析共用。
/// </summary>
public sealed class ClientVersionTextTests
{
    [Theory]
    [InlineData("Codex Desktop/0.153.3 (Windows 10.0.19045; x86_64) unknown (Codex Desktop; 26.818.61809)", "0.153.3")]
    [InlineData("claude-cli/2.1.161 (external, cli)", "2.1.161")]
    [InlineData("opencode/1.15.0", "1.15.0")]
    [InlineData("Codex Desktop/0.149.0-alpha.4.3 (Windows 10.0.19045; x86_64)", "0.149.0-alpha.4.3")]
    [InlineData("Antigravity 1.10.4 something", "1.10.4")]
    [InlineData("Mozilla/5.0", "5.0")]
    public void ExtractVersion_takes_first_name_version_segment(string userAgent, string expected)
    {
        ClientVersionText.ExtractVersion(userAgent).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no version here")]
    public void ExtractVersion_returns_null_when_unrecognized(string? text)
    {
        ClientVersionText.ExtractVersion(text).Should().BeNull();
    }

    [Fact]
    public void ReplaceVersion_replaces_only_first_version_and_keeps_rest()
    {
        const string ua = "Codex Desktop/0.153.3 (Windows 10.0.19045; x86_64) unknown (Codex Desktop; 26.818.61809)";
        var replaced = ClientVersionText.ReplaceVersion(ua, "0.160.0");
        replaced.Should().Be("Codex Desktop/0.160.0 (Windows 10.0.19045; x86_64) unknown (Codex Desktop; 26.818.61809)");
    }

    [Fact]
    public void ReplaceVersion_handles_prerelease_and_leading_whitespace()
    {
        ClientVersionText.ReplaceVersion("  claude-cli/2.1.161 (external)", "2.2.0-rc.1")
            .Should().Be("claude-cli/2.2.0-rc.1 (external)");
    }

    [Fact]
    public void ReplaceVersion_returns_original_when_no_version()
    {
        const string text = "no version here";
        ClientVersionText.ReplaceVersion(text, "1.0.0").Should().Be(text);
    }

    [Theory]
    [InlineData("0.153.3", "0.153.0", 1)]
    [InlineData("0.153.0", "0.153.3", -1)]
    [InlineData("0.153.0", "0.153.0", 0)]
    [InlineData("0.153", "0.153.0", 0)]           // 缺失段按 0 处理
    [InlineData("0.153.0-alpha.1", "0.153.0", -1)] // prerelease 低于正式版
    [InlineData("0.153.0", "0.153.0-alpha.1", 1)]
    [InlineData("v1.2.3", "1.2.3", 0)]             // v 前缀忽略
    [InlineData("1.10.0", "1.9.0", 1)]             // 数值比较，非字符串
    [InlineData(null, "1.0.0", -1)]
    public void CompareVersions_orders_correctly(string? a, string? b, int expectedSign)
    {
        var result = ClientVersionText.CompareVersions(a, b);
        Math.Sign(result).Should().Be(expectedSign);
    }
}
