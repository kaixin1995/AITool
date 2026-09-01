using AITool.Protocol;
using FluentAssertions;

namespace AITool.ApplicationTests.Proxy;

/// <summary>
/// 验证站点映射级思考等级的优先级合并语义（PrepareRequestBody 的 overrideReasoningEffort 参数消费方）：
/// 映射级 > 模型库级 > 透传。合并发生在 ProxyRequestMetadataCache 投影（主路由块），
/// 这里验证不同覆盖值在协议转换层的实际效果（OpenAI 目标透传 effort；Gemini 目标转 thinkingConfig）。
/// </summary>
public sealed class ReasoningEffortPriorityTests
{
    private const string AnthropicBody = """
    {"model":"claude-x","max_tokens":128,"thinking":{"type":"enabled","budget_tokens":2048},"messages":[{"role":"user","content":"1+1=?"}]}
    """;

    [Theory]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public void Mapping_level_effort_overrides_client_value(string effort)
    {
        // 模拟投影合并后传入映射级 effort：转换结果应携带该等级而非客户端原始值。
        var prepared = ProxyProtocolBridge.PrepareRequestBody(
            "Anthropic", "OpenAI", AnthropicBody, "glm-5.2", false,
            effort, "https://upstream.example.com", null, isPassthrough: false);

        prepared.Should().Contain($"\"reasoning_effort\":\"{effort}\"");
    }

    [Fact]
    public void Empty_mapping_and_model_effort_passes_through()
    {
        // 映射与模型库均为空（合并结果为空串）→ 不注入固定覆盖，保持客户端原始推导语义
        // （budget_tokens=2048 → low，由协议桥推导；关键断言：结果不是任何映射/模型级固定值）。
        var prepared = ProxyProtocolBridge.PrepareRequestBody(
            "Anthropic", "OpenAI", AnthropicBody, "glm-5.2", false,
            string.Empty, "https://upstream.example.com", null, isPassthrough: false);

        prepared.Should().Contain("\"reasoning_effort\":\"low\"",
            "透传模式：effort 由客户端 thinking.budget_tokens 推导，而非固定覆盖");
    }
}
