using AITool.Infrastructure.Google;
using FluentAssertions;
using Xunit;

namespace AITool.IntegrationTests.Google;

/// <summary>
/// Antigravity 额度两桶聚合：上游按模型返回的 quotaInfo 是噪声，
/// 真实额度只有 Claude/GPT-OSS 与 Gemini 两桶（见 GoogleQuotaParser）。
/// </summary>
public sealed class GoogleQuotaMatchingTests
{
    [Fact]
    public void Parse_aggregates_models_into_two_buckets()
    {
        var rawJson = """
        {
          "models": {
            "gemini-3.7-flash-high": { "quotaInfo": { "remainingFraction": 0.4 } },
            "gemini-3.7-flash-tiered": { "quotaInfo": { "remainingFraction": 0.4 } },
            "claude-sonnet-4-6": { "quotaInfo": { "remainingFraction": 0.9 } },
            "gpt-oss-120b": { "quotaInfo": { "remainingFraction": 0.9 } },
            "some-other-model": { "quotaInfo": { "remainingFraction": 0.0 } }
          }
        }
        """;

        var windows = GoogleQuotaParser.Parse(rawJson);

        windows.Should().NotBeNull();
        windows!.Should().HaveCount(2);

        var gemini = windows.Single(w => w.Id == GoogleQuotaParser.GeminiBucketId);
        gemini.UsedPercent.Should().BeApproximately(60d, 0.01, "Gemini 桶按成员换算已用百分比");

        var claude = windows.Single(w => w.Id == GoogleQuotaParser.ClaudeGptOssBucketId);
        claude.UsedPercent.Should().BeApproximately(10d, 0.01, "Claude/GPT-OSS 桶聚合 claude 与 gpt-oss 前缀");
    }

    [Fact]
    public void Parse_takes_max_used_percent_within_bucket()
    {
        var rawJson = """
        {
          "models": {
            "gemini-3.7-flash": { "quotaInfo": { "remainingFraction": 0.9 } },
            "gemini-3.1-pro": { "quotaInfo": { "remainingFraction": 0.1 } }
          }
        }
        """;

        var windows = GoogleQuotaParser.Parse(rawJson);

        var gemini = windows!.Single(w => GoogleQuotaParser.IsGeminiBucket(w.Id));
        gemini.UsedPercent.Should().BeApproximately(90d, 0.01, "桶内取最大已用（最保守）");
    }

    [Fact]
    public void Parse_takes_reset_time_from_bucket_members()
    {
        var rawJson = """
        {
          "models": {
            "gemini-3.7-flash": { "quotaInfo": { "remainingFraction": 0.5 } },
            "gemini-3.1-pro": { "quotaInfo": { "remainingFraction": 0.5, "resetTime": "2099-01-01T00:00:00Z" } }
          }
        }
        """;

        var windows = GoogleQuotaParser.Parse(rawJson);

        var gemini = windows!.Single(w => GoogleQuotaParser.IsGeminiBucket(w.Id));
        gemini.ResetAtUtc.Should().NotBeNull("桶内任一成员带重置时间即透出");
        gemini.ResetLabel.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Parse_ignores_models_without_remaining_fraction()
    {
        var rawJson = """
        {
          "models": {
            "gemini-3.7-flash": { "quotaInfo": {} },
            "claude-sonnet-4-6": { "enabled": true }
          }
        }
        """;

        GoogleQuotaParser.Parse(rawJson).Should().BeNull("无有效 remainingFraction 不默认 100%");
    }

    [Fact]
    public void Parse_ignores_unclassified_models()
    {
        var rawJson = """
        {
          "models": {
            "some-other-model": { "quotaInfo": { "remainingFraction": 0.0 } },
            "another-one": { "quotaInfo": { "remainingFraction": 0.5 } }
          }
        }
        """;

        GoogleQuotaParser.Parse(rawJson).Should().BeNull("非 claude/gpt-oss/gemini 前缀的额度数据是噪声");
    }
}
