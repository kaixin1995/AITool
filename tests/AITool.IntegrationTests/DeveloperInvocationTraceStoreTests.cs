using System.Text.Json;
using AITool.Application.Google;
using AITool.Web.Services;
using FluentAssertions;

namespace AITool.IntegrationTests;

public sealed class DeveloperInvocationTraceStoreTests
{
    [Fact]
    public void Admission_and_cancellation_settle_without_inventing_an_attempt()
    {
        var store = new DeveloperInvocationTraceStore();
        var id = store.AddRequest(new DeveloperInvocationTraceRequest { RequestId = Guid.NewGuid() });
        store.MarkAcquiringSlot(id, Guid.NewGuid(), "site", "model");
        var queued = store.Get(id)!;
        queued.Status.Should().Be("pending");
        queued.Stage.Should().Be("acquiring_slot");
        queued.StageStartedAt.Should().NotBeNull();
        queued.TargetSiteName.Should().Be("site");
        queued.Attempts.Should().BeEmpty();
        store.CompletePendingRequest(id, "canceled", 0, "Client disconnected");
        var done = store.Get(id)!;
        done.Status.Should().Be("canceled");
        done.Stage.Should().Be("completed");
        done.Attempts.Should().BeEmpty();
        store.CompletePendingRequest(id, "fail", 502, "must not overwrite");
        store.Get(id)!.Status.Should().Be("canceled");
    }

    [Fact]
    public void Cancellation_after_failed_attempt_settles_request_without_overwriting_attempt()
    {
        var store = new DeveloperInvocationTraceStore();
        var id = store.AddRequest(new DeveloperInvocationTraceRequest { RequestId = Guid.NewGuid() });
        var attempt = store.AddAttempt(id, new DeveloperInvocationAttempt { TargetSiteName = "site" });
        store.CompleteAttempt(id, attempt, new DeveloperInvocationResult { Status = "fail", StatusCode = 404, ErrorMessage = "missing" });
        store.CompletePendingRequest(id, "canceled", 0, "Client disconnected");
        var entry = store.Get(id)!;
        entry.Status.Should().Be("canceled");
        entry.Attempts.Single().Status.Should().Be("fail");
        entry.Attempts.Single().StatusCode.Should().Be(404);
    }

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
                { "User-Agent", GoogleAccountKinds.AntigravityUserAgent },
                { "requestId", "req-123" }
            }
        });

        var detail = store.Get(traceId);
        detail.Should().NotBeNull();
        detail!.PreparedRequestHeaders.Should().ContainKey("User-Agent");
        detail.PreparedRequestHeaders["User-Agent"].Should().Be(GoogleAccountKinds.AntigravityUserAgent);
        detail.Attempts.Should().HaveCount(1);
        detail.Attempts[0].PreparedRequestHeaders.Should().ContainKey("User-Agent");
        detail.Attempts[0].PreparedRequestHeaders["User-Agent"].Should().Be(GoogleAccountKinds.AntigravityUserAgent);

        var list = store.List();
        list.Should().ContainSingle();
        list[0].PreparedRequestHeaders.Should().ContainKey("User-Agent");
    }
    [Fact]
    public void Gateway_failure_completes_request_without_an_upstream_attempt()
    {
        var store = new DeveloperInvocationTraceStore();
        var id = store.AddRequest(new DeveloperInvocationTraceRequest { RequestId = Guid.NewGuid(), ProtocolType = "Responses" });
        store.CompleteRequestFailure(id, 400, "invalid_reference_shape", "{}");
        var trace = store.Get(id)!;
        trace.Status.Should().Be("failed");
        trace.StatusCode.Should().Be(400);
        trace.ErrorMessage.Should().Be("invalid_reference_shape");
        trace.Attempts.Should().BeEmpty();
    }
}
