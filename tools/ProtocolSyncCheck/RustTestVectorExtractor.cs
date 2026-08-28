using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProtocolSyncCheck;

/// <summary>
/// 从 cc-switch 的 Rust 测试函数中反向提取协议转换测试向量：
/// 每个向量 =（测试名、来源位置、转换方向、输入 JSON、一组路径断言）。
/// 提取后交给 <see cref="ProtocolVectorRunner"/> 在 AITool.Protocol 上真实执行——
/// cc-switch 的测试断言什么路径/值，AITool 的转换就必须产出什么，逐路径比对实现"彻底定位"。
/// </summary>
internal static class RustTestVectorExtractor
{
    /// <summary>
    /// cc-switch 转换函数 → AITool 协议桥方向的映射（请求/响应四个非流式方向）。
    /// </summary>
    private static readonly Dictionary<string, string> DirectionByFunction = new(StringComparer.Ordinal)
    {
        // transform.rs：Anthropic 客户端 → OpenAI Chat 上游
        ["anthropic_to_openai"] = "Anthropic→OpenAI:Request",
        ["anthropic_to_openai_with_reasoning_content"] = "Anthropic→OpenAI:Request",
        // transform.rs：Chat 响应 → Anthropic 响应
        ["openai_to_anthropic"] = "OpenAI→Anthropic:Response",
        // transform_responses.rs：Anthropic 客户端 → Responses 上游（请求）
        ["anthropic_to_responses"] = "Anthropic→Responses:Request",
        // transform_responses.rs：Responses 响应 → Anthropic 响应
        ["responses_to_anthropic"] = "Responses→Anthropic:Response",
        ["responses_to_anthropic_with_web_search_name"] = "Responses→Anthropic:Response",
        ["responses_to_anthropic_with_web_search_options"] = "Responses→Anthropic:Response",
        // transform_codex_anthropic.rs：Responses 客户端 → Anthropic 上游（请求）
        ["responses_request_to_anthropic"] = "Responses→Anthropic:Request",
        // transform_codex_anthropic.rs：Anthropic 响应 → Responses 响应
        ["anthropic_response_to_responses"] = "Anthropic→Responses:Response",
        ["anthropic_response_to_responses_with_context"] = "Anthropic→Responses:Response",
        // transform_codex_chat.rs：Responses 客户端 → Chat 上游（请求）
        ["responses_to_chat_completions"] = "Responses→OpenAI:Request",
        ["responses_to_chat_completions_with_reasoning"] = "Responses→OpenAI:Request",
        // transform_codex_chat.rs：Chat 响应 → Responses 响应
        ["chat_completion_to_response"] = "OpenAI→Responses:Response",
        ["chat_completion_to_response_with_context"] = "OpenAI→Responses:Response"
    };

    private static readonly Regex TestFnRegex = new(
        @"#\[test\]\s*(?:fn\s+(?<name>\w+)\s*\(\s*\)\s*\{)",
        RegexOptions.Compiled);

    private static readonly Regex JsonMacroRegex = new(
        @"(?:let\s+(?<var>\w+)\s*=\s*)?json!\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex TransformCallRegex = new(
        @"(?:let\s+(?<recv>\w+)\s*=\s*)?(?<fn>anthropic_to_openai_with_reasoning_content|anthropic_to_openai|openai_to_anthropic|anthropic_to_responses|responses_to_anthropic_with_web_search_options|responses_to_anthropic_with_web_search_name|responses_to_anthropic|responses_request_to_anthropic|anthropic_response_to_responses_with_context|anthropic_response_to_responses|responses_to_chat_completions_with_reasoning|responses_to_chat_completions|chat_completion_to_response_with_context|chat_completion_to_response)\s*\(\s*(?<arg>\w+)(?:\s*\.\s*clone\(\))?",
        RegexOptions.Compiled);

    /// <summary>assert_eq!(recv["a"][0]["b"], 期望)；期望为字面量或 json!(...)。</summary>
    private static readonly Regex AssertEqRegex = new(
        @"assert_eq!\(\s*(?<recv>\w+)(?<path>(?:\[[^\]]+\])*)\s*,\s*(?<expected>json!\s*\(|""(?:[^""\\]|\\.)*""|-?\d+(?:\.\d+)?|true|false)",
        RegexOptions.Compiled);

