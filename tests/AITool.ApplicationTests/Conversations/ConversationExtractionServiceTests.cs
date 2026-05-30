using AITool.Infrastructure.Conversations;
using FluentAssertions;

namespace AITool.ApplicationTests.Conversations;

/// <summary>
/// 验证工具来源识别和会话标识提取逻辑。
/// </summary>
public sealed class ConversationExtractionServiceTests
{
    private readonly ConversationExtractionService _service = new();

    [Theory]
    [InlineData("claude-cli/2.1.153 (external, claude-vscode, agent-sdk/0.3.153)", "claude-code")]
    [InlineData("codex-tui/0.135.0 (Windows 10.0.19045; x86_64)", "codex")]
    [InlineData("opencode/local ai-sdk/provider-utils/4.0.23 runtime/node.js/24", "open-code")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", "proxy")]
    [InlineData("", "proxy")]
    public void ResolveSourceTool_identifies_tool_from_user_agent(string userAgent, string expected)
    {
        _service.ResolveSourceTool(null, userAgent).Should().Be(expected);
    }

    [Fact]
    public void ResolveSourceTool_prefers_explicit_header_over_user_agent()
    {
        _service.ResolveSourceTool("my-tool", "claude-cli/2.1.153").Should().Be("my-tool");
    }

    [Fact]
    public void ExtractSessionId_reads_claude_code_header()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Claude-Code-Session-Id"] = "ac0d54ee-cdb1-4965-850b-f29b180de504"
        };
        _service.ExtractSessionId(headers).Should().Be("ac0d54ee-cdb1-4965-850b-f29b180de504");
    }

    [Fact]
    public void ExtractSessionId_reads_codex_session_id()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Session-Id"] = "019e7688-a110-7a53-8577-ac34e70913ea"
        };
        _service.ExtractSessionId(headers).Should().Be("019e7688-a110-7a53-8577-ac34e70913ea");
    }

    [Fact]
    public void ExtractSessionId_reads_open_code_session_affinity()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-session-affinity"] = "ses_198c8a7bbffesy0l4mzDS2UarI"
        };
        _service.ExtractSessionId(headers).Should().Be("ses_198c8a7bbffesy0l4mzDS2UarI");
    }

    [Fact]
    public void ExtractSessionId_returns_empty_when_no_known_header()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Bearer sk-test",
            ["Content-Type"] = "application/json"
        };
        _service.ExtractSessionId(headers).Should().BeEmpty();
    }

    [Fact]
    public void ExtractSessionId_prioritizes_claude_code_over_others()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Claude-Code-Session-Id"] = "claude-session",
            ["Session-Id"] = "codex-session"
        };
        _service.ExtractSessionId(headers).Should().Be("claude-session");
    }

    [Fact]
    public void ExtractSessionId_ignores_whitespace_only_values()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Claude-Code-Session-Id"] = "   "
        };
        _service.ExtractSessionId(headers).Should().BeEmpty();
    }

    [Theory]
    [InlineData("claude-code", "ac0d54ee-cdb1-4965", "claude-code:ac0d54ee-cdb1-4965")]
    [InlineData("codex", "019e7688-a110", "codex:019e7688-a110")]
    [InlineData("open-code", "ses_abc123", "open-code:ses_abc123")]
    public void BuildConversationGroupKey_includes_source_tool_for_isolation(string sourceTool, string sessionId, string expected)
    {
        _service.BuildConversationGroupKey(sourceTool, sessionId, Guid.NewGuid())
            .Should().Be(expected);
    }

    [Fact]
    public void BuildConversationGroupKey_uses_request_id_when_no_session()
    {
        var requestId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        _service.BuildConversationGroupKey("proxy", string.Empty, requestId)
            .Should().Be("proxy:request:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    }

    [Fact]
    public void Different_tools_with_same_session_are_isolated()
    {
        var sessionId = "shared-session-id";
        var claudeKey = _service.BuildConversationGroupKey("claude-code", sessionId, Guid.NewGuid());
        var codexKey = _service.BuildConversationGroupKey("codex", sessionId, Guid.NewGuid());
        claudeKey.Should().NotBe(codexKey);
    }
}
