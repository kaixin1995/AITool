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

    [Fact]
    public void NormalizeConversationText_removes_system_reminder_and_keeps_real_user_prompt()
    {
        var text = """
<system-reminder>
Note: c:\\Users\\kaikai.hao\\Desktop\\AI-Tool\\src\\AITool.Web\\Program.cs was modified
</system-reminder>

查看当前未提交的代码，看完后，我再告诉你，我要干什么。
""";

        _service.NormalizeConversationText(text).Should().Be("查看当前未提交的代码，看完后，我再告诉你，我要干什么。");
    }

    [Fact]
    public void ExtractUserInputText_for_responses_only_keeps_last_user_message()
    {
        var requestBody = """
{
  "input": [
    {
      "role": "user",
      "content": [
        { "type": "input_text", "text": "第一轮提问" }
      ]
    },
    {
      "role": "assistant",
      "content": [
        { "type": "output_text", "text": "第一轮回答" }
      ]
    },
    {
      "role": "user",
      "content": [
        { "type": "input_text", "text": "第二轮提问" }
      ]
    }
  ]
}
""";

        _service.ExtractUserInputText(requestBody, "OpenAI", "/v1/responses")
            .Should().Be("第二轮提问");
    }

    [Fact]
    public void ExtractUserInputText_for_responses_ignores_tool_result_wrapped_as_user()
    {
        var requestBody = """
{
  "input": [
    {
      "role": "user",
      "content": [
        { "type": "input_text", "text": "帮我修一下这个报错" }
      ]
    },
    {
      "role": "assistant",
      "content": [
        { "type": "output_text", "text": "我先看一下" }
      ]
    },
    {
      "role": "user",
      "content": [
        { "type": "tool_result", "tool_use_id": "call_123", "content": "Read tool finished" }
      ]
    }
  ]
}
""";

        _service.ExtractUserInputText(requestBody, "OpenAI", "/v1/responses")
            .Should().BeEmpty();
    }

    [Fact]
    public void ExtractAssistantOutput_reads_responses_sse_output_text_delta()
    {
        var responseBody = """
event: response.output_text.delta
data: {"type":"response.output_text.delta","delta":"hello"}

event: response.output_text.delta
data: {"type":"response.output_text.delta","delta":" world"}

data: [DONE]

""";

        _service.ExtractAssistantOutput(responseBody, "OpenAI", "/v1/responses")
            .Should().Be("hello world");
    }

    [Fact]
    public void ExtractAssistantOutput_reads_chat_completions_sse_delta_content()
    {
        var responseBody = """
data: {"choices":[{"delta":{"role":"assistant","content":"hello"}}]}

data: {"choices":[{"delta":{"content":" world"}}]}

data: [DONE]

""";

        _service.ExtractAssistantOutput(responseBody, "OpenAI", "/v1/chat/completions")
            .Should().Be("hello world");
    }

    [Fact]
    public void ExtractAssistantOutput_for_responses_json_keeps_function_call_details()
    {
        var responseBody = """
{
  "output": [
    {
      "type": "message",
      "content": [
        { "type": "output_text", "text": "我开始处理了" }
      ]
    },
    {
      "type": "function_call",
      "name": "Edit",
      "arguments": "{\"file\":\"Foo.cs\",\"action\":\"update\"}"
    }
  ]
}
""";

        _service.ExtractAssistantOutput(responseBody, "OpenAI", "/v1/responses")
            .Should().Be("我开始处理了\n工具调用: Edit\n{\"file\":\"Foo.cs\",\"action\":\"update\"}");
    }

    [Fact]
    public void ExtractAssistantOutput_for_responses_sse_keeps_function_call_details()
    {
        var responseBody = """
event: response.output_item.added
data: {"type":"response.output_item.added","item":{"type":"function_call","name":"Edit","arguments":"","call_id":"call_1"}}

event: response.function_call_arguments.delta
data: {"type":"response.function_call_arguments.delta","item_id":"call_1","delta":"{\"file\":\"Foo.cs\"}"}

event: response.output_text.delta
data: {"type":"response.output_text.delta","delta":"修改完成"}

data: [DONE]

""";

        _service.ExtractAssistantOutput(responseBody, "OpenAI", "/v1/responses")
            .Should().Be("工具调用: Edit\n{\"file\":\"Foo.cs\"}\n\n修改完成");
    }

    [Fact]
    public void ExtractToolResultOutput_keeps_structured_patch_details()
    {
        var requestBody = """
{
  "input": [
    {
      "type": "function_call_output",
      "output": "updated",
      "toolUseResult": {
        "filePath": "Foo.cs",
        "structuredPatch": [
          {
            "lines": [
              " public class Foo",
              "+    public string Name { get; set; }",
              "-    public int Old { get; set; }"
            ]
          }
        ]
      }
    }
  ]
}
""";

        var result = _service.ExtractToolResultOutput(requestBody, "OpenAI", "/v1/responses");

        result.Should().Contain("工具结果: 代码改动");
        result.Should().Contain("文件: Foo.cs");
        result.Should().Contain("+    public string Name { get; set; }");
        result.Should().Contain("-    public int Old { get; set; }");
    }

    [Fact]
    public void ExtractAssistantOutput_for_anthropic_json_keeps_edit_change_details()
    {
        var responseBody = """
{
  "content": [
    {
      "type": "tool_use",
      "name": "Edit",
      "input": {
        "file_path": "Index.cshtml",
        "old_string": "    .conversation-log-main {",
        "new_string": "    .conversation-session-meta {\n        white-space: nowrap;\n    }\n\n    .conversation-log-main {"
      }
    }
  ]
}
""";

        var result = _service.ExtractAssistantOutput(responseBody, "Anthropic", "/v1/messages");

        result.Should().Contain("工具调用: Edit");
        result.Should().Contain("工具结果: 代码改动");
        result.Should().Contain("文件: Index.cshtml");
        result.Should().Contain("-    .conversation-log-main {");
        result.Should().Contain("+    .conversation-session-meta {");
        result.Should().NotContain("old_string");
        result.Should().NotContain("new_string");
    }

    [Fact]
    public void ExtractAssistantOutput_for_anthropic_sse_keeps_input_json_delta_change_details()
    {
        var responseBody = """
event: content_block_start
data: {"type":"content_block_start","content_block":{"type":"tool_use","name":"Edit","input":{}}}

event: content_block_delta
data: {"type":"content_block_delta","delta":{"type":"input_json_delta","partial_json":"{\"file_path\":\"Index.cshtml\",\"old_string\":\"old\",\"new_string\":\"new\"}"}}

event: content_block_stop
data: {"type":"content_block_stop"}

event: message_stop
data: {"type":"message_stop"}

""";

        var result = _service.ExtractAssistantOutput(responseBody, "Anthropic", "/v1/messages");

        result.Should().Contain("工具调用: Edit");
        result.Should().Contain("工具结果: 代码改动");
        result.Should().Contain("文件: Index.cshtml");
        result.Should().Contain("-old");
        result.Should().Contain("+new");
        result.Should().NotContain("old_string");
        result.Should().NotContain("new_string");
        result.Should().NotContain("{}", because: "content_block_start 中的空 input 不是最终工具参数");
    }

    [Fact]
    public void ExtractAssistantOutput_for_anthropic_sse_skips_read_only_empty_tool_arguments()
    {
        var responseBody = """
event: content_block_start
data: {"type":"content_block_start","content_block":{"type":"tool_use","name":"Read","input":{}}}

event: content_block_stop
data: {"type":"content_block_stop"}

event: message_stop
data: {"type":"message_stop"}

""";

        _service.ExtractAssistantOutput(responseBody, "Anthropic", "/v1/messages")
            .Should().BeEmpty();
    }

    [Fact]
    public void ExtractAssistantOutput_for_anthropic_json_skips_read_only_tool_call()
    {
        var responseBody = """
{
  "content": [
    {
      "type": "tool_use",
      "name": "Read",
      "input": {
        "file_path": "Index.cshtml"
      }
    }
  ]
}
""";

        _service.ExtractAssistantOutput(responseBody, "Anthropic", "/v1/messages")
            .Should().BeEmpty();
    }

    [Fact]
    public void ExtractUserInputText_for_anthropic_ignores_tool_result_wrapped_as_user()
    {
        var requestBody = """
{
  "messages": [
    {
      "role": "user",
      "content": [
        { "type": "text", "text": "请继续处理这个问题" }
      ]
    },
    {
      "role": "assistant",
      "content": [
        { "type": "tool_use", "id": "tool_1", "name": "Read", "input": {} }
      ]
    },
    {
      "role": "user",
      "content": [
        { "type": "tool_result", "tool_use_id": "tool_1", "content": "file content" }
      ]
    }
  ]
}
""";

        _service.ExtractUserInputText(requestBody, "Anthropic", "/v1/messages")
            .Should().BeEmpty();
    }

    [Fact]
    public void ExtractAssistantOutput_reads_anthropic_sse_text_delta()
    {
        var responseBody = """
event: content_block_delta
data: {"type":"content_block_delta","delta":{"type":"text_delta","text":"hello"}}

event: content_block_delta
data: {"type":"content_block_delta","delta":{"type":"text_delta","text":" world"}}

event: message_stop
data: {"type":"message_stop"}

""";

        _service.ExtractAssistantOutput(responseBody, "Anthropic", "/v1/messages")
            .Should().Be("hello world");
    }

    [Fact]
    public void ExtractAssistantOutput_truncates_huge_edit_diff_to_avoid_memory_blowup()
    {
        // 模拟 Edit 工具携带整文件内容（数百 KB ~ 数 MB），必须被截断，
        // 否则提取结果会整体进入对话记录写入队列导致内存暴涨。
        // 上限为 2000 行，构造 8000 行确保触发截断。
        // 注意：JSON 字符串值里换行需转义为 \n，这里构造合法 JSON。
        var hugeLines = string.Join("\\n", Enumerable.Range(0, 8000).Select(i => $"line {i}"));
        var responseBody = $$"""
{
  "content": [
    {
      "type": "tool_use",
      "name": "Edit",
      "input": {
        "file_path": "BigFile.cs",
        "old_string": "{{hugeLines}}",
        "new_string": "{{hugeLines}}"
      }
    }
  ]
}
""";

        var result = _service.ExtractAssistantOutput(responseBody, "Anthropic", "/v1/messages");

        // 截断标记必须出现，证明没有把 8000 行整体保留。
        result.Should().Contain("已截断");
        result.Should().Contain("BigFile.cs");
        // 截断到 2000 行上限（old/new 各一份），体积应远小于 8000 行原始内容（约 450KB）。
        result.Length.Should().BeLessThan(120000);
    }

    [Fact]
    public void ExtractToolResultOutput_truncates_huge_structured_patch_lines()
    {
        // structuredPatch.lines 可能包含整个文件的所有行，必须截断（上限 2000 行）。
        var hugeLines = Enumerable.Range(0, 8000).Select(i => $" line {i}");
        var linesJson = string.Join(",", hugeLines.Select(l => $"\"{l}\""));
        var requestBody = $$"""
{
  "input": [
    {
      "type": "function_call_output",
      "output": "updated",
      "toolUseResult": {
        "filePath": "BigFile.cs",
        "structuredPatch": [
          { "lines": [{{linesJson}}] }
        ]
      }
    }
  ]
}
""";

        var result = _service.ExtractToolResultOutput(requestBody, "OpenAI", "/v1/responses");

        result.Should().Contain("已截断");
        // 截断到 2000 行上限，体积应远小于 8000 行原始内容。
        result.Length.Should().BeLessThan(120000);
    }
}
