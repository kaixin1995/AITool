using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AITool.Domain.Proxy;

namespace AITool.Protocol;

/// <summary>
/// Gemini GenerateContent 协议桥（请求方向）。移植自 gcli2api
/// （reference-projects/gcli2api/src/converter/anthropic2gemini.py、openai2gemini.py、gemini_fix.py、antigravity_fix.py）。
/// <para>
/// 上游为 Google Code Assist 的 v1internal 接口：GeminiCLI（cloudcode-pa.googleapis.com）与
/// Antigravity（daily-cloudcode-pa.googleapis.com）共用 GenerateContent 语义，但 Antigravity 额外需要
/// CLI 封套（project/requestId/labels/sessionId 等）。本文件产出最终上游请求体：
/// Anthropic/OpenAI 客户端请求 → 内层 GenerateContent → 规范化 → CLI 封套。
/// </para>
/// </summary>
public static partial class ProxyProtocolBridge
{
    /// <summary>
    /// Google 官方的 thought signature 校验跳过占位符：作为 functionCall part 的 thoughtSignature 发送，
    /// 上游跳过跨轮签名校验（中转/换号场景下真实签名回传反而会触发 Corrupted thought signature）。
    /// </summary>
    public const string SkipThoughtSignatureValidator = "skip_thought_signature_validator";

    /// <summary>
    /// 构建 Anthropic Messages 请求对应的 Gemini 内层 GenerateContent 请求。
    /// 输出未做规范化与封套，供 NormalizeGeminiInner / WrapGeminiUpstreamBody 继续处理。
    /// </summary>
    public static JsonObject BuildGeminiInnerFromAnthropic(JsonObject anthropic, string targetModelName)
    {
        var contents = ConvertAnthropicMessagesToGeminiContents(anthropic["messages"] as JsonArray);
        contents = ReorganizeGeminiToolMessages(contents);
        var inner = new JsonObject
        {
            ["contents"] = contents,
            ["generationConfig"] = BuildGeminiGenerationConfigFromAnthropic(anthropic),
        };

        var systemInstruction = BuildGeminiSystemInstructionFromAnthropic(anthropic["system"]);
        if (systemInstruction is not null)
        {
            inner["systemInstruction"] = systemInstruction;
        }

        var tools = ConvertAnthropicToolsToGemini(anthropic["tools"] as JsonArray);
        if (tools is { Count: > 0 })
        {
            inner["tools"] = tools;
        }

        var toolConfig = ConvertAnthropicToolChoiceToGemini(anthropic["tool_choice"]);
        if (toolConfig is not null)
        {
            inner["toolConfig"] = toolConfig;
        }

        return inner;
    }

