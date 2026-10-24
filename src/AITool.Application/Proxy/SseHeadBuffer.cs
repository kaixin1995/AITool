namespace AITool.Application.Proxy;

/// <summary>
/// SSE 首包蓄流（holdWriter 的 SSE 形态，默认关闭）：扣住流的开头几个「热身块」，
/// 直到出现内容块或攒满上限才一次性写给客户端。
/// <para>
/// 动机：上游回 200 后立刻断流/出错时，若首块已写给客户端，回退窗口即告关闭
///（CanFallback=false），客户端只能吃到一个残流。扣住热身块后，这类失败发生在
/// 「尚未写任何字节」阶段，可无感换下一候选。热身块包括：注释/心跳、Anthropic
/// message_start/ping、OpenAI 的 role-only 增量与 response.created 等开头事件。
/// 攒满 <see cref="MaxHeldBlocks"/> 块或 <see cref="MaxHeldChars"/> 字符即强制放行，
/// 保证正常流的首字延迟增量有界（一般只有一块，几十毫秒级）。
/// </para>
/// <para>
/// 不设超时定时器：上游在热身阶段停摆由流空闲超时结束整个尝试，此时蓄流块从未写出，
/// CanFallback 保持 true——这正是想要的结局，无需额外的放行定时器。
/// </para>
/// </summary>
public sealed class SseHeadBuffer
{
    /// <summary>最多扣住的事件块数（超过即强制放行）。</summary>
    public const int MaxHeldBlocks = 4;

    /// <summary>最多扣住的累计字符数（超过即强制放行）。</summary>
    public const int MaxHeldChars = 8192;

    private readonly List<string> _held = [];
    private int _heldChars;
    private bool _released;

    /// <summary>是否已放行（放行后所有块直写）。</summary>
    public bool Released => _released;

    /// <summary>
    /// 尝试扣住一个已渲染的 SSE 块。返回 true 表示扣住（暂不写客户端）；
    /// 返回 false 表示应当立即写出（此时 <see cref="Drain"/> 会一并给出此前扣住的块）。
    /// </summary>
    public bool TryHold(string chunk)
    {
        if (_released)
        {
            return false;
        }

        if (IsContentBearing(chunk) || _held.Count >= MaxHeldBlocks || _heldChars + chunk.Length > MaxHeldChars)
        {
            Release();
            return false;
        }

        _held.Add(chunk);
        _heldChars += chunk.Length;
        return true;
    }

    /// <summary>强制放行：此后所有块直写。</summary>
    public void Release() => _released = true;

    /// <summary>取出并清空扣住的块（流正常结束或强制放行时冲刷用）。</summary>
    public IReadOnlyList<string> Drain()
    {
        if (_held.Count == 0)
        {
            return [];
        }

        var drained = _held.ToList();
        _held.Clear();
        _heldChars = 0;
        return drained;
    }

    /// <summary>
    /// 判断 SSE 块是否携带实际内容。按「增量/正文事件类型」判定，而不是子串扫描——
    /// 热身事件自身就带 content/text/error 字段名（Anthropic message_start 有 "content":[],
    /// OpenAI 首 chunk 有 "content":""，Responses created 有 "text":{}/"error":null），按字段名扫会全部误放行。
    /// 命中以下任一标记才算内容块：Anthropic content_block_delta / content_block_start（含 text_delta、
    /// input_json_delta 载荷）；OpenAI chat 的非空文本增量；Responses 的 output_text.delta；
    /// 结束与错误事件（[DONE]、finish_reason、显式 "error":{ 非空对象）同样立即放行。
    /// </summary>
    private static bool IsContentBearing(string chunk)
    {
        // 注释与心跳永远不是内容。
        if (chunk.Contains("event: ping", StringComparison.Ordinal)
            || chunk.Contains("\"type\":\"ping\"", StringComparison.Ordinal)
            || chunk.Contains("\"type\": \"ping\"", StringComparison.Ordinal))
        {
            return false;
        }

        // Anthropic：内容块开始/增量（event 行或 type 字段两种形态）。
        if (chunk.Contains("event: content_block_delta", StringComparison.Ordinal)
            || chunk.Contains("event: content_block_start", StringComparison.Ordinal)
            || chunk.Contains("\"type\":\"content_block_delta\"", StringComparison.Ordinal)
            || chunk.Contains("\"type\": \"content_block_delta\"", StringComparison.Ordinal)
            || chunk.Contains("\"type\":\"content_block_start\"", StringComparison.Ordinal)
            || chunk.Contains("\"type\": \"content_block_start\"", StringComparison.Ordinal))
        {
            return true;
        }

        // OpenAI chat：非空文本增量（"content":"" 是热身，"content":"字" 才放行）。
        if (ContainsNonEmptyJsonStringField(chunk, "content"))
        {
            return true;
        }

        // Responses：文本增量事件。
        if (chunk.Contains("output_text.delta", StringComparison.Ordinal)
            || chunk.Contains("output_text.added", StringComparison.Ordinal))
        {
            return true;
        }

        // 结束与错误：立即放行（错误块交给上层判定，不能扣在缓冲里）。
        if (chunk.Contains("[DONE]", StringComparison.Ordinal)
            || chunk.Contains("finish_reason", StringComparison.Ordinal)
            || chunk.Contains("message_stop", StringComparison.Ordinal)
            || chunk.Contains("response.completed", StringComparison.Ordinal))
        {
            return true;
        }

        if (chunk.Contains("\"error\":{", StringComparison.Ordinal) || chunk.Contains("\"error\": {", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    /// <summary>检测 JSON 文本中 "field":"非空值" 形态（字段名后紧跟非空字符串值；"field":"" 空串不算）。</summary>
    private static bool ContainsNonEmptyJsonStringField(string chunk, string field)
    {
        foreach (var probe in new[] { "\"" + field + "\":\"", "\"" + field + "\": \"" })
        {
            var index = chunk.IndexOf(probe, StringComparison.Ordinal);
            while (index >= 0)
            {
                var valueStart = index + probe.Length;
                if (valueStart < chunk.Length && chunk[valueStart] != '"')
                {
                    return true;
                }

                index = chunk.IndexOf(probe, index + 1, StringComparison.Ordinal);
            }
        }

        return false;
    }
}
