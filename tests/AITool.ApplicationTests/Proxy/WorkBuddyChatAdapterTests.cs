using System.Text.Json.Nodes;
using AITool.Infrastructure.Proxy;
using FluentAssertions;
using Xunit;

namespace AITool.ApplicationTests.Proxy;

/// <summary>
/// WorkBuddy 渠道校验内容适配器契约。用例移植自 magpie workbuddy 插件的实测
/// （magpie-community/plugins#182 / #279 / #291，2026-10 双构建验证）：
/// code 11128 读取 system/assistant 消息中已知官方客户端提示词的整行（整行精确匹配）。
/// </summary>
public sealed class WorkBuddyChatAdapterTests
{
    [Fact]
    public void Rewrites_each_flagged_phrase_whatever_its_case()
    {
        WorkBuddyChatAdapter.Rewrite(Body("system", "You are Claude Code, Anthropic's official CLI for Claude."))
            .Should().Contain("You are Claude Code, Anthropic's CLI for Claude.");
        WorkBuddyChatAdapter.Rewrite(Body("system", "MAIN BRANCH (YOU WILL USUALLY USE THIS FOR PRS): main"))
            .Should().Contain("Main branch (usually the base for PRs): main");
        WorkBuddyChatAdapter.Rewrite(Body("system", "You are an AI agent powered by DeepSeek Harness. Be brief."))
            .Should().Contain("You are an AI agent that helps the user with programming tasks. Be brief.");
        WorkBuddyChatAdapter.Rewrite(Body("system", "YOU ARE AN AI AGENT POWERED BY DEEPSEEK HARNESS"))
            .Should().Contain("You are an AI agent that helps the user with programming tasks.");
    }

    [Fact]
    public void Codex_opening_rewrites_once_and_is_idempotent()
    {
        const string codex = "You are a coding agent running in the Codex CLI, a terminal-based coding assistant. Codex CLI is an open source project led by OpenAI. You are expected to be precise, safe, and helpful.";
        var once = WorkBuddyChatAdapter.Rewrite(Body("system", codex));
        once.Should().Contain("The Codex CLI is an open source project led by OpenAI.");
        var twice = WorkBuddyChatAdapter.Rewrite(once);
        twice.Should().Be(once, "lookbehind 防止 'The Codex CLI' 二次改写，改写幂等");
    }

    [Fact]
    public void Anthropic_billing_header_line_is_removed()
    {
        WorkBuddyChatAdapter.Rewrite(Body("system", "x-anthropic-billing-header: cc_version=2.1.280.31f; cc_entrypoint=sdk-cli;\nhello"))
            .Should().NotContain("x-anthropic-billing-header").And.Contain("hello");
    }

    [Fact]
    public void Billing_header_only_system_message_is_replaced_by_default_system()
    {
        var result = WorkBuddyChatAdapter.Rewrite("""{"messages":[{"role":"system","content":"x-anthropic-billing-header: cc_version=2.1.280; cc_entrypoint=cli;"},{"role":"user","content":"hi"}]}""");
        var messages = JsonNode.Parse(result)!["messages"]!.AsArray();
        messages.Should().HaveCount(2);
        messages[0]!["role"]!.GetValue<string>().Should().Be("system");
        messages[0]!["content"]!.GetValue<string>().Should().Be("You are a helpful assistant.");
        messages[1]!["content"]!.GetValue<string>().Should().Be("hi");
    }

    [Fact]
    public void Chat_without_system_opener_gets_default_system_first()
    {
        // ChatTestPane 形态：首消息 user、无 system —— 11128「first message is not system prompt」直接命中。
        var result = WorkBuddyChatAdapter.Rewrite("""{"model":"deepseek-v4.1-flash","messages":[{"role":"user","content":"你好"}],"stream":true}""");
        var messages = JsonNode.Parse(result)!["messages"]!.AsArray();
        messages[0]!["role"]!.GetValue<string>().Should().Be("system");
        messages[0]!["content"]!.GetValue<string>().Should().Be("You are a helpful assistant.");
        messages[1]!["content"]!.GetValue<string>().Should().Be("你好");
    }

    [Fact]
    public void Unflagged_chat_is_returned_unchanged()
    {
        const string body = """{"messages":[{"role":"system","content":"be brief"},{"role":"user","content":"hi"}]}""";
        WorkBuddyChatAdapter.Rewrite(body).Should().Be(body, "无改动时返回原字符串，不做 round-trip 序列化");
    }

