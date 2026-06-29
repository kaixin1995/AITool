using System.Text;
using System.Text.Json;

namespace AITool.Web.Services;

/// <summary>
/// 流式转发过程中累积 AI 正文（text delta）的捕获器。
/// <para>
/// 诊断用的 <c>responseBuilder</c> 会把整段 SSE（含 usage、工具调用等）截断到 64KB，
/// 导致 AI 正文超过 64KB 时在写入对话记录时丢失。本捕获器只提取 delta 文本，
/// 受 <see cref="MaxContentChars"/>（默认 1MB）约束，足够覆盖几乎所有 AI 正文回复，
/// 且只持有纯文本，避免把整段 SSE 原文留在内存大对象堆。
/// </para>
/// </summary>
internal sealed class ConversationStreamCapture
{
    /// <summary>
    /// 单轮 AI 正文的最大累积字符数。1MB 足够覆盖绝大多数回复，超出部分截断并标注。
    /// </summary>
    public const int MaxContentChars = 1024 * 1024;

    private readonly StringBuilder _builder = new();
    private bool _capped;

    /// <summary>
    /// 从 OpenAI Chat Completions SSE payload（choices[].delta.content）提取文本并累积。
    /// </summary>
    public void AppendOpenAiChatDelta(ReadOnlySpan<char> payload)
    {
        if (_capped || payload.IsEmpty || payload[0] != '{')
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload.ToString());
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var choice in choices.EnumerateArray())
            {
                if (!choice.TryGetProperty("delta", out var delta))
                {
                    continue;
                }

                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    AppendCore(content.GetString());
                }
            }
        }
        catch
        {
            // 解析失败忽略，不影响转发主链路。
        }
    }

    /// <summary>
    /// 从 OpenAI Responses SSE payload 提取文本增量并累积。
    /// 支持 response.output_text.delta（delta 为字符串）与 response.completed（output 数组里的文本）。
    /// </summary>
    public void AppendOpenAiResponsesDelta(ReadOnlySpan<char> payload)
    {
        if (_capped || payload.IsEmpty || payload[0] != '{')
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload.ToString());
            var type = doc.RootElement.TryGetProperty("type", out var typeEl) ? typeEl.GetString() ?? string.Empty : string.Empty;

            // response.output_text.delta：delta 直接是文本字符串。
            if (string.Equals(type, "response.output_text.delta", StringComparison.OrdinalIgnoreCase))
            {
                if (doc.RootElement.TryGetProperty("delta", out var deltaEl) && deltaEl.ValueKind == JsonValueKind.String)
                {
                    AppendCore(deltaEl.GetString());
                }
                return;
            }

            // response.completed：兜底从 output 数组里取文本，覆盖非流式伪装成 Responses 的场景。
            if (string.Equals(type, "response.completed", StringComparison.OrdinalIgnoreCase))
            {
                if (doc.RootElement.TryGetProperty("response", out var resp)
                    && resp.TryGetProperty("output", out var output)
                    && output.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in output.EnumerateArray())
                    {
                        AppendResponsesOutputItem(item);
                    }
                }
            }
        }
        catch
        {
            // 解析失败忽略。
        }
    }

    /// <summary>
    /// 从 Anthropic SSE payload（content_block_delta 事件的 delta.text / delta.content）提取文本并累积。
    /// </summary>
    /// <param name="eventName">SSE 事件名，仅 content_block_delta 需要累积。</param>
    public void AppendAnthropicDelta(string eventName, ReadOnlySpan<char> payload)
    {
        if (_capped || payload.IsEmpty || payload[0] != '{')
        {
            return;
        }

        // 仅 content_block_delta 事件携带正文增量；message_start/usage 等跳过。
        if (!string.Equals(eventName, "content_block_delta", StringComparison.OrdinalIgnoreCase))
        {
            // 顺带兜底 content_block_start（type=text 的首块），少数实现把首段正文放在这里。
            if (!string.Equals(eventName, "content_block_start", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        try
        {
            using var doc = JsonDocument.Parse(payload.ToString());
            if (!doc.RootElement.TryGetProperty("delta", out var delta))
            {
                return;
            }

            // delta.text（text_delta）或 delta.content。
            if (delta.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                AppendCore(text.GetString());
            }
            else if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            {
                AppendCore(content.GetString());
            }
        }
        catch
        {
            // 解析失败忽略。
        }
    }

    /// <summary>
    /// 返回累积的 AI 正文。若达到上限会附带截断标注。
    /// </summary>
    public string Build()
    {
        return _builder.ToString();
    }

    private void AppendCore(string? text)
    {
        if (string.IsNullOrEmpty(text) || _capped)
        {
            return;
        }

        if (_builder.Length + text.Length > MaxContentChars)
        {
            var remaining = MaxContentChars - _builder.Length;
            if (remaining > 0)
            {
                _builder.Append(text, 0, remaining);
            }
            _builder.Append("\n\n...（AI 正文超过 ").Append(MaxContentChars).Append(" 字符上限，已截断）");
            _capped = true;
            return;
        }

        _builder.Append(text);
    }

    private void AppendResponsesOutputItem(JsonElement item)
    {
        // Responses output 里 message 类型项的 content 数组含文本。
        if (item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    AppendCore(text.GetString());
                }
            }
        }
    }
}
