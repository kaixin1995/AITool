using System.Text.Json.Nodes;
using AITool.Protocol;
using FluentAssertions;

namespace AITool.ApplicationTests.Proxy;

/// <summary>
/// 复现 2026-08-30 生产崩溃：工具参数 schema 中 $ref 为非标量（嵌套对象/数字）时，
/// CleanJsonSchemaForGemini 的 GetValue&lt;string&gt;() 抛 InvalidOperationException（node must be of type JsonValue）。
/// 修复后应防御性跳过，不崩溃。
/// </summary>
public sealed class GeminiSchemaCrashTests
{
    private static string BuildOpenAiBodyWithTools(string toolsJson)
        => $$"""
        {
            "model": "auto",
            "messages": [{"role": "user", "content": "hello"}],
            "tools": {{toolsJson}}
        }
        """;

    [Fact]
    public void PrepareRequestBody_gemini_with_non_scalar_ref_does_not_crash()
    {
        // $ref 为嵌套对象（非字符串）——触发 InvalidOperationException 的场景
        var body = BuildOpenAiBodyWithTools("""
            [{
                "type": "function",
                "function": {
                    "name": "get_weather",
                    "description": "Get weather",
                    "parameters": {
                        "type": "object",
                        "properties": {
                            "city": {
                                "$ref": {"type": "string", "description": "City name"}
                            }
                        }
                    }
                }
            }]
            """);

        var result = ProxyProtocolBridge.PrepareRequestBody(
            "OpenAI", "Gemini", body, "gemini-3-pro", false,
            null, "https://daily-cloudcode-pa.googleapis.com", null,
            isPassthrough: false, isCompact: false, geminiProjectId: "test-project");

        result.Should().NotBeNullOrEmpty("非标量 $ref 应被防御性跳过而非崩溃");
        result.Should().Contain("contents", "Gemini 封套应正常构建");
    }

    [Fact]
    public void PrepareRequestBody_gemini_with_numeric_description_does_not_crash()
    {
        // description 为数字（非字符串）
        var body = BuildOpenAiBodyWithTools("""
            [{
                "type": "function",
                "function": {
                    "name": "calc",
                    "description": 42,
                    "parameters": {
                        "type": "object",
                        "properties": {
                            "expr": {"type": "string", "description": 123, "minLength": 1}
                        }
                    }
                }
            }]
            """);

        var result = ProxyProtocolBridge.PrepareRequestBody(
            "OpenAI", "Gemini", body, "gemini-3-pro", false,
            null, "https://daily-cloudcode-pa.googleapis.com", null,
            isPassthrough: false, isCompact: false, geminiProjectId: "test-project");

        result.Should().NotBeNullOrEmpty("数字 description 应被防御性处理而非崩溃");
    }

    [Fact]
    public void PrepareRequestBody_gemini_with_non_string_required_items_does_not_crash()
    {
        // required 数组含非字符串项
        var body = BuildOpenAiBodyWithTools("""
            [{
                "type": "function",
                "function": {
                    "name": "submit",
                    "parameters": {
                        "type": "object",
                        "properties": {
                            "name": {"type": "string"}
                        },
                        "required": ["name", 42, {"nested": true}]
                    }
                }
            }]
            """);

        var result = ProxyProtocolBridge.PrepareRequestBody(
            "OpenAI", "Gemini", body, "gemini-3-pro", false,
            null, "https://daily-cloudcode-pa.googleapis.com", null,
            isPassthrough: false, isCompact: false, geminiProjectId: "test-project");

        result.Should().NotBeNullOrEmpty("非字符串 required 项应被剔除而非崩溃");
    }

    [Fact]
    public void PrepareRequestBody_gemini_with_non_string_tool_type_does_not_crash()
    {
        // tool.type 为数字（非字符串）
        var body = BuildOpenAiBodyWithTools("""
            [{
                "type": 123,
                "function": {
                    "name": "weird_tool",
                    "parameters": {"type": "object", "properties": {}}
                }
            }]
            """);

        var result = ProxyProtocolBridge.PrepareRequestBody(
            "OpenAI", "Gemini", body, "gemini-3-pro", false,
            null, "https://daily-cloudcode-pa.googleapis.com", null,
            isPassthrough: false, isCompact: false, geminiProjectId: null);

        result.Should().NotBeNullOrEmpty("非字符串 tool.type 应被跳过该工具而非崩溃");
    }

    [Fact]
    public void PrepareRequestBody_gemini_with_normal_tools_still_works()
    {
        // 正常工具不受影响（回归保障）
        var body = BuildOpenAiBodyWithTools("""
            [{
                "type": "function",
                "function": {
                    "name": "get_weather",
                    "description": "Get current weather",
                    "parameters": {
                        "type": "object",
                        "properties": {
                            "city": {"type": "string", "description": "City name"},
                            "unit": {"type": "string", "enum": ["c", "f"]}
                        },
                        "required": ["city"]
                    }
                }
            }]
            """);

        var result = ProxyProtocolBridge.PrepareRequestBody(
            "OpenAI", "Gemini", body, "gemini-3-pro", false,
            null, "https://daily-cloudcode-pa.googleapis.com", null,
            isPassthrough: false, isCompact: false, geminiProjectId: "proj");

        result.Should().Contain("get_weather");
        result.Should().Contain("functionDeclarations");
        result.Should().Contain("proj");
    }
}
