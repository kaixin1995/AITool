using System.Text.Json.Nodes;
using AITool.Domain.Proxy;
using AITool.Protocol;
using AITool.Web.Services;
using FluentAssertions;

namespace AITool.IntegrationTests.Proxy;

/// <summary>
/// Anthropic ↔ OpenAI 协议转换的核心断言。
/// 通过 public 的 PrepareRequestBody 端到端验证，最贴近真实转发链路。
/// 重点覆盖 claude-code 新版（thinking.type=adaptive + output_config.effort）与引发 z.ai 1210 的几个字段。
/// </summary>
public sealed class ProxyProtocolBridgeThinkingTests
{
    /// <summary>
    /// 构造一个最小可转发的 Anthropic 请求体。
    /// </summary>
    private static string BuildAnthropicRequestBody(Action<JsonObject>? mutate = null)
    {
        var root = new JsonObject
        {
            ["model"] = "auto",
            ["max_tokens"] = 1024,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "1+1=?"
                }
            }
        };
        mutate?.Invoke(root);
        return root.ToJsonString();
    }

    /// <summary>
    /// 调用 PrepareRequestBody 把 Anthropic 请求体转换为 OpenAI 格式。
    /// </summary>
    private static JsonObject ConvertToOpenAi(string anthropicRequestBody)
    {
        var prepared = ProxyProtocolBridge.PrepareRequestBody(
            "Anthropic",
            "OpenAI",
            anthropicRequestBody,
            "glm-5.2",
            enableStreaming: false);

        return JsonNode.Parse(prepared) as JsonObject
            ?? throw new InvalidOperationException("转换结果不是合法 JSON 对象");
    }

    // ---- thinking / output_config → reasoning_effort ----

    /// <summary>
    /// claude-code 新版标准格式：adaptive + output_config.effort=xhigh，effort 原样透传。
    /// （z.ai/GLM 等端点支持 max/xhigh 档位，经实测确认，不做收敛。）
    /// </summary>
    [Fact]
    public void Anthropic_adaptive_with_output_config_effort_xhigh_passes_through()
    {
        var body = BuildAnthropicRequestBody(root =>
        {
            root["thinking"] = new JsonObject { ["type"] = "adaptive" };
            root["output_config"] = new JsonObject { ["effort"] = "xhigh" };
        });

        var openAi = ConvertToOpenAi(body);

        openAi["reasoning_effort"]?.GetValue<string>().Should().Be("xhigh");
    }

    /// <summary>
    /// output_config.effort=max 原样透传。
    /// </summary>
    [Fact]
    public void Anthropic_output_config_effort_max_passes_through()
    {
        var body = BuildAnthropicRequestBody(root =>
        {
            root["thinking"] = new JsonObject { ["type"] = "adaptive" };
            root["output_config"] = new JsonObject { ["effort"] = "max" };
        });

        var openAi = ConvertToOpenAi(body);

        openAi["reasoning_effort"]?.GetValue<string>().Should().Be("max");
    }

    /// <summary>
    /// adaptive 但未带 output_config 时，降级为 high（自适应默认倾向较强思考）。
    /// </summary>
    [Fact]
    public void Anthropic_adaptive_without_output_config_defaults_to_high()
    {
        var body = BuildAnthropicRequestBody(root =>
        {
            root["thinking"] = new JsonObject { ["type"] = "adaptive" };
        });

        var openAi = ConvertToOpenAi(body);

        openAi["reasoning_effort"]?.GetValue<string>().Should().Be("high");
    }

    /// <summary>
    /// 老式 enabled + budget_tokens=5000 应映射为 high，保证向后兼容不回归。
    /// </summary>
    [Fact]
    public void Anthropic_enabled_with_large_budget_tokens_maps_to_high()
    {
        var body = BuildAnthropicRequestBody(root =>
        {
            root["thinking"] = new JsonObject
            {
                ["type"] = "enabled",
                ["budget_tokens"] = 5000
            };
        });

        var openAi = ConvertToOpenAi(body);

        openAi["reasoning_effort"]?.GetValue<string>().Should().Be("high");
    }

    /// <summary>
    /// 老式 enabled + budget_tokens=1280 应映射为 low。
    /// </summary>
    [Fact]
    public void Anthropic_enabled_with_small_budget_tokens_maps_to_low()
    {
        var body = BuildAnthropicRequestBody(root =>
        {
            root["thinking"] = new JsonObject
            {
                ["type"] = "enabled",
                ["budget_tokens"] = 1280
            };
        });

        var openAi = ConvertToOpenAi(body);

        openAi["reasoning_effort"]?.GetValue<string>().Should().Be("low");
    }

    /// <summary>
    /// 显式 disabled 时不输出 reasoning_effort。
    /// </summary>
    [Fact]
    public void Anthropic_disabled_thinking_omits_reasoning_effort()
    {
        var body = BuildAnthropicRequestBody(root =>
        {
            root["thinking"] = new JsonObject { ["type"] = "disabled" };
        });

        var openAi = ConvertToOpenAi(body);

        openAi.ContainsKey("reasoning_effort").Should().BeFalse();
    }

    /// <summary>
    /// 仅 output_config.effort=low（无 thinking 对象）也应正确映射。
    /// </summary>
    [Fact]
    public void Anthropic_output_config_only_without_thinking_maps_effort()
    {
        var body = BuildAnthropicRequestBody(root =>
        {
            root["output_config"] = new JsonObject { ["effort"] = "low" };
        });

        var openAi = ConvertToOpenAi(body);

        openAi["reasoning_effort"]?.GetValue<string>().Should().Be("low");
    }

    /// <summary>
    /// 完全不带任何思考参数时，不应输出 reasoning_effort。
    /// </summary>
    [Fact]
    public void Anthropic_without_any_thinking_config_omits_reasoning_effort()
    {
        var body = BuildAnthropicRequestBody();

        var openAi = ConvertToOpenAi(body);

        openAi.ContainsKey("reasoning_effort").Should().BeFalse();
    }

    // ---- z.ai 1210 根因回归：metadata 与 stream_options 不得出现在转换结果里 ----

    /// <summary>
    /// z.ai 等 OpenAI 兼容端点的 chat completions 官方字段清单不含 metadata，
    /// 收到会返回 1210。转换后请求体不得携带 metadata（即使用户原始请求体里带了她）。
    /// 这是本次故障的真正根因。
    /// </summary>
    [Fact]
    public void Anthropic_metadata_is_not_forwarded_to_openai_payload()
    {
        var body = BuildAnthropicRequestBody(root =>
        {
            root["metadata"] = new JsonObject
            {
                ["user_id"] = "{\"device_id\":\"abc\",\"session_id\":\"xyz\"}"
            };
        });

        var openAi = ConvertToOpenAi(body);

        openAi.ContainsKey("metadata").Should().BeFalse();
    }

    /// <summary>
    /// 转换后请求体不得主动添加 stream_options（z.ai 等端点不支持，且流式响应自带 usage，统计不受影响）。
    /// </summary>
    [Fact]
    public void Anthropic_to_openai_never_adds_stream_options()
    {
        var body = BuildAnthropicRequestBody();

        var prepared = ProxyProtocolBridge.PrepareRequestBody(
            "Anthropic", "OpenAI", body, "glm-5.2", enableStreaming: true);
        var openAi = JsonNode.Parse(prepared) as JsonObject
            ?? throw new InvalidOperationException("转换结果不是合法 JSON 对象");

        openAi.ContainsKey("stream_options").Should().BeFalse();
    }

    // ---- messages 结构：claude-code 把 system 塞进 messages 数组时必须合并到开头 ----

    /// <summary>
    /// claude-code 新版会把额外的 system 内容塞进 messages 数组（role=system）。
    /// 转换时必须把这些 system 条目合并到开头的 system message，不能原样追加，
    /// 否则会破坏 OpenAI 规范（system 只能在最前、最后一条必须是 user），部分严格端点会拒绝。
    /// </summary>
    [Fact]
    public void Anthropic_system_messages_in_array_are_merged_to_head()
    {
        var body = BuildAnthropicRequestBody(root =>
        {
            root["system"] = "你是助手";
            root["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "第一问" },
                new JsonObject { ["role"] = "assistant", ["content"] = "第一答" },
                new JsonObject { ["role"] = "user", ["content"] = "第二问" },
                // 中间穿插的 system（claude-code 新版常见）
                new JsonObject { ["role"] = "system", ["content"] = "补充指令" }
            };
        });

        var openAi = ConvertToOpenAi(body);

        var roles = openAi["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()).ToArray();
        // 合并后：system 在最前，后续 user/assistant/user 交替，最后一条必须是 user
        roles[0].Should().Be("system");
        roles.Count(r => r == "system").Should().Be(1, "中间穿插的 system 应被合并到开头");
        roles[^1].Should().Be("user");
        // 合并后的 system 内容应包含 messages 数组里那条额外 system 的明文内容
        // （顶层 system 经 ExtractSystemContent 可能被 JSON 转义，故只断言数组来源的部分）
        var systemContent = openAi["messages"]![0]!["content"]!.GetValue<string>();
        systemContent.Should().Contain("补充指令");
    }

    // ---- 反向转换回归：OpenAI → Anthropic 不受本次改动影响 ----

    /// <summary>
    /// 构造一个最小可转发的 OpenAI 请求体。
    /// </summary>
    private static string BuildOpenAiRequestBody(Action<JsonObject>? mutate = null)
    {
        var root = new JsonObject
        {
            ["model"] = "auto",
            ["max_tokens"] = 1024,
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "1+1=?"
                }
            }
        };
        mutate?.Invoke(root);
        return root.ToJsonString();
    }

    /// <summary>
    /// OpenAI reasoning_effort=high 应转换为 Anthropic thinking.enabled + budget_tokens=4096。
    /// 确认反向转换的 reasoning_effort → thinking 映射未被本次改动破坏。
    /// </summary>
    [Fact]
    public void OpenAi_reasoning_effort_high_maps_to_anthropic_thinking_budget_4096()
    {
        var body = BuildOpenAiRequestBody(root =>
        {
            root["reasoning_effort"] = "high";
        });

        var prepared = ProxyProtocolBridge.PrepareRequestBody(
            "OpenAI", "Anthropic", body, "glm-5.2", enableStreaming: false);
        var anthropic = JsonNode.Parse(prepared) as JsonObject
            ?? throw new InvalidOperationException("转换结果不是合法 JSON 对象");

        var thinking = anthropic["thinking"]?.AsObject();
        thinking.Should().NotBeNull();
        thinking!["type"]?.GetValue<string>().Should().Be("enabled");
        thinking["budget_tokens"]?.GetValue<int>().Should().Be(4096);
    }

    // ---- 思考模式覆盖（OverrideReasoningEffort）原样透传 ----

    /// <summary>
    /// 路由级"思考模式覆盖"配成 max 时，对 OpenAI 目标协议应原样把 reasoning_effort 设为 max。
    /// （覆盖值不收敛，max/xhigh 等档位 GLM 支持。）
    /// </summary>
    [Fact]
    public void Override_effort_max_to_openai_target_passes_through()
    {
        var body = BuildAnthropicRequestBody(root =>
        {
            root["thinking"] = new JsonObject { ["type"] = "adaptive" };
            root["output_config"] = new JsonObject { ["effort"] = "xhigh" };
        });

        var prepared = ProxyProtocolBridge.PrepareRequestBody(
            "Anthropic", "OpenAI", body, "glm-5.2", enableStreaming: true,
            overrideReasoningEffort: "max");
        var openAi = JsonNode.Parse(prepared) as JsonObject
            ?? throw new InvalidOperationException("转换结果不是合法 JSON 对象");

        openAi["reasoning_effort"]?.GetValue<string>().Should().Be("max");
    }

    /// <summary>
    /// 覆盖=max 但目标协议为 Anthropic 时，应映射为 thinking.budget_tokens=16384（max→16384）。
    /// 验证覆盖逻辑对 Anthropic 目标的映射未被破坏。
    /// </summary>
    [Fact]
    public void Override_effort_max_to_anthropic_target_maps_to_budget_tokens()
    {
        var body = BuildOpenAiRequestBody(root =>
        {
            root["reasoning_effort"] = "low";
        });

        var prepared = ProxyProtocolBridge.PrepareRequestBody(
            "OpenAI", "Anthropic", body, "glm-5.2", enableStreaming: false,
            overrideReasoningEffort: "max");
        var anthropic = JsonNode.Parse(prepared) as JsonObject
            ?? throw new InvalidOperationException("转换结果不是合法 JSON 对象");

        var thinking = anthropic["thinking"]?.AsObject();
        thinking.Should().NotBeNull();
        thinking!["type"]?.GetValue<string>().Should().Be("enabled");
        thinking["budget_tokens"]?.GetValue<int>().Should().Be(16384);
    }

    // ---- keep_reasoning 规则：deepseek 等上游要求工具调用时回传 reasoning_content ----

    /// <summary>
    /// 构造一个带 thinking block 的多轮 Anthropic 请求体（assistant 上一轮含 thinking + tool_use）。
    /// </summary>
    private static string BuildAnthropicRequestBodyWithThinkingAssistant()
    {
        return new JsonObject
        {
            ["model"] = "auto",
            ["max_tokens"] = 1024,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = "查天气" },
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonArray
                    {
                        // deepseek 要求：工具调用时 reasoning_content 必须回传
                        new JsonObject { ["type"] = "thinking", ["thinking"] = "需要先获取日期再查天气" },
                        new JsonObject { ["type"] = "text", ["text"] = "我来查一下天气" },
                        new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = "toolu_1",
                            ["name"] = "get_date",
                            ["input"] = new JsonObject()
                        }
                    }
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "toolu_1", ["content"] = "2026-08-13" }
                    }
                }
            }
        }.ToJsonString();
    }

    /// <summary>
    /// 未绑定 keep_reasoning 规则时，thinking block 被丢弃，assistant 不含 reasoning_content。
    /// 标准 OpenAI 不认 reasoning_content，这是默认行为。
    /// </summary>
    [Fact]
    public void Anthropic_assistant_thinking_dropped_without_keep_reasoning_rule()
    {
        var body = BuildAnthropicRequestBodyWithThinkingAssistant();

        var openAi = ConvertToOpenAi(body);

        var messages = openAi["messages"]!.AsArray();
        // assistant 消息是第二条（index=1）
        var assistant = messages[1]!.AsObject();
        assistant["reasoning_content"]?.Should().BeNull("未绑定 keep_reasoning 规则时应丢弃 thinking");
        assistant["content"]?.GetValue<string>().Should().Be("我来查一下天气");
        assistant["tool_calls"]?.Should().NotBeNull("tool_use 仍应保留");
    }

    /// <summary>
    /// 绑定 keep_reasoning 规则时，assistant 的 thinking block 转成 reasoning_content 保留。
    /// deepseek 上游要求工具调用时 reasoning_content 必须回传。
    /// </summary>
    [Fact]
    public void Anthropic_assistant_thinking_kept_as_reasoning_content_with_rule()
    {
        var body = BuildAnthropicRequestBodyWithThinkingAssistant();

        var prepared = ProxyProtocolBridge.PrepareRequestBody(
            "Anthropic", "OpenAI", body, "deepseek-v4-flash", enableStreaming: false,
            compatibilityRules: new[] { new CompatibilityRule { Op = "keep_reasoning" } });

        var openAi = JsonNode.Parse(prepared) as JsonObject
            ?? throw new InvalidOperationException("转换结果不是合法 JSON 对象");

        var messages = openAi["messages"]!.AsArray();
        var assistant = messages[1]!.AsObject();
        // 核心断言：thinking 映射成 reasoning_content 保留
        var reasoningContent = assistant["reasoning_content"]?.GetValue<string>();
        reasoningContent.Should().Be("需要先获取日期再查天气");
        assistant["content"]?.GetValue<string>().Should().Be("我来查一下天气");
        assistant["tool_calls"]?.Should().NotBeNull("tool_use 仍应保留");
    }
}
