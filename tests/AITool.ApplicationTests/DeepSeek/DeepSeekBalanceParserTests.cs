using AITool.Infrastructure.DeepSeek;
using FluentAssertions;
using Xunit;

namespace AITool.ApplicationTests.DeepSeek;

/// <summary>
/// DeepSeek 余额响应解析测试（GET /user/balance，官方文档：数值为字符串）。
/// </summary>
public sealed class DeepSeekBalanceParserTests
{
    private const string OfficialSample = """
        {
          "is_available": true,
          "balance_infos": [
            {
              "currency": "CNY",
              "total_balance": "110.00",
              "granted_balance": "10.00",
              "topped_up_balance": "100.00"
            }
          ]
        }
        """;

    [Fact]
    public void Parse_extracts_official_sample_fields()
    {
        var result = DeepSeekBalanceParser.Parse(OfficialSample);

        result.Error.Should().BeNull();
        result.Balances.Should().HaveCount(1);
        var balance = result.Balances[0];
        balance.Currency.Should().Be("CNY");
        balance.TotalBalance.Should().Be(110.00m);
        balance.GrantedBalance.Should().Be(10.00m);
        balance.ToppedUpBalance.Should().Be(100.00m);
    }

    [Fact]
    public void Parse_handles_multiple_currencies_and_optional_fields()
    {
        var result = DeepSeekBalanceParser.Parse("""
            {
              "is_available": true,
              "balance_infos": [
                { "currency": "CNY", "total_balance": "5.50" },
                { "currency": "USD", "total_balance": 20, "granted_balance": null, "topped_up_balance": "20.00" }
              ]
            }
            """);

        result.Error.Should().BeNull();
        result.Balances.Should().HaveCount(2);
        result.Balances[0].Currency.Should().Be("CNY");
        result.Balances[0].TotalBalance.Should().Be(5.50m);
        result.Balances[0].GrantedBalance.Should().BeNull();
        result.Balances[0].ToppedUpBalance.Should().BeNull();
        // 数字形态的值同样接受；显式 null 的可选项保持 null。
        result.Balances[1].TotalBalance.Should().Be(20m);
        result.Balances[1].GrantedBalance.Should().BeNull();
        result.Balances[1].ToppedUpBalance.Should().Be(20.00m);
    }

    [Fact]
    public void Parse_skips_entries_without_currency_or_total()
    {
        var result = DeepSeekBalanceParser.Parse("""
            {
              "balance_infos": [
                { "total_balance": "1.00" },
                { "currency": "CNY", "total_balance": "not-a-number" },
                { "currency": "USD", "total_balance": "2.00" }
              ]
            }
            """);

        result.Error.Should().BeNull();
        result.Balances.Should().ContainSingle();
        result.Balances[0].Currency.Should().Be("USD");
        result.Balances[0].TotalBalance.Should().Be(2.00m);
    }

    [Fact]
    public void Parse_empty_balance_infos_is_success_without_error()
    {
        // 空数组=成功但无数据（调用方给「暂无数据」），与结构错误区分。
        var result = DeepSeekBalanceParser.Parse("""{ "is_available": false, "balance_infos": [] }""");
        result.Error.Should().BeNull();
        result.Balances.Should().BeEmpty();
    }

    [Fact]
    public void Parse_invalid_payloads_report_error()
    {
        DeepSeekBalanceParser.Parse("").Error.Should().NotBeNull();
        DeepSeekBalanceParser.Parse("not json").Error.Should().NotBeNull();
        DeepSeekBalanceParser.Parse("""{ "foo": 1 }""").Error.Should().NotBeNull();
        DeepSeekBalanceParser.Parse("[1,2]").Error.Should().NotBeNull();
    }

    [Fact]
    public void MatchesBaseUrl_hits_deepseek_hosts_only()
    {
        DeepSeekBalanceParser.MatchesBaseUrl("https://api.deepseek.com").Should().BeTrue();
        DeepSeekBalanceParser.MatchesBaseUrl("https://api.deepseek.com/v1").Should().BeTrue();
        DeepSeekBalanceParser.MatchesBaseUrl("https://API.DeepSeek.COM").Should().BeTrue();
        // host 级匹配：不认路径里出现域名或伪造后缀。
        DeepSeekBalanceParser.MatchesBaseUrl("https://deepseek.com.example.net/v1").Should().BeFalse();
        DeepSeekBalanceParser.MatchesBaseUrl("https://api.example.com/deepseek.com").Should().BeFalse();
        DeepSeekBalanceParser.MatchesBaseUrl("https://notdeepseek.com").Should().BeFalse();
        DeepSeekBalanceParser.MatchesBaseUrl("").Should().BeFalse();
    }
}
