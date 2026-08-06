using AITool.Web.Contracts;
using FluentAssertions;

namespace AITool.IntegrationTests.Contracts;

/// <summary>
/// ApiResponse 契约单元测试：验证工厂方法、泛型推断、序列化字段。
/// </summary>
public sealed class ApiResponseTests
{
    /// <summary>
    /// 验证 Ok(message) 返回 success=true。
    /// </summary>
    [Fact]
    public void Ok_message_sets_success_true()
    {
        var resp = ApiResponse.Ok("done");
        resp.Success.Should().BeTrue();
        resp.Message.Should().Be("done");
        resp.ErrorCode.Should().BeNull();
    }

    /// <summary>
    /// 验证 Fail 设置 success=false + errorCode。
    /// </summary>
    [Fact]
    public void Fail_sets_success_false_and_error_code()
    {
        var resp = ApiResponse.Fail("bad input", "invalid_input");
        resp.Success.Should().BeFalse();
        resp.Message.Should().Be("bad input");
        resp.ErrorCode.Should().Be("invalid_input");
    }

    /// <summary>
    /// 验证泛型 Ok(data) 携带数据且能正确推断类型（含匿名类型）。
    /// </summary>
    [Fact]
    public void Ok_with_data_carries_payload()
    {
        var resp = ApiResponse.Ok(new { id = 42, name = "test" }, null);
        resp.Success.Should().BeTrue();
        resp.Data!.id.Should().Be(42);
        resp.Data!.name.Should().Be("test");
    }

    /// <summary>
    /// 验证泛型 Ok(data, message) 同时携带数据和消息。
    /// </summary>
    [Fact]
    public void Ok_with_data_and_message_carries_both()
    {
        var resp = ApiResponse.Ok<int>(100, "created");
        resp.Success.Should().BeTrue();
        resp.Data.Should().Be(100);
        resp.Message.Should().Be("created");
    }

    /// <summary>
    /// 验证泛型 Fail 返回正确的泛型载体类型。
    /// </summary>
    [Fact]
    public void Fail_generic_returns_typed_carrier()
    {
        var resp = ApiResponse.Fail<int>("not found", "not_found");
        resp.Success.Should().BeFalse();
        resp.Data.Should().Be(0);
        resp.Message.Should().Be("not found");
    }

    /// <summary>
    /// 验证 ApiResponse&lt;T&gt; 可赋值给基类 ApiResponse（多态）。
    /// </summary>
    [Fact]
    public void Generic_response_is_assignable_to_base()
    {
        ApiResponse generic = ApiResponse.Ok(1);
        generic.Success.Should().BeTrue();
    }
}