    /// <summary>
    /// 构建 OpenAI Chat Completions 请求对应的 Gemini 内层 GenerateContent 请求。
    /// </summary>
    public static JsonObject BuildGeminiInnerFromOpenAi(JsonObject openAi, string targetModelName)
    {
        var contents = ConvertOpenAiMessagesToGeminiContents(openAi["messages"] as JsonArray);
        var generationConfig = BuildGeminiGenerationConfigFromOpenAi(openAi);

        // response_format → responseMimeType / responseSchema。
        if (openAi["response_format"] is JsonObject responseFormat)
        {
            var formatType = responseFormat["type"]?.GetValue<string>();
            if (string.Equals(formatType, "json_schema", StringComparison.OrdinalIgnoreCase)
                && responseFormat["json_schema"]?["schema"] is JsonObject schema)
            {
                generationConfig["responseSchema"] = CleanJsonSchemaForGemini(schema.DeepClone(), schema);
                generationConfig["responseMimeType"] = "application/json";
            }
            else if (string.Equals(formatType, "json_object", StringComparison.OrdinalIgnoreCase))
            {
                generationConfig["responseMimeType"] = "application/json";
            }
            else if (string.Equals(formatType, "text", StringComparison.OrdinalIgnoreCase))
            {
                generationConfig["responseMimeType"] = "text/plain";
            }
        }

        if (contents.Count == 0)
        {
            // 与 gcli2api 一致：空 contents 补默认用户消息，避免上游 400。
            contents.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = "请根据系统指令回答。" }) });
        }

        var inner = new JsonObject
        {
            ["contents"] = contents,
            ["generationConfig"] = generationConfig,
        };

        var systemInstruction = BuildGeminiSystemInstructionFromOpenAi(openAi["messages"] as JsonArray);
        if (systemInstruction is not null)
        {
            inner["systemInstruction"] = systemInstruction;
        }

        var tools = ConvertOpenAiToolsToGemini(openAi["tools"] as JsonArray);
        if (tools is { Count: > 0 })
        {
            inner["tools"] = tools;
        }

        var toolConfig = ConvertOpenAiToolChoiceToGemini(openAi["tool_choice"]);
        if (toolConfig is not null)
        {
            inner["toolConfig"] = toolConfig;
        }

        return inner;
    }

    /// <summary>
    /// 对内层 GenerateContent 请求做 gcli2api normalize_gemini_request 同款规范化：
    /// safetySettings 覆盖（BLOCK_NONE）、thinkingConfig 归一（gemini-3 用 thinkingLevel）、
    /// 强制 topK=64 / maxOutputTokens=64000、清理无效 part 与异常 text 字段。
    /// </summary>
    public static void NormalizeGeminiInner(JsonObject inner, string model)
    {
        // —— thinkingConfig 归一 ——
        NormalizeGeminiThinking(inner, model);

        // —— safetySettings：全部 BLOCK_NONE，避免 Claude Code 场景被安全策略拦截 ——
        var liteModel = model.Contains("gemini-2.5-flash-lite", StringComparison.OrdinalIgnoreCase);
        string[] liteCategories =
        [
            "HARM_CATEGORY_HATE_SPEECH",
            "HARM_CATEGORY_SEXUALLY_EXPLICIT",
            "HARM_CATEGORY_HARASSMENT",
            "HARM_CATEGORY_DANGEROUS_CONTENT"
        ];
        string[] defaultCategories =
        [
            "HARM_CATEGORY_HARASSMENT",
            "HARM_CATEGORY_HATE_SPEECH",
            "HARM_CATEGORY_SEXUALLY_EXPLICIT",
            "HARM_CATEGORY_DANGEROUS_CONTENT",
            "HARM_CATEGORY_CIVIC_INTEGRITY",
            "HARM_CATEGORY_IMAGE_HATE",
            "HARM_CATEGORY_IMAGE_DANGEROUS_CONTENT",
            "HARM_CATEGORY_IMAGE_HARASSMENT",
            "HARM_CATEGORY_IMAGE_SEXUALLY_EXPLICIT",
            "HARM_CATEGORY_JAILBREAK"
        ];
        var categories = liteModel ? liteCategories : defaultCategories;
        var safety = new JsonArray();
        foreach (var category in categories)
        {
            safety.Add(new JsonObject { ["category"] = category, ["threshold"] = "BLOCK_NONE" });
        }

        inner["safetySettings"] = safety;

        // —— 参数范围（对齐 gcli2api：强制放大避免上游截断）——
        if (inner["generationConfig"] is JsonObject config)
        {
            config["maxOutputTokens"] = 64000;
            config["topK"] = 64;
        }

        // —— part 清理：移除全空 part；text 非 string 时转字符串；text 去尾部空白 ——
        if (inner["contents"] is JsonArray contents)
        {
            for (var i = contents.Count - 1; i >= 0; i--)
            {
                if (contents[i] is not JsonObject content || content["parts"] is not JsonArray parts)
                {
                    continue;
                }

                for (var p = parts.Count - 1; p >= 0; p--)
                {
                    if (parts[p] is not JsonObject part || !HasGeminiPartValue(part))
                    {
                        parts.RemoveAt(p);
                    }
                    else if (part["text"] is { } textNode && textNode.GetValueKind() != JsonValueKind.String)
                    {
                        part["text"] = textNode.ToJsonString();
                    }
                    else if (part["text"]?.GetValue<string>() is { } text)
                    {
                        part["text"] = text.TrimEnd();
                    }
                }

                if (parts.Count == 0)
                {
                    contents.RemoveAt(i);
                }
            }

            // —— 轮次归一：合并连续相同 role 的轮次，保证严格交替（user → model → user ...） ——
            var mergedContents = new JsonArray();
            foreach (var contentNode in contents)
            {
                if (contentNode is not JsonObject content || content["parts"] is not JsonArray parts || parts.Count == 0)
                {
                    continue;
                }

                var role = string.Equals(content["role"]?.GetValue<string>(), "model", StringComparison.OrdinalIgnoreCase)
                    ? "model"
                    : "user";

                if (mergedContents.Count > 0
                    && string.Equals(mergedContents[^1]?["role"]?.GetValue<string>(), role, StringComparison.OrdinalIgnoreCase)
                    && mergedContents[^1]?["parts"] is JsonArray prevParts)
                {
                    foreach (var part in parts)
                    {
                        if (part is not null)
                        {
                            prevParts.Add(part.DeepClone());
                        }
                    }
                }
                else
                {
                    mergedContents.Add(new JsonObject
                    {
                        ["role"] = role,
                        ["parts"] = (JsonArray)parts.DeepClone()
                    });
                }
            }

            // 确保首轮必须为 user 角色（Google Gemini API 规范）
            if (mergedContents.Count > 0 && string.Equals(mergedContents[0]?["role"]?.GetValue<string>(), "model", StringComparison.OrdinalIgnoreCase))
            {
                mergedContents.Insert(0, new JsonObject
                {
                    ["role"] = "user",
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = "请根据系统指令回答。" })
                });
            }

            // —— 孤儿 functionResponse 校验与修复 ——
            // 在 Gemini 协议中，user 轮次中的每个 functionResponse 必须在紧邻的前一 model 轮次中有对应的 functionCall；
            // 若前一轮次非 model 或无匹配的 functionCall，Gemini 会报 400 INVALID_ARGUMENT。
            for (var i = 0; i < mergedContents.Count; i++)
            {
                if (mergedContents[i] is not JsonObject current
                    || !string.Equals(current["role"]?.GetValue<string>(), "user", StringComparison.OrdinalIgnoreCase)
                    || current["parts"] is not JsonArray currentParts)
                {
                    continue;
                }

                var precedingCallsById = new Dictionary<string, string>(StringComparer.Ordinal);
                var precedingCallNames = new HashSet<string>(StringComparer.Ordinal);
                if (i > 0
                    && mergedContents[i - 1] is JsonObject preceding
                    && string.Equals(preceding["role"]?.GetValue<string>(), "model", StringComparison.OrdinalIgnoreCase)
                    && preceding["parts"] is JsonArray precedingParts)
                {
                    foreach (var pNode in precedingParts)
                    {
                        if (pNode is JsonObject p && p["functionCall"] is JsonObject fc)
                        {
                            var name = fc["name"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(name))
                            {
                                precedingCallNames.Add(name);
                                if (fc["id"]?.GetValue<string>() is { Length: > 0 } id)
                                {
                                    precedingCallsById[id] = name;
                                }
                            }
                        }
                    }
                }

                for (var p = 0; p < currentParts.Count; p++)
                {
                    if (currentParts[p] is JsonObject part && part["functionResponse"] is JsonObject fr)
                    {
                        var respId = fr["id"]?.GetValue<string>();
                        var respName = fr["name"]?.GetValue<string>() ?? "tool";

                        bool isMatched;
                        if (!string.IsNullOrEmpty(respId) && precedingCallsById.TryGetValue(respId, out var actualCallName))
                        {
                            // 修正名称与前一轮 functionCall 完全一致，避免上游 400（name mismatch）。
                            fr["name"] = actualCallName;
                            isMatched = true;
                        }
                        else
                        {
                            isMatched = precedingCallNames.Contains(respName);
                        }

                        if (!isMatched)
                        {
                            // 孤儿 functionResponse 降级为普通 text part，保留上下文内容同时避免 400。
                            var respContent = fr["response"]?["result"]?.GetValue<string>()
                                ?? fr["response"]?["output"]?.GetValue<string>()
                                ?? fr["response"]?.ToJsonString()
                                ?? string.Empty;

                            currentParts[p] = new JsonObject
                            {
                                ["text"] = $"[工具结果: {respName}]\n{respContent}"
                            };
                        }
                    }
                }
            }

            inner["contents"] = mergedContents;
        }
    }

    /// <summary>
    /// 把内层 GenerateContent 请求包成最终上游请求体（{model, project, request} 封套）。
    /// Antigravity 上游额外应用 CLI 封套字段（requestId/labels/sessionId/toolConfig/userAgent 等），
    /// 与 gcli2api wrap_cli_request 一致；GeminiCLI 上游保持纯封套。
    /// </summary>
    public static string WrapGeminiUpstreamBody(JsonObject inner, string model, string? projectId, bool isAntigravity)
    {
        var upstreamModel = model;
        if (isAntigravity)
        {
            // Antigravity 模型别名与废弃对齐（对齐 Google fetchAvailableModels / deprecatedModelIds）
            if (string.Equals(upstreamModel, "gemini-3.7-flash-high", StringComparison.OrdinalIgnoreCase)
                || string.Equals(upstreamModel, "gemini-3.7-flash", StringComparison.OrdinalIgnoreCase))
            {
                upstreamModel = "gemini-3.7-flash-tiered";
            }
            else if (string.Equals(upstreamModel, "gemini-3.1-pro-high", StringComparison.OrdinalIgnoreCase))
            {
                upstreamModel = "gemini-pro-agent";
            }

            ApplyAntigravityCliWrap(inner, upstreamModel);
        }

        var payload = new JsonObject
        {
            ["model"] = upstreamModel,
            ["request"] = inner,
        };
        if (!string.IsNullOrWhiteSpace(projectId))
        {
            payload["project"] = projectId;
        }

        if (isAntigravity)
        {
            // CLI 封套字段：requestId 对齐官方真实抓包格式 agent/{uuid}/{unix_ms}/{trajectory}/{step}（标准 GUID）
            // userAgent 字段同时被转发层用于识别 Antigravity 上游并设置对应的请求头。
            payload["requestId"] = $"agent/{Guid.NewGuid():D}/{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}/{Guid.NewGuid():D}/2";
            payload["userAgent"] = "antigravity";
            payload["requestType"] = upstreamModel.Contains("image", StringComparison.OrdinalIgnoreCase) ? "image_gen" : "agent";
        }

        return payload.ToJsonString();
    }

    /// <summary>
    /// 在内层请求上覆盖思考等级（用户受保护功能：思考等级强覆盖）。
    /// gemini-3 系列用 thinkingLevel 表达；2.5 系列用 thinkingBudget 表达。
    /// </summary>
    public static void ApplyGeminiThinkingEffort(JsonObject inner, string effort, string model)
    {
        var normalized = effort.Trim().ToLowerInvariant();
        var config = inner["generationConfig"] as JsonObject ?? new JsonObject();
        inner["generationConfig"] = config;

        var baseModel = GetGeminiBaseModelName(model);
        if (baseModel.Contains("gemini-3", StringComparison.OrdinalIgnoreCase))
        {
            var level = normalized switch
            {
                "low" or "minimal" => "low",
                "medium" => baseModel.Contains("flash", StringComparison.OrdinalIgnoreCase) ? "medium" : null,
                _ => "high"
            };
            var thinking = new JsonObject { ["includeThoughts"] = level is not null };
            if (level is not null)
            {
                thinking["thinkingLevel"] = level;
            }

            config["thinkingConfig"] = thinking;
            return;
        }

        var budget = normalized switch
        {
            "minimal" => baseModel.Contains("flash", StringComparison.OrdinalIgnoreCase) ? 0 : 128,
            "low" => 1024,
            "medium" => 8192,
            "high" => 16000,
            "xhigh" => baseModel.Contains("flash", StringComparison.OrdinalIgnoreCase) ? 24576 : 32768,
            "max" => baseModel.Contains("flash", StringComparison.OrdinalIgnoreCase) ? 24576 : 32768,
            _ => 16000
        };
        config["thinkingConfig"] = new JsonObject
        {
            ["thinkingBudget"] = budget,
            ["includeThoughts"] = budget > 0
        };
    }

    /// <summary>
    /// 思考配置归一：gemini-3 系列不支持 thinkingBudget（转 thinkingLevel）；
    /// 思考模型确保 thinkingConfig 存在且 includeThoughts 有值（默认 true，对齐 gcli2api return_thoughts 默认开）。
    /// </summary>
    private static void NormalizeGeminiThinking(JsonObject inner, string model)
    {
        var baseModel = GetGeminiBaseModelName(model);
        var config = inner["generationConfig"] as JsonObject;
        var thinking = config?["thinkingConfig"] as JsonObject;

        if (thinking is not null && thinking["thinkingBudget"] is { } budgetNode && budgetNode.GetValueKind() == JsonValueKind.Number)
        {
            var budget = budgetNode.GetValue<int>();
            if (baseModel.Contains("gemini-3", StringComparison.OrdinalIgnoreCase))
            {
                // gemini-3：预算转等级（pro 不支持 medium，回落默认）。
                string? level = budget <= 0 ? null
                    : budget < 8192 ? "low"
                    : budget < 16000 ? (baseModel.Contains("flash", StringComparison.OrdinalIgnoreCase) ? "medium" : null)
                    : "high";
                thinking.Remove("thinkingBudget");
                if (level is not null)
                {
                    thinking["thinkingLevel"] = level;
                }

                thinking["includeThoughts"] = level is not null;
            }
            else if (!thinking.ContainsKey("includeThoughts"))
            {
                thinking["includeThoughts"] = budget > 0;
            }

            return;
        }

        var isThinkingModel = baseModel.Contains("think", StringComparison.OrdinalIgnoreCase)
            || baseModel.Contains("pro", StringComparison.OrdinalIgnoreCase);
        if (!isThinkingModel)
        {
            return;
        }

        config ??= new JsonObject();
        inner["generationConfig"] = config;
        thinking ??= new JsonObject();
        config["thinkingConfig"] = thinking;
        if (!thinking.ContainsKey("includeThoughts"))
        {
            thinking["includeThoughts"] = true;
        }
    }

    /// <summary>
    /// Antigravity CLI 封套（对齐 gcli2api wrap_cli_request + normalize_antigravity_request 的上游侧差异）：
    /// 移除 CLI 不发送的字段（safetySettings / stopSequences / presencePenalty / frequencyPenalty）、
    /// 注入 sessionId 与 labels、toolConfig 默认 VALIDATED、opus/sonnet 系列剥离末尾 model 消息（不支持预填充）。
    /// </summary>
    private static void ApplyAntigravityCliWrap(JsonObject inner, string model)
    {
        inner.Remove("safetySettings");
        if (inner["generationConfig"] is JsonObject config)
        {
            config.Remove("stopSequences");
            config.Remove("presencePenalty");
            config.Remove("frequencyPenalty");

            // 对齐官方 1.1.20 真实抓包：gemini-3 系列使用 thinkingBudget: -1 与 includeThoughts: true
            var modelLower = model.ToLowerInvariant();
            if (modelLower.Contains("gemini-3", StringComparison.OrdinalIgnoreCase))
            {
                if (config["thinkingConfig"] is JsonObject thinking)
                {
                    thinking.Remove("thinkingLevel");
                    if (!thinking.ContainsKey("thinkingBudget"))
                    {
                        thinking["thinkingBudget"] = -1;
                    }
                    if (!thinking.ContainsKey("includeThoughts"))
                    {
                        thinking["includeThoughts"] = true;
                    }
                }
                else
                {
                    config["thinkingConfig"] = new JsonObject
                    {
                        ["includeThoughts"] = true,
                        ["thinkingBudget"] = -1
                    };
                }
            }
            else
            {
                if (config["thinkingConfig"] is JsonObject thinking)
                {
                    thinking.Remove("thinkingLevel");
                }
            }
        }

        // sessionId：复用已有值，否则用首条用户文本哈希生成（对齐 gcli2api）。
        var sessionId = inner["sessionId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            var firstUserText = ExtractFirstGeminiUserText(inner["contents"] as JsonArray);
            if (!string.IsNullOrEmpty(firstUserText))
            {
                var digest = SHA256.HashData(Encoding.UTF8.GetBytes(firstUserText));
                var value = BitConverter.ToUInt64(digest, 0) & 0x7FFFFFFFFFFFFFFF;
                sessionId = value.ToString();
            }
            else
            {
                sessionId = (Random.Shared.NextInt64(0, 9_000_000_000_000_000_000L)).ToString();
            }
        }

        inner["sessionId"] = sessionId;
        var usedClaude = model.Contains("claude", StringComparison.OrdinalIgnoreCase).ToString().ToLowerInvariant();
        inner["labels"] = new JsonObject
        {
            ["last_step_index"] = "1",
            ["model_enum"] = model,
            ["trajectory_id"] = sessionId,
            ["used_claude"] = usedClaude,
            ["used_claude_conservative"] = usedClaude
        };

        // toolConfig 默认 VALIDATED（CLI 行为）。
        var toolConfig = inner["toolConfig"] as JsonObject ?? new JsonObject();
        inner["toolConfig"] = toolConfig;
        var functionConfig = toolConfig["functionCallingConfig"] as JsonObject ?? new JsonObject();
        toolConfig["functionCallingConfig"] = functionConfig;
        if (!functionConfig.ContainsKey("mode"))
        {
            functionConfig["mode"] = "VALIDATED";
        }

        // 不支持预填充的模型：循环移除末尾 model 消息，保证以用户消息结尾。
        var lower = model.ToLowerInvariant();
        if (lower.Contains("opus") || lower.Contains("sonnet") || lower.Contains("gemini-3.6") || lower.Contains("gemini-3.7"))
        {
            if (inner["contents"] is JsonArray contents)
            {
                while (contents.Count > 0
                       && contents[^1] is JsonObject last
                       && string.Equals(last["role"]?.GetValue<string>(), "model", StringComparison.OrdinalIgnoreCase))
                {
                    contents.RemoveAt(contents.Count - 1);
                }
            }
        }
    }

    /// <summary>
    /// 剥离模型名的思考/搜索后缀，得到上游真实模型名（对齐 gcli2api get_base_model_name）。
    /// </summary>
    public static string GetGeminiBaseModelName(string modelName)
    {
        var suffixes = new[]
        {
            "-maxthinking", "-nothinking", "-minimal", "-medium", "-search", "-think", "-high", "-max", "-low"
        };
        var result = modelName;
        bool changed;
        do
        {
            changed = false;
            foreach (var suffix in suffixes)
            {
                if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    result = result[..^suffix.Length];
                    changed = true;
                }
            }
        }
        while (changed);

        return result;
    }

    private static string? ExtractFirstGeminiUserText(JsonArray? contents)
    {
        if (contents is null)
        {
            return null;
        }

        foreach (var content in contents)
        {
            if (content is not JsonObject item
                || !string.Equals(item["role"]?.GetValue<string>(), "user", StringComparison.OrdinalIgnoreCase)
                || item["parts"] is not JsonArray parts)
            {
                continue;
            }

            foreach (var partNode in parts)
            {
                if (partNode is JsonObject part
                    && part["text"]?.GetValue<string>() is { Length: > 0 } text)
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static bool HasGeminiPartValue(JsonObject part)
    {
        foreach (var (key, value) in part)
        {
            if (string.Equals(key, "thought", StringComparison.Ordinal))
            {
                continue;
            }

            if (value is null)
            {
                continue;
            }

            if (value.GetValueKind() == JsonValueKind.String)
            {
                if (!string.IsNullOrEmpty(value.GetValue<string>()))
                {
                    return true;
                }
            }
            else if (value.GetValueKind() is JsonValueKind.Object or JsonValueKind.Array)
            {
                return true;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    // —— Anthropic → Gemini：messages / system / tools / tool_choice / generationConfig ——

    private static JsonArray ConvertAnthropicMessagesToGeminiContents(JsonArray? messages)
    {
        var contents = new JsonArray();

        // 第一遍：tool_use_id → 工具名映射（tool_result 可能不带 name）。
        var toolNamesById = new Dictionary<string, string>(StringComparer.Ordinal);
        if (messages is not null)
        {
            foreach (var messageNode in messages)
            {
                if (messageNode?["content"] is not JsonArray blocks)
                {
                    continue;
                }

                foreach (var blockNode in blocks)
                {
                    if (blockNode is JsonObject block
                        && string.Equals(block["type"]?.GetValue<string>(), "tool_use", StringComparison.OrdinalIgnoreCase)
                        && block["id"]?.GetValue<string>() is { Length: > 0 } id
                        && block["name"]?.GetValue<string>() is { Length: > 0 } name)
                    {
                        toolNamesById[id] = name;
                    }
                }
            }
        }

        if (messages is null)
        {
            return contents;
        }

        foreach (var messageNode in messages)
        {
            if (messageNode is not JsonObject message)
            {
                continue;
            }

            var role = message["role"]?.GetValue<string>();
            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var geminiRole = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, "model", StringComparison.OrdinalIgnoreCase)
                ? "model"
                : "user";

            var parts = new JsonArray();
            var content = message["content"];
            if (content is JsonArray blocks)
            {
                foreach (var blockNode in blocks)
                {
                    if (blockNode is not JsonObject block)
                    {
                        if (blockNode?.GetValueKind() == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(blockNode.GetValue<string>()))
                        {
                            parts.Add(new JsonObject { ["text"] = blockNode.GetValue<string>() });
                        }
                        continue;
                    }

                    var blockType = block["type"]?.GetValue<string>();
                    if (string.Equals(blockType, "thinking", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(blockType, "redacted_thinking", StringComparison.OrdinalIgnoreCase))
                    {
                        // 客户端回传的 thinking 块（含签名）不送回 Google，中转/换号后极易 Corrupted thought signature。
                        continue;
                    }

                    if (string.Equals(blockType, "text", StringComparison.OrdinalIgnoreCase))
                    {
                        var text = block["text"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            parts.Add(new JsonObject { ["text"] = text });
                        }
                    }
                    else if (string.Equals(blockType, "image", StringComparison.OrdinalIgnoreCase))
                    {
                        var source = block["source"] as JsonObject;
                        if (source is not null
                            && string.Equals(source["type"]?.GetValue<string>(), "base64", StringComparison.OrdinalIgnoreCase))
                        {
                            parts.Add(new JsonObject
                            {
                                ["inlineData"] = new JsonObject
                                {
                                    ["mimeType"] = source["media_type"]?.GetValue<string>() ?? "image/png",
                                    ["data"] = source["data"]?.GetValue<string>() ?? string.Empty
                                }
                            });
                        }
                    }
                    else if (string.Equals(blockType, "tool_use", StringComparison.OrdinalIgnoreCase))
                    {
                        var functionCall = new JsonObject
                        {
                            ["name"] = block["name"]?.GetValue<string>(),
                            ["args"] = block["input"]?.DeepClone() ?? new JsonObject()
                        };
                        var id = block["id"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(id))
                        {
                            functionCall["id"] = id;
                        }

                        parts.Add(new JsonObject
                        {
                            ["functionCall"] = functionCall,
                            // 官方跳过校验占位符：真实签名经客户端往返后不可信。
                            ["thoughtSignature"] = SkipThoughtSignatureValidator
                        });
                    }
                    else if (string.Equals(blockType, "tool_result", StringComparison.OrdinalIgnoreCase))
                    {
                        var output = ExtractAnthropicToolResultOutput(block["content"]);
                        var toolUseId = block["tool_use_id"]?.GetValue<string>() ?? string.Empty;
                        var functionName = toolNamesById.TryGetValue(toolUseId, out var resolved)
                            ? resolved
                            : "unknown_function";
                        var functionResponse = new JsonObject
                        {
                            ["name"] = functionName,
                            ["response"] = new JsonObject { ["output"] = output }
                        };
                        if (!string.IsNullOrEmpty(toolUseId))
                        {
                            functionResponse["id"] = toolUseId;
                        }

                        parts.Add(new JsonObject { ["functionResponse"] = functionResponse });
                    }
                }
            }
            else if (content?.GetValueKind() == JsonValueKind.String)
            {
                var text = content.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add(new JsonObject { ["text"] = text });
                }
            }

            if (parts.Count == 0)
            {
                continue;
            }

            contents.Add(new JsonObject { ["role"] = geminiRole, ["parts"] = parts });
        }

        return contents;
    }

    /// <summary>
    /// 重排 tool 消息满足上游约束：每个 functionCall 独立成 model 轮，紧随其对应的 functionResponse 作为 user 轮。
    /// </summary>
    private static JsonArray ReorganizeGeminiToolMessages(JsonArray contents)
    {
        var responsesById = new Dictionary<string, JsonObject>();
        foreach (var contentNode in contents)
        {
            if (contentNode?["parts"] is not JsonArray parts)
            {
                continue;
            }

            foreach (var partNode in parts)
            {
                if (partNode is JsonObject part
                    && part["functionResponse"] is JsonObject response
                    && response["id"]?.GetValue<string>() is { Length: > 0 } id)
                {
                    responsesById[id] = part;
                }
            }
        }

        var flattened = new List<(string Role, JsonObject Part)>();
        foreach (var contentNode in contents)
        {
            if (contentNode is not JsonObject content || content["parts"] is not JsonArray parts)
            {
                continue;
            }

            var role = content["role"]?.GetValue<string>() ?? "user";
            foreach (var partNode in parts)
            {
                if (partNode is JsonObject part)
                {
                    flattened.Add((role, part));
                }
            }
        }

        var result = new JsonArray();
        foreach (var (role, part) in flattened)
        {
            if (part.ContainsKey("functionResponse"))
            {
                continue;
            }

            if (part.ContainsKey("functionCall"))
            {
                result.Add(new JsonObject { ["role"] = "model", ["parts"] = new JsonArray(part.DeepClone()) });
                var id = part["functionCall"]?["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id) && responsesById.TryGetValue(id!, out var response))
                {
                    result.Add(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(response.DeepClone()) });
                }

                continue;
            }

            result.Add(new JsonObject { ["role"] = role, ["parts"] = new JsonArray(part.DeepClone()) });
        }

        return result;
    }

    private static JsonObject? BuildGeminiSystemInstructionFromAnthropic(JsonNode? system)
    {
        var parts = new JsonArray();
        if (system is JsonArray blocks)
        {
            foreach (var blockNode in blocks)
            {
                if (blockNode is JsonObject block
                    && string.Equals(block["type"]?.GetValue<string>(), "text", StringComparison.OrdinalIgnoreCase)
                    && block["text"]?.GetValue<string>() is { Length: > 0 } text)
                {
                    parts.Add(new JsonObject { ["text"] = text });
                }
            }
        }
        else if (system?.GetValueKind() == JsonValueKind.String && system.GetValue<string>() is { Length: > 0 } text)
        {
            parts.Add(new JsonObject { ["text"] = text });
        }

        return parts.Count > 0 ? new JsonObject { ["parts"] = parts } : null;
    }

    private static JsonObject BuildGeminiGenerationConfigFromAnthropic(JsonObject anthropic)
    {
        // 与 gcli2api build_generation_config 一致：默认温度 0.4、candidateCount 1、内置默认停止序列。
        var config = new JsonObject
        {
            ["topP"] = 1,
            ["candidateCount"] = 1,
            ["temperature"] = 0.4
        };

        if (anthropic["temperature"] is { } temperature)
        {
            config["temperature"] = temperature.DeepClone();
        }

        if (anthropic["top_p"] is { } topP)
        {
            config["topP"] = topP.DeepClone();
        }

        if (anthropic["top_k"] is { } topK)
        {
            config["topK"] = topK.DeepClone();
        }

        if (anthropic["max_tokens"] is { } maxTokens)
        {
            config["maxOutputTokens"] = maxTokens.DeepClone();
        }

        if (anthropic["thinking"] is JsonObject thinking)
        {
            var thinkingType = thinking["type"]?.GetValue<string>();
            if (string.Equals(thinkingType, "enabled", StringComparison.OrdinalIgnoreCase))
            {
                config["thinkingConfig"] = new JsonObject
                {
                    // 无 budget 时给较大默认值（对齐 gcli2api 计划模式默认 48000）。
                    ["thinkingBudget"] = thinking["budget_tokens"]?.DeepClone() ?? 48000,
                    ["includeThoughts"] = true
                };
            }
            else if (string.Equals(thinkingType, "disabled", StringComparison.OrdinalIgnoreCase))
            {
                config["thinkingConfig"] = new JsonObject { ["includeThoughts"] = false };
            }
        }

        // plan 模式（thinking enabled 且无自定义停止序列）清空默认停止序列，避免计划生成过早截断。
        var hasThinking = anthropic["thinking"] is JsonObject t
            && string.Equals(t["type"]?.GetValue<string>(), "enabled", StringComparison.OrdinalIgnoreCase);
        if (anthropic["stop_sequences"] is JsonArray stopSequences && stopSequences.Count > 0)
        {
            var merged = new JsonArray();
            foreach (var legacy in DefaultGeminiStopSequences)
            {
                merged.Add(legacy);
            }

            foreach (var stop in stopSequences)
            {
                if (stop?.GetValueKind() == JsonValueKind.String)
                {
                    merged.Add(stop.GetValue<string>());
                }
            }

            config["stopSequences"] = merged;
        }
        else if (hasThinking)
        {
            config["stopSequences"] = new JsonArray();
        }
        else
        {
            var defaults = new JsonArray();
            foreach (var legacy in DefaultGeminiStopSequences)
            {
                defaults.Add(legacy);
            }

            config["stopSequences"] = defaults;
        }

        // claude-code 新版思考强度字段：output_config.effort → thinkingConfig 预算（未显式 thinking 时兜底）。
        if (config["thinkingConfig"] is null
            && anthropic["output_config"] is JsonObject outputConfig
            && outputConfig["effort"]?.GetValue<string>() is { Length: > 0 } effort)
        {
            config["thinkingConfig"] = new JsonObject
            {
                ["thinkingBudget"] = effort.Trim().ToLowerInvariant() switch
                {
                    "low" => 1024,
                    "medium" => 8192,
                    "high" => 16000,
                    _ => 32768
                },
                ["includeThoughts"] = true
            };
        }

        return config;
    }

    private static readonly string[] DefaultGeminiStopSequences =
    [
        "<|user|>",
        "<|bot|>",
        "<|context_request|>",
        "<|endoftext|>",
        "<|end_of_turn|>"
    ];

    private static JsonArray? ConvertAnthropicToolsToGemini(JsonArray? tools)
    {
        if (tools is null || tools.Count == 0)
        {
            return null;
        }

        var declarations = new JsonArray();
        foreach (var toolNode in tools)
        {
            if (toolNode is not JsonObject tool)
            {
                continue;
            }

            var name = tool["name"]?.GetValue<string>() ?? "nameless_function";
            var declaration = new JsonObject
            {
                ["name"] = name,
                ["description"] = tool["description"]?.GetValue<string>() ?? string.Empty,
            };

            if (tool["input_schema"] is JsonObject schema)
            {
                var cleanedSchema = CleanJsonSchemaForGemini(schema.DeepClone(), schema);
                declaration["parameters"] = cleanedSchema;
            }

            declarations.Add(declaration);
        }

        if (declarations.Count == 0)
        {
            return null;
        }

        return new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
    }

    private static JsonObject? ConvertAnthropicToolChoiceToGemini(JsonNode? toolChoice)
    {
        if (toolChoice is not JsonObject choice)
        {
            return null;
        }

        var choiceType = choice["type"]?.GetValue<string>();
        if (string.Equals(choiceType, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject { ["functionCallingConfig"] = new JsonObject { ["mode"] = "AUTO" } };
        }

        if (string.Equals(choiceType, "any", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject { ["functionCallingConfig"] = new JsonObject { ["mode"] = "ANY" } };
        }

        if (string.Equals(choiceType, "tool", StringComparison.OrdinalIgnoreCase)
            && choice["name"]?.GetValue<string>() is { Length: > 0 } name)
        {
            return new JsonObject
            {
                ["functionCallingConfig"] = new JsonObject
                {
                    ["mode"] = "ANY",
                    ["allowedFunctionNames"] = new JsonArray(name)
                }
            };
        }

        return null;
    }

    private static string ExtractAnthropicToolResultOutput(JsonNode? content)
    {
        if (content is JsonArray blocks)
        {
            if (blocks.Count == 0)
            {
                return string.Empty;
            }

            if (blocks[0] is JsonObject first
                && string.Equals(first["type"]?.GetValue<string>(), "text", StringComparison.OrdinalIgnoreCase))
            {
                return first["text"]?.GetValue<string>() ?? string.Empty;
            }

            return blocks[0]?.ToJsonString() ?? string.Empty;
        }

        if (content is null)
        {
            return string.Empty;
        }

        return content.GetValueKind() == JsonValueKind.String ? content.GetValue<string>() : content.ToJsonString();
    }

    // —— OpenAI → Gemini：messages / system / tools / tool_choice / generationConfig ——

    private static JsonArray ConvertOpenAiMessagesToGeminiContents(JsonArray? messages)
    {
        var contents = new JsonArray();
        if (messages is null)
        {
            return contents;
        }

        // tool_call_id → (name, id) 映射（tool 消息可能不带 name）。
        var toolCallsById = new Dictionary<string, (string Name, string Id)>(StringComparer.Ordinal);
        foreach (var messageNode in messages)
        {
            if (messageNode?["tool_calls"] is not JsonArray toolCalls)
            {
                continue;
            }

            foreach (var callNode in toolCalls)
            {
                if (callNode?["id"]?.GetValue<string>() is { Length: > 0 } id
                    && callNode["function"]?["name"]?.GetValue<string>() is { Length: > 0 } name)
                {
                    toolCallsById[id] = (name, id);
                }
            }
        }

        var pendingToolParts = new JsonArray();
        foreach (var messageNode in messages)
        {
            if (messageNode is not JsonObject message)
            {
                continue;
            }

            var role = message["role"]?.GetValue<string>() ?? "user";
            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                var toolCallId = message["tool_call_id"]?.GetValue<string>() ?? string.Empty;
                string? functionName = null;
                if (toolCallsById.TryGetValue(toolCallId, out var mapped))
                {
                    functionName = mapped.Name;
                }
                functionName ??= message["name"]?.GetValue<string>() ?? "unknown_function";

                JsonObject responseData;
                var content = message["content"];
                if (content?.GetValueKind() == JsonValueKind.String)
                {
                    try
                    {
                        responseData = JsonNode.Parse(content.GetValue<string>()) as JsonObject ?? new JsonObject { ["result"] = content.GetValue<string>() };
                    }
                    catch
                    {
                        responseData = new JsonObject { ["result"] = content.GetValue<string>() };
                    }
                }
                else if (content is JsonObject contentObject)
                {
                    responseData = contentObject;
                }
                else
                {
                    responseData = new JsonObject { ["result"] = content?.ToJsonString() ?? string.Empty };
                }

                var functionResponse = new JsonObject
                {
                    ["name"] = functionName,
                    ["response"] = responseData.DeepClone()
                };
                if (!string.IsNullOrEmpty(toolCallId))
                {
                    functionResponse["id"] = toolCallId;
                }

                pendingToolParts.Add(new JsonObject { ["functionResponse"] = functionResponse });
                continue;
            }

            if (pendingToolParts.Count > 0)
            {
                // 非工具消息前先 flush 累积的 functionResponse parts（Gemini 要求连续 user 轮聚合）。
                contents.Add(new JsonObject { ["role"] = "user", ["parts"] = pendingToolParts });
                pendingToolParts = new JsonArray();
            }

            var geminiRole = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? "model" : "user";
            var parts = new JsonArray();

            if (message["tool_calls"] is JsonArray calls)
            {
                AppendOpenAiTextParts(parts, message["content"]);
                foreach (var callNode in calls)
                {
                    if (callNode?["function"] is not JsonObject function)
                    {
                        continue;
                    }

                    var functionName = function["name"]?.GetValue<string>();
                    if (string.IsNullOrEmpty(functionName))
                    {
                        continue;
                    }

                    JsonObject? args = null;
                    var argumentsNode = function["arguments"];
                    if (argumentsNode?.GetValueKind() == JsonValueKind.String
                        && argumentsNode.GetValue<string>() is { } argumentsJson)
                    {
                        try
                        {
                            args = JsonNode.Parse(argumentsJson) as JsonObject;
                        }
                        catch
                        {
                            // 参数 JSON 非法时（例如历史消息被客户端截断），兜底为空对象，绝不能丢弃工具调用以防破坏对话轮次配对。
                            args = new JsonObject();
                        }
                    }
                    else if (function["arguments"] is JsonObject argsObject)
                    {
                        args = argsObject;
                    }

                    var functionCall = new JsonObject
                    {
                        ["name"] = functionName,
                        ["args"] = args ?? new JsonObject()
                    };
                    var id = callNode["id"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(id))
                    {
                        functionCall["id"] = id;
                    }

                    parts.Add(new JsonObject
                    {
                        ["functionCall"] = functionCall,
                        ["thoughtSignature"] = SkipThoughtSignatureValidator
                    });
                }
            }
            else if (message["content"] is { } content)
            {
                AppendOpenAiTextParts(parts, content);
            }

            if (parts.Count > 0)
            {
                contents.Add(new JsonObject { ["role"] = geminiRole, ["parts"] = parts });
            }
        }

        if (pendingToolParts.Count > 0)
        {
            contents.Add(new JsonObject { ["role"] = "user", ["parts"] = pendingToolParts });
        }

        return contents;
    }

    private static void AppendOpenAiTextParts(JsonArray parts, JsonNode? content)
    {
        if (content is null)
        {
            return;
        }

        if (content.GetValueKind() == JsonValueKind.String)
        {
            var text = content.GetValue<string>();
            if (!string.IsNullOrEmpty(text))
            {
                parts.Add(new JsonObject { ["text"] = text });
            }

            return;
        }

        if (content is JsonArray blocks)
        {
            foreach (var blockNode in blocks)
            {
                if (blockNode is not JsonObject block)
                {
                    continue;
                }

                var blockType = block["type"]?.GetValue<string>();
                if (string.Equals(blockType, "text", StringComparison.OrdinalIgnoreCase)
                    && block["text"]?.GetValue<string>() is { Length: > 0 } text)
                {
                    parts.Add(new JsonObject { ["text"] = text });
                }
                else if (string.Equals(blockType, "image_url", StringComparison.OrdinalIgnoreCase))
                {
                    // data:image/png;base64,xxx → inlineData。
                    var url = block["image_url"]?["url"]?.GetValue<string>();
                    if (url?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        var metaSeparator = url.IndexOf(',', StringComparison.Ordinal);
                        if (metaSeparator > 5 && metaSeparator < url.Length - 1)
                        {
                            var meta = url[5..metaSeparator];
                            var mime = meta.Contains(';') ? meta[..meta.IndexOf(';')] : meta;
                            parts.Add(new JsonObject
                            {
                                ["inlineData"] = new JsonObject
                                {
                                    ["mimeType"] = mime,
                                    ["data"] = url[(metaSeparator + 1)..]
                                }
                            });
                        }
                    }
                }
            }
        }
        else if (content is JsonObject { } contentObject && contentObject["text"]?.GetValue<string>() is { Length: > 0 } objectText)
        {
            parts.Add(new JsonObject { ["text"] = objectText });
        }
    }

    private static JsonObject? BuildGeminiSystemInstructionFromOpenAi(JsonArray? messages)
    {
        var parts = new JsonArray();
        if (messages is not null)
        {
            foreach (var messageNode in messages)
            {
                if (messageNode is JsonObject message
                    && string.Equals(message["role"]?.GetValue<string>(), "system", StringComparison.OrdinalIgnoreCase))
                {
                    AppendOpenAiTextParts(parts, message["content"]);
                }
            }
        }

        return parts.Count > 0 ? new JsonObject { ["parts"] = parts } : null;
    }

    private static JsonObject BuildGeminiGenerationConfigFromOpenAi(JsonObject openAi)
    {
        var config = new JsonObject();
        if (openAi["temperature"] is { } temperature)
        {
            config["temperature"] = temperature.DeepClone();
        }

        if (openAi["top_p"] is { } topP)
        {
            config["topP"] = topP.DeepClone();
        }

        if (openAi["top_k"] is { } topK)
        {
            config["topK"] = topK.DeepClone();
        }

        var maxOutputTokens = openAi["max_completion_tokens"] ?? openAi["max_tokens"];
        if (maxOutputTokens is not null)
        {
            config["maxOutputTokens"] = maxOutputTokens.DeepClone();
        }

        if (openAi["stop"] is { } stop)
        {
            if (stop.GetValueKind() == JsonValueKind.String)
            {
                config["stopSequences"] = new JsonArray(stop.GetValue<string>());
            }
            else if (stop is JsonArray stopArray)
            {
                config["stopSequences"] = stopArray.DeepClone();
            }
        }

        if (openAi["frequency_penalty"] is { } frequencyPenalty)
        {
            config["frequencyPenalty"] = frequencyPenalty.DeepClone();
        }

        if (openAi["presence_penalty"] is { } presencePenalty)
        {
            config["presencePenalty"] = presencePenalty.DeepClone();
        }

        if (openAi["seed"] is { } seed)
        {
            config["seed"] = seed.DeepClone();
        }

        // OpenAI reasoning_effort → thinkingConfig（effort 语义与 Anthropic 覆盖一致）。
        if (openAi["reasoning_effort"]?.GetValue<string>() is { Length: > 0 } effort)
        {
            config["thinkingConfig"] = new JsonObject
            {
                ["thinkingBudget"] = effort.Trim().ToLowerInvariant() switch
                {
                    "low" or "minimal" => 1024,
                    "medium" => 8192,
                    "high" => 16000,
                    _ => 32768
                },
                ["includeThoughts"] = true
            };
        }

        return config;
    }

    private static JsonArray? ConvertOpenAiToolsToGemini(JsonArray? tools)
    {
        if (tools is null || tools.Count == 0)
        {
            return null;
        }

        var declarations = new JsonArray();
        foreach (var toolNode in tools)
        {
            if (toolNode is not JsonObject tool
                || !string.Equals(tool["type"]?.GetValue<string>(), "function", StringComparison.OrdinalIgnoreCase)
                || tool["function"] is not JsonObject function
                || function["name"]?.GetValue<string>() is not { Length: > 0 } name)
            {
                continue;
            }

            var declaration = new JsonObject
            {
                ["name"] = name,
                ["description"] = function["description"]?.GetValue<string>() ?? string.Empty,
            };

            if (function["parameters"] is JsonObject schema)
            {
                var cleanedSchema = CleanJsonSchemaForGemini(schema.DeepClone(), schema);
                declaration["parameters"] = cleanedSchema;
            }

            declarations.Add(declaration);
        }

        if (declarations.Count == 0)
        {
            return null;
        }

        return new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });
    }

    private static JsonObject? ConvertOpenAiToolChoiceToGemini(JsonNode? toolChoice)
    {
        if (toolChoice?.GetValueKind() == JsonValueKind.String)
        {
            return toolChoice.GetValue<string>() switch
            {
                "auto" => new JsonObject { ["functionCallingConfig"] = new JsonObject { ["mode"] = "AUTO" } },
                "required" => new JsonObject { ["functionCallingConfig"] = new JsonObject { ["mode"] = "ANY" } },
                _ => null
            };
        }

        if (toolChoice is JsonObject choice
            && choice["function"]?["name"]?.GetValue<string>() is { Length: > 0 } name)
        {
            return new JsonObject
            {
                ["functionCallingConfig"] = new JsonObject
                {
                    ["mode"] = "ANY",
                    ["allowedFunctionNames"] = new JsonArray(name)
                }
            };
        }

        return null;
    }

    /// <summary>
    /// 清理工具参数 JSON Schema 以适配 Code Assist 的 parametersJsonSchema：
    /// 解析 $ref（递归，含 ~0/~1 转义）、拍平 allOf、移除不支持字段、type 数组取首个非 null、
    /// nullable/校验信息并入 description（对齐 gcli2api clean_json_schema + _clean_parameters_json_schema）。
    /// </summary>
    private static JsonObject CleanJsonSchemaForGemini(JsonNode? node, JsonObject root)
    {
        var result = new JsonObject();
        if (node is not JsonObject schema)
        {
            return result;
        }

        // $ref 解析：取被引用对象，保留本地 description/default 覆盖。
        JsonObject effective = schema;
        var refName = schema["$ref"]?.GetValue<string>()
            ?? schema["ref"]?.GetValue<string>();
        if (refName?.StartsWith("#/", StringComparison.Ordinal) == true)
        {
            var resolved = ResolveSchemaRef(root, refName);
            if (resolved is not null)
            {
                effective = resolved;
            }
        }

        // allOf 拍平：合并子 schema 的 properties/required。
        if (effective["allOf"] is JsonArray allOf)
        {
            var merged = new JsonObject();
            var properties = new JsonObject();
            var required = new JsonArray();
            foreach (var itemNode in allOf)
            {
                if (itemNode is not JsonObject item)
                {
                    continue;
                }

                if (item["properties"] is JsonObject itemProperties)
                {
                    foreach (var (key, value) in itemProperties)
                    {
                        properties[key] = value?.DeepClone();
                    }
                }

                if (item["required"] is JsonArray itemRequired)
                {
                    foreach (var nameNode in itemRequired)
                    {
                        if (nameNode?.GetValueKind() == JsonValueKind.String)
                        {
                            required.Add(nameNode.GetValue<string>());
                        }
                    }
                }

                foreach (var (key, value) in item)
                {
                    if (string.Equals(key, "properties") || string.Equals(key, "required") || string.Equals(key, "allOf"))
                    {
                        continue;
                    }

                    merged[key] = value?.DeepClone();
                }
            }

            foreach (var (key, value) in effective)
            {
                if (string.Equals(key, "allOf"))
                {
                    continue;
                }

                if (string.Equals(key, "properties"))
                {
                    if (properties.Count == 0)
                    {
                        continue;
                    }

                    foreach (var (propertyKey, propertyValue) in (JsonObject)value!)
                    {
                        properties[propertyKey] = propertyValue?.DeepClone();
                    }

                    continue;
                }

                if (string.Equals(key, "required") && required.Count > 0)
                {
                    continue;
                }

                merged[key] = value?.DeepClone();
            }

            if (properties.Count > 0)
            {
                merged["properties"] = properties;
            }

            if (required.Count > 0)
            {
                merged["required"] = required;
            }

            effective = merged;
        }

        string[] unsupportedKeys =
        [
            "$schema", "$id", "$ref", "ref", "$defs", "definitions", "title",
            "example", "examples", "readOnly", "writeOnly", "default",
            "exclusiveMaximum", "exclusiveMinimum", "oneOf", "anyOf",
            "const", "additionalItems", "contains", "patternProperties",
            "dependencies", "propertyNames", "if", "then", "else",
            "contentEncoding", "contentMediaType", "nullable", "additionalProperties"
        ];
        string[] validationFields = ["minLength", "maxLength", "minimum", "maximum", "minItems", "maxItems"];

        var validations = new List<string>();
        foreach (var field in validationFields)
        {
            if (effective.ContainsKey(field) && effective[field] is { } value)
            {
                validations.Add($"{field}: {value.ToJsonString()}");
            }
        }

        foreach (var (key, value) in effective)
        {
            if (unsupportedKeys.Contains(key, StringComparer.Ordinal)
                || validationFields.Contains(key, StringComparer.Ordinal))
            {
                continue;
            }

            if (string.Equals(key, "type", StringComparison.Ordinal) && value is JsonArray typeArray)
            {
                var nonNull = typeArray
                    .Where(t => t?.GetValueKind() == JsonValueKind.String)
                    .Select(t => t!.GetValue<string>())
                    .FirstOrDefault(t => !string.Equals(t, "null", StringComparison.OrdinalIgnoreCase));
                result[key] = nonNull ?? "string";
                continue;
            }

            if (string.Equals(key, "description", StringComparison.Ordinal) && validations.Count > 0)
            {
                var description = value?.GetValue<string>() ?? string.Empty;
                result[key] = $"{description} ({string.Join(", ", validations)})";
                continue;
            }

            if (value is JsonObject childObject)
            {
                result[key] = CleanJsonSchemaForGemini(childObject, root);
            }
            else if (value is JsonArray childArray)
            {
                var cleaned = new JsonArray();
                foreach (var itemNode in childArray)
                {
                    cleaned.Add(itemNode is JsonObject itemObject
                        ? CleanJsonSchemaForGemini(itemObject, root)
                        : itemNode?.DeepClone());
                }

                result[key] = cleaned;
            }
            else
            {
                result[key] = value?.DeepClone();
            }
        }

        if (validations.Count > 0 && !result.ContainsKey("description"))
        {
            result["description"] = $"Validation: {string.Join(", ", validations)}";
        }

        // 有 properties 但没有显式 type 时补 object。
        if (result.ContainsKey("properties") && !result.ContainsKey("type"))
        {
            result["type"] = "object";
        }

        // Google Antigravity / Gemini 严格校验防御：
        // 如果 schema 中声明了 required 数组，但 required 包含的字段在 properties 中未定义，
        // Google 上游会直接拒绝并报：requires unspecified property 'xxx'。
        // 网关在此处自动对齐：剔除 properties 中不存在的 required 项，彻底避免上游 400。
        if (result.TryGetPropertyValue("required", out var reqNode) && reqNode is JsonArray reqArr)
        {
            var propObj = result["properties"] as JsonObject;
            for (var i = reqArr.Count - 1; i >= 0; i--)
            {
                var reqPropName = reqArr[i]?.GetValue<string>();
                if (string.IsNullOrEmpty(reqPropName) || propObj == null || !propObj.ContainsKey(reqPropName))
                {
                    reqArr.RemoveAt(i);
                }
            }

            if (reqArr.Count == 0)
            {
                result.Remove("required");
            }
        }

        return result;
    }

    /// <summary>
    /// 按 JSON Pointer（#/definitions/Foo 形式）从根 schema 解析引用节点。
    /// </summary>
    private static JsonObject? ResolveSchemaRef(JsonObject root, string reference)
    {
        JsonNode? node = root;
        foreach (var raw in reference[2..].Split('/'))
        {
            var segment = raw.Replace("~1", "/").Replace("~0", "~");
            if (node is not JsonObject current || !current.TryGetPropertyValue(segment, out node))
            {
                return null;
            }
        }

        return node as JsonObject;
    }
}
