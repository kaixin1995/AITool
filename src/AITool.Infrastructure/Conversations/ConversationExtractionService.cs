using System.Text.Json;
using System.Text.RegularExpressions;

namespace AITool.Infrastructure.Conversations;

/// <summary>
/// 从代理请求与响应中提取结构化对话内容。
/// </summary>
public sealed class ConversationExtractionService
{
    private static readonly Regex SystemReminderBlockRegex = new(@"<system-reminder>[\s\S]*?</system-reminder>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex XmlLikeTagRegex = new(@"</?[a-zA-Z][^>]*>", RegexOptions.Compiled);
    private const int MaxToolResultTextLength = 12000;

    /// <summary>
    /// 从请求中解析工具来源。
    /// </summary>
    public string ResolveSourceTool(string? explicitSource, string? userAgent)
    {
        var normalizedExplicitSource = explicitSource?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(normalizedExplicitSource))
        {
            return normalizedExplicitSource;
        }

        var normalizedUserAgentSource = userAgent?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedUserAgentSource))
        {
            return "proxy";
        }

        var normalizedUserAgent = normalizedUserAgentSource.ToLowerInvariant();
        if (normalizedUserAgent.Contains("claude"))
        {
            return "claude-code";
        }

        if (normalizedUserAgent.Contains("codex"))
        {
            return "codex";
        }

        if (normalizedUserAgent.Contains("open-code") || normalizedUserAgent.Contains("opencode"))
        {
            return "open-code";
        }

        if (normalizedUserAgent.Contains("zcode"))
        {
            return "zcode";
        }

        return "proxy";
    }

    /// <summary>
    /// 从请求头中提取会话标识。
    /// 不同工具通过不同的请求头传递会话信息：
    /// claude-code → X-Claude-Code-Session-Id
    /// codex → Session-Id
    /// open-code → x-session-affinity
    /// </summary>
    public string ExtractSessionId(IDictionary<string, string> headers)
    {
        // claude-code 通过 X-Claude-Code-Session-Id 标识会话
        if (headers.TryGetValue("X-Claude-Code-Session-Id", out var claudeSessionId))
        {
            var value = claudeSessionId.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        // codex 通过 Session-Id 标识会话
        if (headers.TryGetValue("Session-Id", out var codexSessionId))
        {
            var value = codexSessionId.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        // open-code 通过 x-session-affinity 标识会话
        if (headers.TryGetValue("x-session-affinity", out var openCodeSessionId))
        {
            var value = openCodeSessionId.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        // ZCode 通过 x-session-id 标识会话
        if (headers.TryGetValue("x-session-id", out var zcodeSessionId))
        {
            var value = zcodeSessionId.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 构造会话分组键。
    /// </summary>
    public string BuildConversationGroupKey(string sourceTool, string sessionId, Guid requestId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            return $"{sourceTool}:{sessionId}";
        }

        return $"{sourceTool}:request:{requestId:N}";
    }

    /// <summary>
    /// 从请求体中提取用户输入文本。
    /// </summary>
    public string ExtractUserInputText(string requestBody, string protocolType, string requestPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            var root = doc.RootElement;

            if (requestPath.Contains("/v1/responses", StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeConversationText(ExtractResponsesInputText(root));
            }

            if (string.Equals(protocolType, "Anthropic", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                {
                    return NormalizeConversationText(ExtractLastUserMessage(messages));
                }
            }
            else
            {
                if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                {
                    return NormalizeConversationText(ExtractLastUserMessage(messages));
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    /// <summary>
    /// 清理对话文本中的系统注入片段，尽量还原用户真正输入。
    /// </summary>
    public string NormalizeConversationText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n").Trim();
        normalized = SystemReminderBlockRegex.Replace(normalized, string.Empty).Trim();

        var lines = normalized
            .Split('\n')
            .Select(x => x.TrimEnd())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        lines = lines
            .Where(x =>
                !x.StartsWith("<system-reminder>", StringComparison.OrdinalIgnoreCase) &&
                !x.StartsWith("</system-reminder>", StringComparison.OrdinalIgnoreCase) &&
                !x.StartsWith("<user-prompt-submit-hook>", StringComparison.OrdinalIgnoreCase) &&
                !x.StartsWith("</user-prompt-submit-hook>", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var endIndex = lines.Count - 1;
        while (endIndex >= 0 && !LooksLikeNaturalUserPrompt(lines[endIndex]))
        {
            endIndex--;
        }

        if (endIndex < 0)
        {
            return string.Empty;
        }

        var startIndex = endIndex;
        while (startIndex > 0 && LooksLikeNaturalUserPrompt(lines[startIndex - 1]))
        {
            startIndex--;
        }

        return string.Join("\n", lines.Skip(startIndex).Take(endIndex - startIndex + 1)).Trim();
    }

    /// <summary>
    /// 从响应体中提取 AI 输出正文。
    /// </summary>
    public string ExtractAssistantOutput(string responseBody, string protocolType, string requestPath)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return string.Empty;
        }

        if (LooksLikeSsePayload(responseBody))
        {
            return ExtractAssistantOutputFromSse(responseBody, protocolType, requestPath);
        }

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var output = ExtractAssistantOutputFromJson(doc.RootElement, protocolType, requestPath);
            var toolResult = ExtractToolResultSummary(doc.RootElement);
            return JoinAssistantParts(output, toolResult);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 从请求体中提取工具执行结果，作为 AI 行为的一部分展示。
    /// </summary>
    public string ExtractToolResultOutput(string requestBody, string protocolType, string requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(requestBody);
            return ExtractToolResultSummary(doc.RootElement);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 生成可搜索的纯文本内容。
    /// </summary>
    public string ToPlainText(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        return markdown
            .Replace("```", " ")
            .Replace("`", " ")
            .Replace("#", " ")
            .Replace("*", " ")
            .Replace("_", " ")
            .Replace(">", " ")
            .Replace("[", " ")
            .Replace("]", " ")
            .Replace("(", " ")
            .Replace(")", " ")
            .Replace("\r", " ")
            .Replace("\n", " ")
            .Trim();
    }

    private static string ExtractAssistantOutputFromJson(JsonElement root, string protocolType, string requestPath)
    {
        var contentParts = new List<string>();

        if (requestPath.Contains("/v1/responses", StringComparison.OrdinalIgnoreCase))
        {
            if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in output.EnumerateArray())
                {
                    AppendIfNotEmpty(contentParts, ExtractResponsesOutputItemText(item));
                }
            }
        }
        else if (string.Equals(protocolType, "Anthropic", StringComparison.OrdinalIgnoreCase))
        {
            if (root.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in contentArr.EnumerateArray())
                {
                    var type = item.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : string.Empty;
                    if (string.Equals(type, "thinking", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase))
                    {
                        AppendIfNotEmpty(contentParts, BuildAnthropicToolUseSummary(item));
                        continue;
                    }

                    AppendIfNotEmpty(contentParts, ExtractElementText(item, "text", "content"));
                }
            }
        }
        else if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                {
                    AppendIfNotEmpty(contentParts, content.GetString());
                }
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in content.EnumerateArray())
                    {
                        var itemType = item.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : string.Empty;
                        if (string.Equals(itemType, "reasoning", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(itemType, "thinking", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        AppendIfNotEmpty(contentParts, ExtractElementText(item, "text", "content"));
                    }
                }
            }
        }

        return string.Join("\n", contentParts.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
    }

    private static string ExtractAssistantOutputFromSse(string responseBody, string protocolType, string requestPath)
    {
        var contentBuilder = new System.Text.StringBuilder();
        var hasPendingToolArguments = false;
        var anthropicToolName = string.Empty;
        var anthropicToolInputBuilder = new System.Text.StringBuilder();
        var dataLines = responseBody.Replace("\r\n", "\n").Split('\n');

        foreach (var rawLine in dataLines)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var payload = line.Length > 5 ? line[5..].TrimStart() : string.Empty;
            if (string.IsNullOrWhiteSpace(payload) || string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                if (requestPath.Contains("/v1/responses", StringComparison.OrdinalIgnoreCase))
                {
                    if (root.TryGetProperty("type", out var typeElement))
                    {
                        var eventType = typeElement.GetString() ?? string.Empty;
                        if (string.Equals(eventType, "response.output_text.delta", StringComparison.OrdinalIgnoreCase)
                            && root.TryGetProperty("delta", out var deltaElement)
                            && deltaElement.ValueKind == JsonValueKind.String)
                        {
                            if (hasPendingToolArguments)
                            {
                                AppendSeparator(contentBuilder);
                                hasPendingToolArguments = false;
                            }

                            AppendRawIfNotEmpty(contentBuilder, deltaElement.GetString());
                        }
                        else if (string.Equals(eventType, "response.output_item.added", StringComparison.OrdinalIgnoreCase)
                            && root.TryGetProperty("item", out var item))
                        {
                            if (AppendToolCallSummaryIfPresent(contentBuilder, item))
                            {
                                hasPendingToolArguments = true;
                            }
                        }
                        else if (string.Equals(eventType, "response.function_call_arguments.delta", StringComparison.OrdinalIgnoreCase)
                            && root.TryGetProperty("delta", out var argsDelta)
                            && argsDelta.ValueKind == JsonValueKind.String)
                        {
                            AppendRawIfNotEmpty(contentBuilder, argsDelta.GetString());
                            hasPendingToolArguments = true;
                        }
                        else if (string.Equals(eventType, "response.output_item.done", StringComparison.OrdinalIgnoreCase)
                            && root.TryGetProperty("item", out var completedItem))
                        {
                            if (AppendFunctionCallChangeSummaryIfPresent(contentBuilder, completedItem))
                            {
                                hasPendingToolArguments = false;
                            }

                            if (AppendToolResultSummaryIfPresent(contentBuilder, completedItem))
                            {
                                hasPendingToolArguments = false;
                            }
                        }
                    }
                }
                else if (string.Equals(protocolType, "Anthropic", StringComparison.OrdinalIgnoreCase))
                {
                    var eventType = root.TryGetProperty("type", out var rootTypeValue) ? rootTypeValue.GetString() : string.Empty;
                    if (string.Equals(eventType, "content_block_start", StringComparison.OrdinalIgnoreCase)
                        && root.TryGetProperty("content_block", out var contentBlock))
                    {
                        anthropicToolName = ExtractAnthropicToolName(contentBlock);
                        anthropicToolInputBuilder.Clear();
                        AppendAnthropicToolUseWithInlineInputIfPresent(contentBuilder, contentBlock);
                    }

                    if (root.TryGetProperty("delta", out var delta))
                    {
                        var deltaType = delta.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : string.Empty;
                        if (string.Equals(deltaType, "text_delta", StringComparison.OrdinalIgnoreCase))
                        {
                            AppendRawIfNotEmpty(contentBuilder, ExtractElementText(delta, "text", "content"));
                        }
                        else if (string.Equals(deltaType, "input_json_delta", StringComparison.OrdinalIgnoreCase))
                        {
                            AppendRawIfNotEmpty(anthropicToolInputBuilder, ExtractElementText(delta, "partial_json"));
                        }
                    }

                    if (string.Equals(eventType, "content_block_stop", StringComparison.OrdinalIgnoreCase))
                    {
                        AppendAnthropicToolUseFromDeltaIfPresent(contentBuilder, anthropicToolName, anthropicToolInputBuilder.ToString());
                        anthropicToolName = string.Empty;
                        anthropicToolInputBuilder.Clear();
                    }
                }
                else if (root.TryGetProperty("choices", out var choices)
                    && choices.ValueKind == JsonValueKind.Array
                    && choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    if (first.TryGetProperty("delta", out var delta))
                    {
                        AppendRawIfNotEmpty(contentBuilder, ExtractElementText(delta, "content"));
                    }
                }
            }
            catch
            {
            }
        }

        return contentBuilder.ToString().Trim();
    }

    private static bool LooksLikeSsePayload(string text)
    {
        return text.Contains("\ndata:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || text.Contains("\nevent:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("event:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 构造元数据 JSON。
    /// </summary>
    public string BuildMetadataJson(string? userAgent, string? xApp, string sessionId)
    {
        var payload = new Dictionary<string, string>
        {
            ["userAgent"] = userAgent ?? string.Empty,
            ["xApp"] = xApp ?? string.Empty,
            ["sessionId"] = sessionId
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string ExtractResponsesInputText(JsonElement root)
    {
        if (!root.TryGetProperty("input", out var input))
        {
            return string.Empty;
        }

        if (input.ValueKind == JsonValueKind.String)
        {
            return input.GetString() ?? string.Empty;
        }

        if (input.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var items = input.EnumerateArray().ToList();
        for (var i = items.Count - 1; i >= 0; i--)
        {
            var item = items[i];
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var role = item.TryGetProperty("role", out var roleValue) ? roleValue.GetString() : string.Empty;
            var type = item.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : string.Empty;

            if (string.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)
                || !item.TryGetProperty("content", out var content))
            {
                continue;
            }

            var extracted = ExtractResponsesContentText(content);
            if (!string.IsNullOrWhiteSpace(extracted))
            {
                return extracted;
            }
        }

        return string.Empty;
    }

    private static string ExtractLastUserMessage(JsonElement messages)
    {
        string result = string.Empty;
        foreach (var message in messages.EnumerateArray())
        {
            var role = message.TryGetProperty("role", out var roleValue) ? roleValue.GetString() : string.Empty;
            if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!message.TryGetProperty("content", out var content))
            {
                continue;
            }

            if (content.ValueKind == JsonValueKind.String)
            {
                result = content.GetString() ?? string.Empty;
                continue;
            }

            if (content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var parts = new List<string>();
            var hasNaturalUserContent = false;
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var itemType = item.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : string.Empty;
                if (string.Equals(itemType, "tool_result", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var text = ExtractElementText(item, "text", "content", "value");
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                parts.Add(text.Trim());
                hasNaturalUserContent = true;
            }

            result = hasNaturalUserContent
                ? string.Join("\n", parts.Where(x => !string.IsNullOrWhiteSpace(x))).Trim()
                : string.Empty;
        }

        return result;
    }

    private static string ExtractResponsesContentText(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var blockType = block.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : string.Empty;
            if (!string.IsNullOrWhiteSpace(blockType)
                && !string.Equals(blockType, "input_text", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(blockType, "text", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AppendIfNotEmpty(parts, ExtractElementText(block, "text", "content", "value"));
        }

        return string.Join("\n", parts.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
    }

    /// <summary>
    /// 从请求或响应 JSON 中提取工具结果摘要。
    /// </summary>
    private static string ExtractToolResultSummary(JsonElement root)
    {
        var parts = new List<string>();
        ExtractToolResultSummaryRecursive(root, parts);
        return string.Join("\n\n", parts.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
    }

    private static void ExtractToolResultSummaryRecursive(JsonElement element, List<string> parts)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            AppendIfNotEmpty(parts, BuildToolUseResultSummary(element));
            var type = element.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : string.Empty;
            if (string.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "tool_result", StringComparison.OrdinalIgnoreCase))
            {
                AppendIfNotEmpty(parts, BuildToolResultContentSummary(element));
            }

            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, "toolUseResult", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ExtractToolResultSummaryRecursive(property.Value, parts);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ExtractToolResultSummaryRecursive(item, parts);
            }
        }
    }

    private static string BuildToolUseResultSummary(JsonElement element)
    {
        if (!element.TryGetProperty("toolUseResult", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var filePath = ExtractElementText(result, "filePath", "file_path");
        var lines = new List<string> { "工具结果: 代码改动" };
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            lines.Add($"文件: {filePath}");
        }

        if (result.TryGetProperty("structuredPatch", out var patch) && patch.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in patch.EnumerateArray())
            {
                AppendIfNotEmpty(lines, BuildStructuredPatchSummary(block));
            }
        }

        var oldString = ExtractElementText(result, "oldString", "old_string");
        var newString = ExtractElementText(result, "newString", "new_string");
        if (!string.IsNullOrWhiteSpace(oldString) || !string.IsNullOrWhiteSpace(newString))
        {
            lines.Add("```diff");
            AppendDiffLines(lines, oldString, "-");
            AppendDiffLines(lines, newString, "+");
            lines.Add("```");
        }

        return string.Join("\n", lines).Trim();
    }

    private static string BuildStructuredPatchSummary(JsonElement block)
    {
        if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("lines", out var linesElement) || linesElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var lines = new List<string> { "```diff" };
        foreach (var line in linesElement.EnumerateArray())
        {
            if (line.ValueKind == JsonValueKind.String)
            {
                lines.Add(line.GetString() ?? string.Empty);
            }
        }
        lines.Add("```");
        return string.Join("\n", lines);
    }

    private static string BuildToolResultContentSummary(JsonElement element)
    {
        var content = ExtractElementText(element, "content", "output", "text");
        if (string.IsNullOrWhiteSpace(content) || !LooksLikeUsefulToolResult(content))
        {
            return string.Empty;
        }

        var text = content.Trim();
        if (text.Length > MaxToolResultTextLength)
        {
            text = text[..MaxToolResultTextLength] + "\n...";
        }

        return $"工具结果:\n```text\n{text}\n```";
    }

    private static void AppendDiffLines(List<string> lines, string text, string prefix)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            lines.Add(prefix + line);
        }
    }

    private static string BuildToolInputChangeSummary(string? toolName, string arguments)
    {
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            return BuildToolInputChangeSummary(toolName, doc.RootElement);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildToolInputChangeSummary(string? toolName, JsonElement input)
    {
        if (!LooksLikeFileChangeTool(toolName) || input.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var oldString = ExtractElementText(input, "old_string", "oldString");
        var newString = ExtractElementText(input, "new_string", "newString", "content");
        if (string.IsNullOrWhiteSpace(oldString) && string.IsNullOrWhiteSpace(newString))
        {
            return string.Empty;
        }

        var lines = new List<string> { "工具结果: 代码改动" };
        var filePath = ExtractElementText(input, "file_path", "filePath", "path");
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            lines.Add($"文件: {filePath}");
        }

        lines.Add("```diff");
        AppendDiffLines(lines, oldString, "-");
        AppendDiffLines(lines, newString, "+");
        lines.Add("```");
        return string.Join("\n", lines).Trim();
    }

    private static bool LooksLikeFileChangeTool(string? toolName)
    {
        return string.Equals(toolName, "Edit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "Write", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "MultiEdit", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "NotebookEdit", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldShowToolCallArguments(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return false;
        }

        return !string.Equals(toolName, "Read", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(toolName, "Grep", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(toolName, "Glob", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(toolName, "TodoWrite", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEmptyObject(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object && !element.EnumerateObject().Any();
    }

    private static bool LooksLikeUsefulToolResult(string text)
    {
        var normalized = text.Trim();
        return normalized.Contains("updated successfully", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("has been updated", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Added ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Removed ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Modified ", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("新增", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("删除", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("修改", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把 Responses 输出项整理成便于展示的文本，尽量保留工具调用细节。
    /// </summary>
    private static string ExtractResponsesOutputItemText(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var type = item.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : string.Empty;
        if (string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase))
        {
            return BuildFunctionCallSummary(item);
        }

        if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var blockType = block.TryGetProperty("type", out var blockTypeValue) ? blockTypeValue.GetString() : string.Empty;
            if (string.Equals(blockType, "output_text", StringComparison.OrdinalIgnoreCase)
                || string.Equals(blockType, "text", StringComparison.OrdinalIgnoreCase))
            {
                AppendIfNotEmpty(parts, ExtractElementText(block, "text", "output_text", "content"));
            }
        }

        return string.Join("\n", parts.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
    }

    /// <summary>
    /// 将工具调用信息拼成一段可直接展示的摘要文本。
    /// </summary>
    private static string BuildFunctionCallSummary(JsonElement item)
    {
        var name = item.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : string.Empty;
        var arguments = item.TryGetProperty("arguments", out var argumentsValue) && argumentsValue.ValueKind == JsonValueKind.String
            ? argumentsValue.GetString()
            : string.Empty;

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(name))
        {
            lines.Add($"工具调用: {name}");
        }

        if (!string.IsNullOrWhiteSpace(arguments))
        {
            var changeSummary = BuildToolInputChangeSummary(name, arguments);
            if (!string.IsNullOrWhiteSpace(changeSummary))
            {
                lines.Add(changeSummary);
            }
            else if (ShouldShowToolCallArguments(name))
            {
                lines.Add(arguments.Trim());
            }
        }

        return string.Join("\n", lines).Trim();
    }

    private static string BuildAnthropicToolUseSummary(JsonElement item)
    {
        var name = item.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : string.Empty;
        if (!item.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object || IsEmptyObject(input))
        {
            return string.Empty;
        }

        return BuildToolUseSummaryFromInput(name, input);
    }

    private static string BuildToolUseSummaryFromInput(string? name, JsonElement input)
    {
        var changeSummary = BuildToolInputChangeSummary(name, input);
        if (string.IsNullOrWhiteSpace(changeSummary) && !ShouldShowToolCallArguments(name))
        {
            return string.Empty;
        }

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(name))
        {
            lines.Add($"工具调用: {name}");
        }

        if (!string.IsNullOrWhiteSpace(changeSummary))
        {
            lines.Add(changeSummary);
        }
        else if (ShouldShowToolCallArguments(name))
        {
            lines.Add(input.GetRawText());
        }
        return string.Join("\n", lines).Trim();
    }

    private static bool AppendToolResultSummaryIfPresent(System.Text.StringBuilder builder, JsonElement item)
    {
        var summary = ExtractToolResultSummary(item);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return false;
        }

        AppendSeparator(builder);
        builder.Append(summary);
        return true;
    }

    private static bool AppendAnthropicToolUseWithInlineInputIfPresent(System.Text.StringBuilder builder, JsonElement item)
    {
        var summary = BuildAnthropicToolUseSummary(item);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return false;
        }

        AppendSeparator(builder);
        builder.Append(summary);
        return true;
    }

    private static bool AppendAnthropicToolUseFromDeltaIfPresent(System.Text.StringBuilder builder, string? toolName, string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(arguments);
            var summary = BuildToolUseSummaryFromInput(toolName, doc.RootElement);
            if (string.IsNullOrWhiteSpace(summary))
            {
                return false;
            }

            AppendSeparator(builder);
            builder.Append(summary);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool AppendToolInputChangeSummaryIfPresent(System.Text.StringBuilder builder, string? toolName, string arguments)
    {
        var summary = BuildToolInputChangeSummary(toolName, arguments);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return false;
        }

        AppendSeparator(builder);
        builder.Append(summary);
        return true;
    }

    private static bool AppendFunctionCallChangeSummaryIfPresent(System.Text.StringBuilder builder, JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var type = item.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : string.Empty;
        if (!string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = item.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : string.Empty;
        var arguments = item.TryGetProperty("arguments", out var argumentsValue) && argumentsValue.ValueKind == JsonValueKind.String
            ? argumentsValue.GetString()
            : string.Empty;
        return AppendToolInputChangeSummaryIfPresent(builder, name, arguments ?? string.Empty);
    }

    private static string ExtractAnthropicToolName(JsonElement contentBlock)
    {
        if (contentBlock.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var type = contentBlock.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : string.Empty;
        return string.Equals(type, "tool_use", StringComparison.OrdinalIgnoreCase)
            ? contentBlock.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? string.Empty : string.Empty
            : string.Empty;
    }

    /// <summary>
    /// 遇到 Responses 的 function_call 输出项时，把工具名补到展示文本里。
    /// </summary>
    private static bool AppendToolCallSummaryIfPresent(System.Text.StringBuilder builder, JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var type = item.TryGetProperty("type", out var typeValue) ? typeValue.GetString() : string.Empty;
        if (!string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var summary = BuildFunctionCallSummary(item);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return false;
        }

        if (builder.Length > 0 && builder[^1] != '\n')
        {
            builder.Append('\n');
        }

        builder.Append(summary);
        if (builder.Length > 0 && builder[^1] != '\n')
        {
            builder.Append('\n');
        }

        return true;
    }

    private static string ExtractElementText(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!element.TryGetProperty(propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString() ?? string.Empty;
            }

            if (value.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var item in value.EnumerateArray())
                {
                    AppendIfNotEmpty(parts, ExtractElementText(item, "text", "content", "value"));
                }
                if (parts.Count > 0)
                {
                    return string.Join("\n", parts);
                }
            }
        }

        return string.Empty;
    }

    private static void AppendIfNotEmpty(List<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            values.Add(value.Trim());
        }
    }

    private static string JoinAssistantParts(params string[] values)
    {
        return string.Join("\n\n", values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
    }

    private static void AppendSeparator(System.Text.StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != '\n')
        {
            builder.Append("\n\n");
        }
    }

    /// <summary>
    /// 流式分片需要保留前后空格，避免把相邻文本错误粘连。
    /// </summary>
    private static void AppendRawIfNotEmpty(System.Text.StringBuilder builder, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            builder.Append(value);
        }
    }

    private static bool LooksLikeNaturalUserPrompt(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (XmlLikeTagRegex.IsMatch(text))
        {
            return false;
        }

        if (text.StartsWith("Note:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Reminder:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("IMPORTANT:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("As you answer", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Contents of ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
