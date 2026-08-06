using AITool.Application.UsageLogs;
using FluentAssertions;

namespace AITool.ApplicationTests.UsageLogs;

/// <summary>
/// 验证使用日志错误分类器的固定优先级和分类结果。
/// </summary>
public sealed class UsageLogErrorClassifierTests
{
    /// <summary>
    /// 成功请求不应产生错误分类，即使日志中残留了错误文本。
    /// </summary>
    [Fact]
    public void Classify_success_returns_null()
    {
        var entry = new UsageLogEntry
        {
            Status = "success",
            ErrorMessage = "ignored error text"
        };

        UsageLogErrorClassifier.Classify(entry).Should().BeNull();
    }

    /// <summary>
    /// 流中断应优先于其他失败原因。
    /// </summary>
    [Fact]
    public void Classify_stream_interruption_has_highest_priority()
    {
        var entry = new UsageLogEntry
        {
            Status = "fail",
            HttpStatusCode = 500,
            ErrorMessage = "timeout from upstream",
            IsStreamInterrupted = true
        };

        UsageLogErrorClassifier.Classify(entry).Should().Be("stream-interrupted");
    }

    /// <summary>
    /// 状态码为空或为零时，包含 timeout 的文本仍应归类为超时。
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void Classify_timeout_text_with_missing_status_code_returns_timeout(int? httpStatusCode)
    {
        var entry = new UsageLogEntry
        {
            Status = "fail",
            HttpStatusCode = httpStatusCode,
            ErrorMessage = "upstream timeout"
        };

        UsageLogErrorClassifier.Classify(entry).Should().Be("timeout");
    }

    /// <summary>
    /// 固定状态码应映射到对应的错误分类。
    /// </summary>
    [Theory]
    [InlineData(401, "authentication")]
    [InlineData(408, "timeout")]
    [InlineData(429, "rate-limit")]
    [InlineData(404, "model-not-found")]
    [InlineData(500, "upstream-error")]
    [InlineData(503, "upstream-error")]
    public void Classify_http_status_returns_expected_category(int httpStatusCode, string expectedCategory)
    {
        var entry = new UsageLogEntry
        {
            Status = "fail",
            HttpStatusCode = httpStatusCode
        };

        UsageLogErrorClassifier.Classify(entry).Should().Be(expectedCategory);
    }

    /// <summary>
    /// 无法匹配固定规则时，网络错误文本和未知文本分别归入网络错误与其他错误。
    /// </summary>
    [Theory]
    [InlineData("connection refused", null, "network-error")]
    [InlineData("unrecognized failure", 400, "other")]
    public void Classify_text_returns_category_without_error_body(
        string errorMessage,
        int? httpStatusCode,
        string expectedCategory)
    {
        var entry = new UsageLogEntry
        {
            Status = "fail",
            HttpStatusCode = httpStatusCode,
            ErrorMessage = errorMessage
        };

        var category = UsageLogErrorClassifier.Classify(entry);

        category.Should().Be(expectedCategory);
        category.Should().NotContain(errorMessage);
    }
}
