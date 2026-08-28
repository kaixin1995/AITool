using AITool.Application.Kimi;
using FluentAssertions;
using Xunit;

namespace AITool.ApplicationTests.Kimi;

/// <summary>
/// Kimi 上游模型名规范化测试（移植自 CLIProxyAPI normalizeKimiUpstreamModel 的语义）。
/// </summary>
public sealed class KimiModelNormalizationTests
{
    [Theory]
    [InlineData("kimi-k2.5", "k2.5")]
    [InlineData("kimi-k2", "k2")]
    [InlineData("kimi-k2-thinking", "k2-thinking")]
    [InlineData("kimi-k3", "k3")]
    [InlineData("kimi-k3-256k", "k3-256k")]
    [InlineData("KIMI-K2.6", "k2.6")]
    public void NormalizeUpstreamModel_strips_kimi_prefix(string input, string expected)
    {
        KimiModelNormalizer.NormalizeUpstreamModel(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("kimi-k2.7-code", "kimi-for-coding")]
    [InlineData("k2.7-code", "kimi-for-coding")]
    [InlineData("kimi-for-coding", "kimi-for-coding")]
    [InlineData("kimi-k2.7-code-highspeed", "kimi-for-coding-highspeed")]
    [InlineData("k2.7-code-highspeed", "kimi-for-coding-highspeed")]
    [InlineData("for-coding-highspeed", "kimi-for-coding-highspeed")]
    public void NormalizeUpstreamModel_remaps_k27_code_aliases(string input, string expected)
    {
        KimiModelNormalizer.NormalizeUpstreamModel(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("kimi-k3[1m]", "k3")]
    [InlineData("k2.5[1m]", "k2.5")]
    public void NormalizeUpstreamModel_strips_context_suffix(string input, string expected)
    {
        KimiModelNormalizer.NormalizeUpstreamModel(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("k2.5")]
    [InlineData("kimi-for-coding")]
    [InlineData("k3")]
    public void NormalizeUpstreamModel_is_idempotent_for_canonical_ids(string canonical)
    {
        var once = KimiModelNormalizer.NormalizeUpstreamModel(canonical);
        once.Should().Be(canonical);
        KimiModelNormalizer.NormalizeUpstreamModel(once).Should().Be(canonical);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeUpstreamModel_handles_empty_input(string? input)
    {
        KimiModelNormalizer.NormalizeUpstreamModel(input).Should().Be(string.Empty);
    }

    [Theory]
    [InlineData("k2.5", "kimi-k2.5")]
    [InlineData("k2", "kimi-k2")]
    [InlineData("k2-thinking", "kimi-k2-thinking")]
    [InlineData("k2.6", "kimi-k2.6")]
    [InlineData("kimi-for-coding", "kimi-k2.7-code")]
    [InlineData("kimi-for-coding-highspeed", "kimi-k2.7-code-highspeed")]
    [InlineData("k3", "kimi-k3")]
    [InlineData("k3-256k", "kimi-k3-256k")]
    public void PublicModelNameFromUpstream_maps_known_upstream_ids(string upstream, string expected)
    {
        KimiModelNormalizer.PublicModelNameFromUpstream(upstream).Should().Be(expected);
    }

    [Fact]
    public void PublicModelNameFromUpstream_keeps_unknown_ids()
    {
        KimiModelNormalizer.PublicModelNameFromUpstream("k4-preview").Should().Be("k4-preview");
        KimiModelNormalizer.PublicModelNameFromUpstream(null).Should().Be(string.Empty);
    }
}
