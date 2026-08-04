using System.Text.Json;
using AITool.Infrastructure.Proxy;
using FluentAssertions;

namespace AITool.ApplicationTests.Proxy;

/// <summary>
/// DeveloperInvocationTraceStore.SummarizeBody 的单元测试。
/// 验证：超长 JSON 字符串值被截断（保留结构），非 JSON 原样返回。
/// </summary>
public sealed class DeveloperInvocationTraceStoreTests
{
    [Fact]
    public void Summarize_body_truncates_only_long_json_string_values()
    {
        var longText = new string('a', 250);
        var body = JsonSerializer.Serialize(new
        {
            shortText = "保留原文",
            prompt = longText,
            nested = new { output = longText },
            items = new[] { longText }
        });

        var summarized = DeveloperInvocationTraceStore.SummarizeBody(body);

        using var document = JsonDocument.Parse(summarized);
        var expected = $"{new string('a', 100)}…(省略130字符){new string('a', 20)}";
        document.RootElement.GetProperty("shortText").GetString().Should().Be("保留原文");
        document.RootElement.GetProperty("prompt").GetString().Should().Be(expected);
        document.RootElement.GetProperty("nested").GetProperty("output").GetString().Should().Be(expected);
        document.RootElement.GetProperty("items")[0].GetString().Should().Be(expected);
    }

    [Fact]
    public void Summarize_body_keeps_non_json_body_unchanged()
    {
        const string body = "not a json body";

        DeveloperInvocationTraceStore.SummarizeBody(body).Should().Be(body);
    }
}
