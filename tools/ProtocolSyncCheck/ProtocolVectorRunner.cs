using System.Text.Json.Nodes;
using AITool.Protocol;

namespace ProtocolSyncCheck;

/// <summary>
/// 在 AITool.Protocol 的真实转换代码上执行从 cc-switch 测试反向提取的协议向量：
/// cc-switch 的测试怎么转，AITool 的桥就怎么跑，逐断言路径比对，
/// 任何分歧都精确到「测试名 + 路径 + 期望值 + 实际值」。
/// </summary>
internal static class ProtocolVectorRunner
{
    /// <summary>
    /// 执行全部向量并返回逐条结果。
    /// </summary>
    public static List<VectorRunResult> RunAll(IEnumerable<RustTestVector> vectors)
    {
        var results = new List<VectorRunResult>();
        foreach (var vector in vectors)
        {
            results.Add(RunOne(vector));
        }

        return results;
    }

    public static VectorRunResult RunOne(RustTestVector vector)
    {
        var result = new VectorRunResult(vector);
        JsonNode? input;
        try
        {
            input = JsonNode.Parse(vector.InputJson);
        }
        catch (Exception ex)
        {
            result.MarkSkipped("输入 JSON 解析失败：" + ex.Message);
            return result;
        }

        var inputObject = input as JsonObject;
        var modelName = inputObject?["model"]?.GetValue<string>() ?? "test-model";
        string outputJson;
        try
        {
            outputJson = Convert(vector.Direction, vector.InputJson, modelName);
        }
        catch (Exception ex)
        {
            result.MarkFailed("转换抛出异常：" + ex.GetType().Name + "：" + ex.Message);
            return result;
        }

        JsonNode? output;
        try
        {
            output = string.IsNullOrWhiteSpace(outputJson) ? null : JsonNode.Parse(outputJson);
        }
        catch (Exception ex)
        {
            result.MarkFailed("输出 JSON 解析失败：" + ex.Message);
            return result;
        }

        if (output is null)
        {
            result.MarkFailed("转换返回空结果");
            return result;
        }

        foreach (var assertion in vector.Assertions)
        {
            var actual = output.Navigate(assertion.Path);
            JsonNode? expected = assertion.Kind == VectorAssertionKind.MirrorInput
                ? input.Navigate(assertion.MirrorInputPath!)
                : assertion.Expected;

            switch (assertion.Kind)
            {
                case VectorAssertionKind.TypeIs:
                    EvaluateTypeIs(result, assertion, actual);
                    break;
                case VectorAssertionKind.Contains:
                    EvaluateContains(result, assertion, actual);
                    break;
                default:
                    EvaluateEquals(result, assertion, expected, actual);
                    break;
            }
        }

        result.Complete();
        return result;
    }

    /// <summary>
    /// 方向 → AITool 协议桥入口。全部使用公开 API，与生产链路同一条代码路径。
    /// </summary>
    private static string Convert(string direction, string inputJson, string modelName) => direction switch
    {
        "Anthropic→OpenAI:Request" =>
            ProxyProtocolBridge.PrepareRequestBody("Anthropic", "OpenAI", inputJson, modelName, enableStreaming: false),
        "Anthropic→Responses:Request" =>
            ProxyProtocolBridge.PrepareRequestBody("Anthropic", "Responses", inputJson, modelName, enableStreaming: false),
        "Responses→Anthropic:Request" =>
            ProxyProtocolBridge.PrepareRequestBody("Responses", "Anthropic", inputJson, modelName, enableStreaming: false),
        "Responses→OpenAI:Request" =>
            ProxyProtocolBridge.PrepareRequestBody("Responses", "OpenAI", inputJson, modelName, enableStreaming: false),
        "OpenAI→Anthropic:Response" =>
            ProxyProtocolBridge.AdaptResponseBodyForClient("Anthropic", "OpenAI", inputJson, isStreaming: false, modelName, 0, 0, 0),
        "Responses→Anthropic:Response" =>
            ProxyProtocolBridge.AdaptResponseBodyForClient("Anthropic", "Responses", inputJson, isStreaming: false, modelName, 0, 0, 0),
        "Anthropic→Responses:Response" =>
            ProxyProtocolBridge.ConvertAnthropicResponseToResponses(inputJson),
        "OpenAI→Responses:Response" =>
            ProxyProtocolBridge.ConvertChatResponseToResponses(inputJson),
        _ => throw new NotSupportedException("未映射的转换方向：" + direction)
    };

    private static void EvaluateEquals(VectorRunResult result, VectorAssertion assertion, JsonNode? expected, JsonNode? actual)
    {
        var displayPath = assertion.Path.ToDisplayPath();

        // 容差规则：两侧网关都会自行生成标识/时间戳，这类字段只要求"存在且非空"，
        // 不做逐字符比对（cc-switch 的断言值是其实现生成的，AITool 生成的必然不同）。
        if (IsGeneratedIdentityPath(assertion.Path))
        {
            if (actual is null || IsEmptyValue(actual))
            {
                result.AddFailure(displayPath, DescribeNode(expected), DescribeNode(actual), "生成型标识字段缺失或为空");
            }

            return;
        }

        if (expected is null)
        {
            return;
        }

        if (!SemanticEquals(expected, actual))
        {
            result.AddFailure(displayPath, DescribeNode(expected), DescribeNode(actual), "值不一致");
        }
    }

