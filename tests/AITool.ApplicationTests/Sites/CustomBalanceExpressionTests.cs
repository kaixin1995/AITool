using AITool.Infrastructure.Sites;
using FluentAssertions;
using Xunit;

namespace AITool.ApplicationTests.Sites;

/// <summary>
/// 自定义余额表达式求值器测试：路径取值（$. 与 $data. 两种写法）、四则运算与优先级、
/// 数字字符串兼容、除零/缺失路径/非数值等错误路径。
/// </summary>
public sealed class CustomBalanceExpressionTests
{
    private const string NewApiBody = """
        { "success": true, "data": { "quota": 5000000, "used_quota": 1000000, "user": { "email": "a@b.c" } } }
        """;

    [Fact]
    public void Simple_path_extraction()
    {
        CustomBalanceExpression.TryEvaluate("$.data.quota", NewApiBody, out var value, out _)
            .Should().BeTrue();
        value.Should().Be(5000000);
    }

    [Fact]
    public void Magpie_style_data_root_is_equivalent()
    {
        // magpie balanceExpr 用 $data.x 写法，等价 $.x。
        CustomBalanceExpression.TryEvaluate("$data.data.quota", NewApiBody, out var value, out _)
            .Should().BeTrue();
        value.Should().Be(5000000);
    }

    [Fact]
    public void New_api_quota_division_preset()
    {
        // new-api 面板：quota / 500000 = 美元余额。
        CustomBalanceExpression.TryEvaluate("$.data.quota / 500000", NewApiBody, out var value, out _)
            .Should().BeTrue();
        value.Should().Be(10);
    }

    [Fact]
    public void Arithmetic_precedence_and_parentheses()
    {
        var body = """{ "a": 6, "b": 4, "c": 5 }""";
        CustomBalanceExpression.TryEvaluate("$.a - $.b - $.c", body, out var leftToRight, out _).Should().BeTrue();
        leftToRight.Should().Be(-3); // (6-4)-5，左结合

        CustomBalanceExpression.TryEvaluate("$.a - ($.b - $.c)", body, out var grouped, out _).Should().BeTrue();
        grouped.Should().Be(7);

        CustomBalanceExpression.TryEvaluate("$.a + $.b * 2", body, out var precedence, out _).Should().BeTrue();
        precedence.Should().Be(14);
    }

    [Fact]
    public void Array_index_and_numeric_string()
    {
        var body = """
            { "balance_infos": [ { "currency": "CNY", "total_balance": "88.60" } ] }
            """;
        CustomBalanceExpression.TryEvaluate("$.balance_infos[0].total_balance", body, out var value, out _)
            .Should().BeTrue();
        value.Should().Be(88.6);
    }

    [Fact]
    public void Bare_root_number_without_expression()
    {
        CustomBalanceExpression.TryEvaluate(null, "42.5", out var value, out _).Should().BeTrue();
        value.Should().Be(42.5);
        CustomBalanceExpression.TryEvaluate("", "\"15\"", out var textNumber, out _).Should().BeTrue();
        textNumber.Should().Be(15);
    }

    [Fact]
    public void Division_by_zero_is_an_error()
    {
        CustomBalanceExpression.TryEvaluate("$.data.quota / (1 - 1)", NewApiBody, out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("除以 0");
    }

    [Fact]
    public void Missing_path_is_an_error()
    {
        CustomBalanceExpression.TryEvaluate("$.data.not_exist", NewApiBody, out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("not_exist");
    }

    [Fact]
    public void Non_numeric_path_value_is_an_error()
    {
        CustomBalanceExpression.TryEvaluate("$.data.user.email", NewApiBody, out _, out var error)
            .Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Invalid_json_body_is_an_error()
    {
        CustomBalanceExpression.TryEvaluate("$.a", "not json", out _, out var error)
            .Should().BeFalse();
        error.Should().Contain("JSON");
    }

    [Fact]
    public void Negative_intermediate_and_unary_minus()
    {
        var body = """{ "x": 3 }""";
        CustomBalanceExpression.TryEvaluate("-$.x * 2", body, out var value, out _).Should().BeTrue();
        value.Should().Be(-6);
    }
}
