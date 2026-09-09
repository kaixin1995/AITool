using AITool.Admin.Services;
using FluentAssertions;

namespace AITool.Admin.IntegrationTests;

/// <summary>
/// AiAssistantService 响应提取的契约：各协议/各响应形态都必须能取出正文。
/// 背景：OpenAI 兼容站点的新版数组 content 形态曾被误判为「空内容」，
/// 导致「AI 查最新版 / AI 查价格」不可用。
/// </summary>
public sealed class AiAssistantResponseExtractionTests
{
    [Fact]
    public void OpenAI_string_content_is_extracted()
    {
        const string body = """
        {"choices":[{"message":{"role":"assistant","content":"{\"version\":\"1.2.3\"}"}}]}
        """;
        AiAssistantService.ExtractChatCompletionContent(body, "OpenAI").Content
            .Should().Be("{\"version\":\"1.2.3\"}");
    }

    [Fact]
    public void OpenAI_array_content_is_extracted()
    {
        const string body = """
        {"choices":[{"message":{"role":"assistant","content":[{"type":"text","text":"hello "},{"type":"text","text":"world"}]}}]}
        """;
        AiAssistantService.ExtractChatCompletionContent(body, "OpenAI").Content
            .Should().Be("hello world");
    }

    [Fact]
    public void OpenAI_legacy_top_level_text_is_extracted()
    {
        const string body = """
        {"choices":[{"text":"legacy completion text"}]}
        """;
        AiAssistantService.ExtractChatCompletionContent(body, "OpenAI").Content
            .Should().Be("legacy completion text");
    }

    [Fact]
    public void Anthropic_content_blocks_are_concatenated()
    {
        const string body = """
        {"content":[{"type":"thinking","thinking":"let me think"},{"type":"text","text":"answer"}]}
        """;
        var (content, reasoning) = AiAssistantService.ExtractChatCompletionContent(body, "Anthropic");
        content.Should().Be("answer");
        reasoning.Should().Be("let me think");
    }

    [Fact]
    public void Gemini_candidates_parts_are_concatenated()
    {
        const string body = """
        {"candidates":[{"content":{"parts":[{"text":"part1"},{"text":"part2"}]}}]}
        """;
        AiAssistantService.ExtractChatCompletionContent(body, "Gemini").Content
            .Should().Be("part1part2");
    }

    [Fact]
    public void Responses_output_text_is_extracted()
    {
        const string body = """
        {"output_text":"direct answer"}
        """;
        AiAssistantService.ExtractChatCompletionContent(body, "Responses").Content
            .Should().Be("direct answer");
    }

    [Fact]
    public void Responses_output_message_array_is_extracted()
    {
        const string body = """
        {"output":[{"type":"message","content":[{"type":"output_text","text":"nested answer"}]}]}
        """;
        AiAssistantService.ExtractChatCompletionContent(body, "Responses").Content
            .Should().Be("nested answer");
    }

    [Fact]
    public void Empty_or_invalid_body_yields_empty_content()
    {
        AiAssistantService.ExtractChatCompletionContent("", "OpenAI").Content.Should().BeEmpty();
        AiAssistantService.ExtractChatCompletionContent("not json", "OpenAI").Content.Should().BeEmpty();
        AiAssistantService.ExtractChatCompletionContent("{}", "OpenAI").Content.Should().BeEmpty();
    }

    [Fact]
    public void ExtractJsonBlock_strips_markdown_fence()
    {
        AiAssistantService.ExtractJsonBlock("前言```json\n[{\"id\":\"m1\"}]\n```后记")
            .Should().Be("[{\"id\":\"m1\"}]");
    }

    [Fact]
    public void ExtractJsonBlock_takes_outermost_object_or_array()
    {
        AiAssistantService.ExtractJsonBlock("说明 {\"version\":\"1.0\"} 结束")
            .Should().Be("{\"version\":\"1.0\"}");
        AiAssistantService.ExtractJsonBlock("前 [1,2] 后").Should().Be("[1,2]");
        AiAssistantService.ExtractJsonBlock("").Should().BeEmpty();
    }
}