    private static void EvaluateTypeIs(VectorRunResult result, VectorAssertion assertion, JsonNode? actual)
    {
        var displayPath = assertion.Path.ToDisplayPath();
        var kind = assertion.TypeKind;
        var matches = kind switch
        {
            "string" => actual is JsonValue { } v && v.TryGetValue<string>(out _),
            "number" => actual is JsonValue { } n && (n.TryGetValue<int>(out _) || n.TryGetValue<long>(out _) || n.TryGetValue<double>(out _)),
            "boolean" => actual is JsonValue { } b && b.TryGetValue<bool>(out _),
            "array" => actual is JsonArray,
            "object" => actual is JsonObject,
            "null" => actual is null,
            _ => true
        };
        if (!matches)
        {
            result.AddFailure(displayPath, "类型 " + kind, DescribeNode(actual), "类型不符");
        }
    }

    private static void EvaluateContains(VectorRunResult result, VectorAssertion assertion, JsonNode? actual)
    {
        var displayPath = assertion.Path.ToDisplayPath();
        var actualText = actual?.GetValue<string>() ?? actual?.ToJsonString() ?? string.Empty;
        if (!actualText.Contains(assertion.Fragment ?? string.Empty, StringComparison.Ordinal))
        {
            result.AddFailure(displayPath, "包含 «" + assertion.Fragment + "»", DescribeNode(actual), "内容不包含期望片段");
        }
    }

    /// <summary>
    /// 生成型字段：路径末段是 id 类键，或时间戳键。
    /// </summary>
    private static bool IsGeneratedIdentityPath(IReadOnlyList<PathSegment> path)
    {
        if (path.Count == 0 || path[^1] is not { IsKey: true } last)
        {
            return false;
        }

        var key = last.Key!;
        return key.Equals("id", StringComparison.OrdinalIgnoreCase)
            || key.EndsWith("_id", StringComparison.OrdinalIgnoreCase)
            || key.Equals("created_at", StringComparison.OrdinalIgnoreCase)
            || key.Equals("created", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEmptyValue(JsonNode node) =>
        node is null
        || (node is JsonValue value && value.TryGetValue<string>(out var text) && string.IsNullOrEmpty(text));

    /// <summary>
    /// 语义相等：数字按数值比、字符串精确比、对象/数组递归比。
    /// </summary>
    private static bool SemanticEquals(JsonNode expected, JsonNode? actual)
    {
        if (actual is null)
        {
            return false;
        }

        if (expected is JsonValue expectedValue && actual is JsonValue actualValue)
        {
            if (expectedValue.TryGetValue<string>(out var expectedText)
                && actualValue.TryGetValue<string>(out var actualText))
            {
                return string.Equals(expectedText, actualText, StringComparison.Ordinal);
            }

            if (TryGetNumber(expectedValue, out var expectedNumber) && TryGetNumber(actualValue, out var actualNumber))
            {
                return Math.Abs(expectedNumber - actualNumber) < 1e-9;
            }

            if (expectedValue.TryGetValue<bool>(out var expectedBool) && actualValue.TryGetValue<bool>(out var actualBool))
            {
                return expectedBool == actualBool;
            }

            return string.Equals(expected.ToJsonString(), actual.ToJsonString(), StringComparison.Ordinal);
        }

        if (expected is JsonObject expectedObject)
        {
            if (actual is not JsonObject actualObject)
            {
                return false;
            }

            foreach (var (key, value) in expectedObject)
            {
                if (!SemanticEquals(value!, actualObject[key]))
                {
                    return false;
                }
            }

            return true;
        }

        if (expected is JsonArray expectedArray)
        {
            if (actual is not JsonArray actualArray || actualArray.Count != expectedArray.Count)
            {
                return false;
            }

            for (var i = 0; i < expectedArray.Count; i++)
            {
                if (!SemanticEquals(expectedArray[i]!, actualArray[i]))
                {
                    return false;
                }
            }

            return true;
        }

        return string.Equals(expected.ToJsonString(), actual.ToJsonString(), StringComparison.Ordinal);
    }

    private static bool TryGetNumber(JsonValue value, out double number)
    {
        if (value.TryGetValue<int>(out var intValue))
        {
            number = intValue;
            return true;
        }

        if (value.TryGetValue<long>(out var longValue))
        {
            number = longValue;
            return true;
        }

        if (value.TryGetValue<double>(out var doubleValue))
        {
            number = doubleValue;
            return true;
        }

        number = 0;
        return false;
    }

    private static string DescribeNode(JsonNode? node) => node switch
    {
        null => "（不存在）",
        JsonValue value when value.TryGetValue<string>(out var text) => "\"" + (text.Length > 80 ? text[..80] + "…" : text) + "\"",
        _ => node.ToJsonString() is { Length: > 100 } json ? json[..100] + "…" : node.ToJsonString()
    };
}

/// <summary>
/// 单个向量的执行结果。
/// </summary>
internal sealed class VectorRunResult(RustTestVector vector)
{
    public RustTestVector Vector { get; } = vector;
    public VectorRunStatus Status { get; private set; } = VectorRunStatus.Passed;
    public string? SkipReason { get; private set; }
    public List<VectorAssertionFailure> Failures { get; } = [];

    public void AddFailure(string path, string expected, string actual, string reason)
    {
        Failures.Add(new VectorAssertionFailure(path, expected, actual, reason));
        Status = VectorRunStatus.Failed;
    }

    public void MarkFailed(string reason)
    {
        Failures.Add(new VectorAssertionFailure("（整体）", "—", "—", reason));
        Status = VectorRunStatus.Failed;
    }

    public void MarkSkipped(string reason)
    {
        SkipReason = reason;
        Status = VectorRunStatus.Skipped;
    }

    public void Complete()
    {
        // 全部断言通过则保持 Passed。
    }
}

internal enum VectorRunStatus
{
    Passed,
    Failed,
    Skipped
}

/// <summary>
/// 单条断言失败明细。
/// </summary>
internal sealed record VectorAssertionFailure(string Path, string Expected, string Actual, string Reason);