    /// <summary>assert!(recv["a"] == 期望)。</summary>
    private static readonly Regex AssertBoolEqRegex = new(
        @"assert!\(\s*(?<recv>\w+)(?<path>(?:\[[^\]]+\])*)\s*==\s*(?<expected>json!\s*\(|""(?:[^""\\]|\\.)*""|-?\d+(?:\.\d+)?|true|false)",
        RegexOptions.Compiled);

    /// <summary>assert_eq!(recv["a"], input["b"])：期望引用输入变量路径。</summary>
    private static readonly Regex AssertEqVarPathRegex = new(
        @"assert_eq!\(\s*(?<recv>\w+)(?<path>(?:\[[^\]]+\])*)\s*,\s*(?<var>\w+)(?<varPath>(?:\[[^\]]+\])*)\s*\)",
        RegexOptions.Compiled);

    /// <summary>assert!(recv["a"].is_string() / is_array() ...)：类型断言。</summary>
    private static readonly Regex AssertTypeRegex = new(
        @"assert!\(\s*(?<recv>\w+)(?<path>(?:\[[^\]]+\])*)\s*\.\s*is_(?<kind>string|array|object|number|boolean|null)\(\)",
        RegexOptions.Compiled);

    /// <summary>assert!(recv["a"].as_str().unwrap().contains("x"))：包含断言。</summary>
    private static readonly Regex AssertContainsRegex = new(
        @"assert!\(\s*(?<recv>\w+)(?<path>(?:\[[^\]]+\])*)[^\n]*?contains\(\s*""(?<fragment>[^""\\]*(?:\\.[^""\\]*)*)""",
        RegexOptions.Compiled);

    private static readonly Regex IndexPathSegmentRegex = new(
        @"\[\s*(?:""(?<key>[^""]+)""|(?<index>\d+))\s*\]",
        RegexOptions.Compiled);

    public static List<RustTestVector> ExtractFile(string filePath)
    {
        var vectors = new List<RustTestVector>();
        if (!File.Exists(filePath))
        {
            return vectors;
        }

        var content = File.ReadAllText(filePath);
        foreach (Match testMatch in TestFnRegex.Matches(content))
        {
            var testName = testMatch.Groups["name"].Value;
            var line = CountLines(content, testMatch.Index) + 1;
            var body = ExtractBraceBlock(content, testMatch.Index + testMatch.Length - 1);
            var vector = ExtractVector(Path.GetFileName(filePath), line, testName, body);
            if (vector is not null)
            {
                vectors.Add(vector);
            }
        }

        return vectors;
    }

    private static RustTestVector? ExtractVector(string sourceFile, int line, string testName, string body)
    {
        // 转换调用决定方向与结果变量；无已知函数调用的测试（纯辅助函数测试）跳过。
        var call = TransformCallRegex.Match(body);
        if (!call.Success || !DirectionByFunction.TryGetValue(call.Groups["fn"].Value, out var direction))
        {
            return null;
        }

        var inputVar = call.Groups["arg"].Value;
        var receiverVar = call.Groups["recv"].Success ? call.Groups["recv"].Value : "result";

        // 输入 JSON：let <inputVar> = json!(...) 的宏体。
        var inputJson = FindAssignedJsonMacro(body, inputVar);
        if (inputJson is null)
        {
            return null;
        }

        var vector = new RustTestVector(sourceFile, line, testName, direction, inputJson);

        foreach (Match match in AssertEqRegex.Matches(body))
        {
            if (match.Groups["recv"].Value != receiverVar)
            {
                continue;
            }

            var path = ParsePath(match.Groups["path"].Value);
            var expected = ParseExpectedLiteral(body, match.Groups["expected"].Value, match.Index + match.Groups["expected"].Index);
            if (path.Count > 0 && expected is not null)
            {
                vector.Assertions.Add(new VectorAssertion(VectorAssertionKind.Equals, path, expected, null));
            }
        }

        foreach (Match match in AssertBoolEqRegex.Matches(body))
        {
            if (match.Groups["recv"].Value != receiverVar)
            {
                continue;
            }

            var path = ParsePath(match.Groups["path"].Value);
            var expected = ParseExpectedLiteral(body, match.Groups["expected"].Value, match.Index + match.Groups["expected"].Index);
            if (path.Count > 0 && expected is not null && !vector.HasAssertion(path))
            {
                vector.Assertions.Add(new VectorAssertion(VectorAssertionKind.Equals, path, expected, null));
            }
        }

        foreach (Match match in AssertEqVarPathRegex.Matches(body))
        {
            if (match.Groups["recv"].Value != receiverVar || match.Groups["var"].Value != inputVar)
            {
                continue;
            }

            var path = ParsePath(match.Groups["path"].Value);
            var inputPath = ParsePath(match.Groups["varPath"].Value);
            if (path.Count > 0 && inputPath.Count > 0 && !vector.HasAssertion(path))
            {
                vector.Assertions.Add(new VectorAssertion(VectorAssertionKind.MirrorInput, path, null, inputPath));
            }
        }

        foreach (Match match in AssertTypeRegex.Matches(body))
        {
            if (match.Groups["recv"].Value != receiverVar)
            {
                continue;
            }

            var path = ParsePath(match.Groups["path"].Value);
            if (path.Count > 0 && !vector.HasAssertion(path))
            {
                vector.Assertions.Add(new VectorAssertion(VectorAssertionKind.TypeIs, path, null, null)
                {
                    TypeKind = match.Groups["kind"].Value
                });
            }
        }

        foreach (Match match in AssertContainsRegex.Matches(body))
        {
            if (match.Groups["recv"].Value != receiverVar)
            {
                continue;
            }

            var path = ParsePath(match.Groups["path"].Value);
            if (path.Count > 0 && !vector.HasAssertion(path))
            {
                vector.Assertions.Add(new VectorAssertion(VectorAssertionKind.Contains, path, null, null)
                {
                    Fragment = UnescapeRustString(match.Groups["fragment"].Value)
                });
            }
        }

        return vector.Assertions.Count > 0 ? vector : null;
    }

