using System.Text.Json;

namespace AITool.Infrastructure.Conversations;

/// <summary>
/// 从代理请求与响应中提取结构化对话内容。
/// </summary>
public sealed class ConversationExtractionService
{
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
                return ExtractResponsesInputText(root);
            }

            if (string.Equals(protocolType, "Anthropic", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                {
                    return ExtractLastUserMessage(messages);
                }
            }
            else
            {
                if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                {
                    return ExtractLastUserMessage(messages);
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    /// <summary>
    /// 从响应体中提取 AI 输出正文。
    /// </summary>
    public string ExtractAssistantOutput(string responseBody, string protocolType, string requestPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            var contentParts = new List<string>();

            if (requestPath.Contains("/v1/responses", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in output.EnumerateArray())
                    {
                        if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var block in content.EnumerateArray())
                            {
                                AppendIfNotEmpty(contentParts, ExtractElementText(block, "text", "output_text", "content"));
                            }
                        }
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

        var parts = new List<string>();
        foreach (var item in input.EnumerateArray())
        {
            var role = item.TryGetProperty("role", out var roleValue) ? roleValue.GetString() : string.Empty;
            if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    AppendIfNotEmpty(parts, ExtractElementText(block, "text", "content", "value"));
                }
            }
        }

        return string.Join("\n", parts.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
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

            if (message.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                {
                    result = content.GetString() ?? string.Empty;
                }
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    var parts = new List<string>();
                    foreach (var item in content.EnumerateArray())
                    {
                        AppendIfNotEmpty(parts, ExtractElementText(item, "text", "content", "value"));
                    }
                    result = string.Join("\n", parts.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
                }
            }
        }

        return result;
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
}
