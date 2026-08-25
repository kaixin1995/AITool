using System.Text.Json;
using AITool.Web.Services;
using FluentAssertions;

namespace AITool.IntegrationTests;

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

    [Fact]
    public void Get_and_List_preserve_PreparedRequestHeaders_on_entry_and_attempts()
    {
        var store = new DeveloperInvocationTraceStore();
        var traceId = store.AddRequest(new DeveloperInvocationTraceRequest
        {
            RequestId = Guid.NewGuid(),
            Source = "open-code",
            RequestPath = "/v1/chat/completions",
            RequestModel = "1M",
            RequestBody = "{\"prompt\":\"hi\"}",
            RequestHeaders = new Dictionary<string, string> { { "User-Agent", "curl/7.0" } }
        });

        var attemptId = store.AddAttempt(traceId, new DeveloperInvocationAttempt
        {
            AttemptedModel = "gemini-3.7-flash-high",
            UpstreamProtocolType = "Gemini",
            ForwardingMode = "bridge",
            TargetSiteName = "GeminiPro1",
            PreparedRequestBody = "{}",
            PreparedRequestHeaders = new Dictionary<string, string>
            {
                { "User-Agent", "antigravity/1.10.4 linux/x86_64" },
                { "requestId", "req-123" }
            }
        });

        var detail = store.Get(traceId);
        detail.Should().NotBeNull();
        detail!.PreparedRequestHeaders.Should().ContainKey("User-Agent");
        detail.PreparedRequestHeaders["User-Agent"].Should().Be("antigravity/1.10.4 linux/x86_64");
        detail.Attempts.Should().HaveCount(1);
        detail.Attempts[0].PreparedRequestHeaders.Should().ContainKey("User-Agent");
        detail.Attempts[0].PreparedRequestHeaders["User-Agent"].Should().Be("antigravity/1.10.4 linux/x86_64");

        var list = store.List();
        list.Should().ContainSingle();
        list[0].PreparedRequestHeaders.Should().ContainKey("User-Agent");
    }
}