    /// <summary>
    /// 找到 let var = json!(...) 的宏体并转为合法 JSON 文本；容忍尾随逗号。
    /// </summary>
    private static string? FindAssignedJsonMacro(string body, string varName)
    {
        foreach (Match match in JsonMacroRegex.Matches(body))
        {
            if (!match.Groups["var"].Success || match.Groups["var"].Value != varName)
            {
                continue;
            }

            var openParen = body.IndexOf('(', match.Index + match.Length - 1);
            if (openParen < 0)
            {
                return null;
            }

            var macroBody = ExtractBalanced(body, openParen, '(', ')');
            var cleaned = CleanRustJson(macroBody);
            return LooksLikeJson(cleaned) ? cleaned : null;
        }

        return null;
    }

    /// <summary>
    /// 解析期望值字面量：字符串 / 数字 / 布尔 / json! 宏体。
    /// literalStart 为组内绝对位置；越界（组未参与等）时安全返回 null。
    /// </summary>
    private static JsonNode? ParseExpectedLiteral(string body, string literal, int literalStart)
    {
        var trimmed = literal.Trim();
        if (literalStart < 0 || literalStart > body.Length)
        {
            return TryParseJson(trimmed);
        }

        if (trimmed.StartsWith("json!"))
        {
            var openParen = body.IndexOf('(', literalStart);
            if (openParen < 0)
            {
                return null;
            }

            var macroBody = ExtractBalanced(body, openParen, '(', ')');
            var cleaned = CleanRustJson(macroBody);
            return TryParseJson(cleaned);
        }

        if (trimmed.StartsWith('"'))
        {
            var match = Regex.Match(body.Substring(literalStart), @"""(?:[^""\\]|\\.)*""");
            if (!match.Success)
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(UnescapeRustString(match.Value));
            }
            catch
            {
                return null;
            }
        }