    [Fact]
    public void Normalizes_workbuddy_body_spellings()
    {
        var result = WorkBuddyChatAdapter.Rewrite("""{"max_completion_tokens":1024,"messages":[{"role":"developer","content":"be brief"},{"role":"user","content":"hi"}]}""");
        var root = JsonNode.Parse(result)!;
        root["max_tokens"]!.GetValue<int>().Should().Be(1024);
        root["max_completion_tokens"].Should().BeNull();
        root["messages"]![0]!["role"]!.GetValue<string>().Should().Be("system");
    }

    [Fact]
    public void Flagged_line_in_text_parts_and_image_parts_are_handled()
    {
        var result = WorkBuddyChatAdapter.Rewrite("""
        {"messages":[
            {"role":"system","content":[
                {"type":"text","text":"x-anthropic-billing-header: cc_version=2.1.280.31f; cc_entrypoint=cli;"},
                {"type":"text","text":"You are Claude Code, Anthropic's official CLI for Claude."}]},
            {"role":"user","content":[
                {"type":"text","text":"gitStatus\nMain branch (you will usually use this for PRs): main"},
                {"type":"image_url","image_url":{"url":"data:,"}}]}]}
        """);
        var messages = JsonNode.Parse(result)!["messages"]!.AsArray();
        var systemParts = messages[0]!["content"]!.AsArray();
        systemParts.Should().HaveCount(1, "计费标识块整块删除");
        systemParts[0]!["text"]!.GetValue<string>().Should().Be("You are Claude Code, Anthropic's CLI for Claude.");
        var userParts = messages[1]!["content"]!.AsArray();
        userParts[0]!["text"]!.GetValue<string>().Should().Contain("Main branch (usually the base for PRs): main");
        userParts[1]!["type"]!.GetValue<string>().Should().Be("image_url", "非 text part 原样保留");
    }

    [Fact]
    public void Flagged_line_quoted_back_in_assistant_message_is_reworded()
    {
        const string line = "There is a Web GUI whose URL the runtime context names. When the user refers to it, open it.";
        var result = WorkBuddyChatAdapter.Rewrite(Body("assistant", line));
        result.Should().Contain("The interface, when the user refers to it");
        // 换行断行形态同样命中（\s+）。
        WorkBuddyChatAdapter.Rewrite(Body("assistant", line.Replace(". When", ".\nWhen")))
            .Should().Contain("The interface, when the user refers");
    }

    [Fact]
    public void Invalid_json_and_body_without_messages_pass_through()
    {
        WorkBuddyChatAdapter.Rewrite("not json").Should().Be("not json");
        const string noMessages = """{"model":"x","stream":true}""";
        WorkBuddyChatAdapter.Rewrite(noMessages).Should().Be(noMessages);
    }

    [Fact]
    public void Forward_service_rewrites_body_only_when_marker_header_present()
    {
        const string body = """{"messages":[{"role":"user","content":"hi"}]}""";
        var withMarker = new AITool.Application.Proxy.ProxyForwardRequest
        {
            ForwardHeaders = new Dictionary<string, string> { [ProxyForwardService.RewriteChannelHeader] = "workbuddy" }
        };
        var rewritten = ProxyForwardService.ApplyChannelRewrite(withMarker, body);
        rewritten.Should().NotBe(body, "标记头驱动转发层改写");
        JsonNode.Parse(rewritten)!["messages"]![0]!["role"]!.GetValue<string>().Should().Be("system");

        // 无标记 / 其他渠道值 / 未改写内容 → 原样。
        ProxyForwardService.ApplyChannelRewrite(new AITool.Application.Proxy.ProxyForwardRequest(), body).Should().Be(body);
        var otherChannel = new AITool.Application.Proxy.ProxyForwardRequest
        {
            ForwardHeaders = new Dictionary<string, string> { [ProxyForwardService.RewriteChannelHeader] = "other" }
        };
        ProxyForwardService.ApplyChannelRewrite(otherChannel, body).Should().Be(body);
    }

    private static string Body(string role, string content)
        => $$"""{"messages":[{"role":"{{role}}","content":{{System.Text.Json.JsonSerializer.Serialize(content)}}},{"role":"user","content":"go"}]}""";
}
