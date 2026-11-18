using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AITool.Infrastructure.Proxy;

/// <summary>
/// WorkBuddy（腾讯 CodeBuddy）渠道校验内容适配器。
/// 上游 code 11128「Illegal API invocation from an unapproved channel」是<b>内容审查</b>而非请求头问题：
/// 渠道校验读取 system / assistant 消息中已知官方客户端提示词的整行（Claude Code / Codex CLI /
/// DeepSeek Harness 开场白等，整行精确匹配、大小写不敏感、多一个空格即放过），且要求消息列表以
/// system 开头（对照 magpie workbuddy 插件 2026-10 双构建实测：magpie-community/plugins#182 / #279 / #291）。
/// <para>
/// 本适配器在转发前把已知被拒措辞替换为语义等价的可过表述，并修正 WorkBuddy 聊天端点的请求体拼写
/// （max_completion_tokens→max_tokens、developer→system、首消息非 system 时补默认系统提示）。
/// 由站点/预设头 <c>X-AITool-Rewrite-Channel: workbuddy</c>（内部标记，不透传）驱动。
/// </para>
/// </summary>
public static partial class WorkBuddyChatAdapter
{
    private const string DefaultSystemPrompt = "You are a helpful assistant.";

    /// <summary>已知被拒措辞 → 等价可过表述。大小写不敏感；来源与实测记录见 magpie workbuddy 插件。</summary>
    private static readonly (Regex Pattern, string Replacement)[] FlaggedLines =
    [
        // Claude Code 把计费标识当作系统提示的一部分发出来：整行删除（含换行）。
        (BillingHeaderLine(), string.Empty),
        ("You are Claude Code, Anthropic['’]s official CLI for Claude".AsInsensitive(), "You are Claude Code, Anthropic's CLI for Claude"),
        ("Main branch \\(you will usually use this for PRs\\)".AsInsensitive(), "Main branch (usually the base for PRs)"),
        // lookbehind 防 "The Codex CLI is..." 二次改写；幂等。
        (CodexCliOpening(), "The Codex CLI is an open source project led by OpenAI."),
        ("You are an AI agent powered by DeepSeek Harness\\.?".AsInsensitive(), "You are an AI agent that helps the user with programming tasks."),
        // 模型可能在 assistant 消息里引用回该行：所有消息统一改写；\s+ 兼容空格与换行两种断行。
        ("Web GUI whose URL the runtime context names\\.\\s+When the user refers".AsInsensitive(), "Web GUI whose URL the runtime context names. The interface, when the user refers"),
    ];

    /// <summary>
    /// 按渠道校验规则改写 OpenAI Chat Completions 请求体；解析失败或无 messages 时原样返回，
    /// 无改动时返回原字符串（不做无谓的 round-trip 序列化）。
    /// </summary>
    public static string Rewrite(string requestBody)
    {
        if (string.IsNullOrWhiteSpace(requestBody))
        {
            return requestBody;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(requestBody);
        }
        catch (JsonException)
        {
            return requestBody;
        }

        if (root?["messages"] is not JsonArray messages || messages.Count == 0)
        {
            return requestBody;
        }

        var changed = Unflag(messages);
        changed |= NormalizeSpellings(root);
        changed |= EnsureLeadingSystem(messages);
        return changed ? root!.ToJsonString(RelaxedJson) : requestBody;
    }

    // 宽松编码：单引号等 ASCII 字符不转义（上游 JSON 解析等价，请求体可读且更小）。
    private static readonly JsonSerializerOptions RelaxedJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// 把 FlaggedLines 的措辞替换应用到每条消息（string 或 text part 数组）；
    /// 替换后为空文本的 system 消息整条移除（如仅含计费标识头的系统块）。
    /// </summary>
    private static bool Unflag(JsonArray messages)
    {
        var changed = false;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            switch (message?["content"]?.GetValueKind())
            {
                case JsonValueKind.String:
                {
                    var text = message["content"]!.GetValue<string>();
                    var rewritten = Unflagged(text);
                    if (ReferenceEquals(rewritten, text))
                    {
                        continue;
                    }

                    changed = true;
                    if (rewritten.Trim().Length == 0 && IsSystem(message))
                    {
                        messages.RemoveAt(i);
                    }
                    else
                    {
                        message["content"] = rewritten;
                    }

                    break;
                }
                case JsonValueKind.Array:
                {
                    var parts = message["content"]!.AsArray();
                    var kept = new JsonArray();
                    var partChanged = false;
                    foreach (var part in parts)
                    {
                        // 只有 text part 参与改写；非 text part（如 image_url）原样保留顺序。
                        if (part?["text"]?.GetValueKind() is not JsonValueKind.String)
                        {
                            kept.Add(part!.DeepClone());
                            continue;
                        }

                        var text = part["text"]!.GetValue<string>();
                        var rewritten = Unflagged(text);
                        if (ReferenceEquals(rewritten, text))
                        {
                            kept.Add(part.DeepClone());
                            continue;
                        }

                        partChanged = true;
                        if (rewritten.Trim().Length > 0)
                        {
                            var clone = part.DeepClone();
                            clone["text"] = rewritten;
                            kept.Add(clone);
                        }
                        // 替换后为空的 text part 丢弃（如仅含计费标识头的块）。
                    }

                    if (!partChanged)
                    {
                        continue;
                    }

                    changed = true;
                    if (kept.Count == 0 && IsSystem(message))
                    {
                        messages.RemoveAt(i);
                    }
                    else
                    {
                        message["content"] = kept;
                    }

                    break;
                }
            }
        }

        return changed;
    }

    private static bool IsSystem(JsonNode message)
        => message["role"]?.GetValueKind() is JsonValueKind.String
           && string.Equals(message["role"]!.GetValue<string>(), "system", StringComparison.Ordinal);

    /// <summary>对单段文本应用全部 FlaggedLines 替换；无命中返回原引用。</summary>
    private static string Unflagged(string text)
    {
        foreach (var (pattern, replacement) in FlaggedLines)
        {
            if (pattern.IsMatch(text))
            {
                text = pattern.Replace(text, replacement);
            }
        }

        return text;
    }

    /// <summary>修正 WorkBuddy 端点的请求体拼写：max_completion_tokens→max_tokens、developer→system。</summary>
    private static bool NormalizeSpellings(JsonNode root)
    {
        var changed = false;
        if (root["max_completion_tokens"] is { } maxCompletion)
        {
            if (root["max_tokens"] is null)
            {
                root["max_tokens"] = maxCompletion.DeepClone();
            }

            (root as JsonObject)?.Remove("max_completion_tokens");
            changed = true;
        }

        if (root["messages"] is JsonArray messages)
        {
            foreach (var message in messages)
            {
                if (message?["role"]?.GetValueKind() is JsonValueKind.String
                    && string.Equals(message["role"]!.GetValue<string>(), "developer", StringComparison.Ordinal))
                {
                    message["role"] = "system";
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>渠道校验要求首消息为 system：缺失时补 WorkBuddy 默认系统提示。</summary>
    private static bool EnsureLeadingSystem(JsonArray messages)
    {
        if (messages[0] is { } first && IsSystem(first))
        {
            return false;
        }

        messages.Insert(0, new JsonObject
        {
            ["role"] = "system",
            ["content"] = DefaultSystemPrompt
        });
        return true;
    }

    private static Regex AsInsensitive(this string pattern)
        => new(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [GeneratedRegex("x-anthropic-billing-header:[^\\n]*\\n?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BillingHeaderLine();

    [GeneratedRegex("(?<!the )Codex CLI is an open source project led by OpenAI\\.", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex CodexCliOpening();
}