        return TryParseJson(trimmed);
    }

    /// <summary>
    /// Rust json! 宏体 → 合法 JSON：去尾随逗号；移除插值表达式（保留 null 占位，解析失败时跳过该向量）。
    /// </summary>
    private static string CleanRustJson(string macroBody)
    {
        var text = macroBody.Trim();
        // 去掉对象/数组末尾多余逗号：",}" / ",]"
        text = Regex.Replace(text, @",\s*([}\]])", "$1");
        return text;
    }

    private static bool LooksLikeJson(string text) =>
        text.StartsWith('{') || text.StartsWith('[') || text.StartsWith('"');

    private static JsonNode? TryParseJson(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch
        {
            return null;
        }
    }

    private static string UnescapeRustString(string rustLiteral)
    {
        if (rustLiteral.Length >= 2 && rustLiteral.StartsWith('"') && rustLiteral.EndsWith('"'))
        {
            rustLiteral = rustLiteral[1..^1];
        }

        return rustLiteral
            .Replace("\\\"", "\"")
            .Replace("\\n", "\n")
            .Replace("\\t", "\t")
            .Replace("\\\\", "\\");
    }

    private static List<PathSegment> ParsePath(string pathExpression)
    {
        var segments = new List<PathSegment>();
        foreach (Match match in IndexPathSegmentRegex.Matches(pathExpression))
        {
            if (match.Groups["key"].Success)
            {
                segments.Add(new PathSegment(match.Groups["key"].Value, -1));
            }
            else
            {
                segments.Add(new PathSegment(null, int.Parse(match.Groups["index"].Value)));
            }
        }

        return segments;
    }

    /// <summary>
    /// 从 openChar 位置提取平衡括号内容（不含括号本身）。
    /// </summary>
    private static string ExtractBalanced(string text, int openIndex, char openChar, char closeChar)
    {
        var depth = 0;
        var inString = false;
        for (var i = openIndex; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (ch == '\\')
                {
                    i++;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
            }
            else if (ch == openChar)
            {
                depth++;
            }
            else if (ch == closeChar)
            {
                depth--;
                if (depth == 0)
                {
                    return text.Substring(openIndex + 1, i - openIndex - 1);
                }
            }
        }

        return text.Substring(openIndex + 1);
    }

    /// <summary>
    /// 从函数体起始大括号提取平衡块内容。
    /// </summary>
    private static string ExtractBraceBlock(string content, int braceStart)
    {
        return ExtractBalanced(content, braceStart, '{', '}');
    }

    private static int CountLines(string text, int untilIndex)
    {
        var lines = 0;
        for (var i = 0; i < untilIndex && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines++;
            }
        }

        return lines;
    }
}

/// <summary>
/// 一个反向提取的测试向量。
/// </summary>
internal sealed class RustTestVector(
    string sourceFile,
    int line,
    string testName,
    string direction,
    string inputJson)
{
    public string SourceFile { get; } = sourceFile;
    public int Line { get; } = line;
    public string TestName { get; } = testName;
    public string Direction { get; } = direction;
    public string InputJson { get; } = inputJson;
    public List<VectorAssertion> Assertions { get; } = [];

    public bool HasAssertion(List<PathSegment> path) =>
        Assertions.Any(assertion => assertion.Path.Count == path.Count
            && assertion.Path.Zip(path, (a, b) => a.Equals(b)).All(equal => equal));
}

/// <summary>
/// 向量中的一条断言。
/// </summary>
internal sealed class VectorAssertion(
    VectorAssertionKind kind,
    List<PathSegment> path,
    JsonNode? expected,
    List<PathSegment>? mirrorInputPath)
{
    public VectorAssertionKind Kind { get; } = kind;
    public List<PathSegment> Path { get; } = path;
    public JsonNode? Expected { get; } = expected;
    public List<PathSegment>? MirrorInputPath { get; } = mirrorInputPath;
    public string? TypeKind { get; init; }
    public string? Fragment { get; init; }
}

internal enum VectorAssertionKind
{
    Equals,
    MirrorInput,
    TypeIs,
    Contains
}

/// <summary>
/// JSON 路径段：键名或数组下标。
/// </summary>
internal sealed record PathSegment(string? Key, int Index)
{
    public bool IsKey => Key is not null;
}

internal static class PathSegmentJsonExtensions
{
    /// <summary>
    /// 在 JSON 节点上按路径段导航；返回 null 表示路径不存在或类型不符。
    /// </summary>
    public static JsonNode? Navigate(this JsonNode? node, IReadOnlyList<PathSegment> path)
    {
        var current = node;
        foreach (var segment in path)
        {
            if (current is null)
            {
                return null;
            }

            if (segment.IsKey)
            {
                current = (current as JsonObject)?[segment.Key!];
            }
            else
            {
                if (current is not JsonArray array || segment.Index >= array.Count)
                {
                    return null;
                }

                current = array[segment.Index];
            }
        }

        return current;
    }

    /// <summary>
    /// 路径的显示形式：a.b[0].c。
    /// </summary>
    public static string ToDisplayPath(this IReadOnlyList<PathSegment> path) =>
        string.Join(string.Empty, path.Select(segment =>
            segment.IsKey ? "." + segment.Key : "[" + segment.Index + "]")).TrimStart('.');
}
