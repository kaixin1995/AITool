using System.Text.Json.Nodes;
using System.Text;

namespace AITool.Protocol;

public sealed record ResponsesToolDefinition(string Name, string? Namespace, bool Custom, bool Search = false);

public static partial class ProxyProtocolBridge
{
    private const string CompactPrefix = "aitool-compact-v1:";
    private const string RedactedThinkingPrefix = "aitool-anthropic-redacted-v1:";

    private static string EncodeAnthropicRedactedThinking(JsonObject block) =>
        RedactedThinkingPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(block.ToJsonString()));

    private static bool TryDecodeAnthropicRedactedThinking(string? value, out JsonObject? block)
    {
        block = null;
        if (value is null || !value.StartsWith(RedactedThinkingPrefix, StringComparison.Ordinal)) return false;
        try
        {
            block = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value[RedactedThinkingPrefix.Length..]))) as JsonObject;
            return block?["type"]?.ToString() == "redacted_thinking" && block["data"] is not null;
        }
        catch { return false; }
    }

    public static string ExpandLocalCompactions(string requestBody)
    {
        if (JsonNode.Parse(requestBody) is not JsonObject root || root["input"] is not JsonArray input) return requestBody;
        for (var i = 0; i < input.Count; i++)
        {
            if (input[i] is not JsonObject item || item["type"]?.ToString() is not ("compaction" or "compaction_summary")) continue;
            var encrypted = item["encrypted_content"]?.ToString() ?? "";
            var prefix = encrypted.StartsWith(CompactPrefix, StringComparison.Ordinal) ? CompactPrefix
                : encrypted.StartsWith("magpie1:", StringComparison.Ordinal) ? "magpie1:" : null;
            if (prefix is null) continue;
            var summary = Encoding.UTF8.GetString(Convert.FromBase64String(encrypted[prefix.Length..]));
            input[i] = new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] =
                $"Earlier conversation summary. Continue the task using this context:\n{summary}" };
        }
        return root.ToJsonString();
    }

    public static bool ContainsOpaqueCompaction(string requestBody) =>
        JsonNode.Parse(requestBody) is JsonObject root && root["input"] is JsonArray input
        && input.OfType<JsonObject>().Any(i => i["type"]?.ToString() is "compaction" or "compaction_summary");

    public static bool HasCompactionTrigger(string requestBody) =>
        JsonNode.Parse(requestBody) is JsonObject root && root["input"] is JsonArray input
        && input.OfType<JsonObject>().Any(i => i["type"]?.ToString() == "compaction_trigger");

    public static string PrepareCompactSummaryRequest(string requestBody)
    {
        var root = JsonNode.Parse(requestBody)!.AsObject();
        root.Remove("tools");
        root.Remove("tool_choice");
        root["stream"] = false;
        if (root["input"] is JsonArray input)
            for (var i = input.Count - 1; i >= 0; i--)
                if (input[i] is JsonObject item && item["type"]?.ToString() is "compaction_trigger" or "additional_tools") input.RemoveAt(i);
        var prompt = "Summarize the conversation for continuation. Preserve the user's goals, decisions, relevant files, changes, errors and remaining work. Do not call tools.";
        root["instructions"] = (root["instructions"]?.ToString() ?? "") + "\n" + prompt;
        return root.ToJsonString();
    }

    public static string PreparePlainCompactSummaryRequest(string requestBody)
    {
        var root = JsonNode.Parse(PrepareCompactSummaryRequest(requestBody))!.AsObject();
        var transcript = CompactTranscript(root, 120_000, 12_000);
        root.Remove("previous_response_id");
        root.Remove("conversation");
        root["input"] = new JsonArray(new JsonObject { ["type"] = "message", ["role"] = "user",
            ["content"] = "Conversation to summarize:\n" + transcript });
        return root.ToJsonString();
    }

    private static string CompactTranscript(JsonObject root, int maximum, int perItem)
    {
        var parts = new List<string>();
        if (root["input"] is JsonValue scalar) parts.Add(scalar.ToString());
        else if (root["input"] is JsonArray input)
            foreach (var item in input.OfType<JsonObject>())
            {
                var type = item["type"]?.ToString() ?? "message";
                if (type is "compaction_trigger" or "additional_tools" or "reasoning" or "compaction" or "compaction_summary") continue;
                var text = type switch
                {
                    "message" => item["content"] is JsonArray content
                        ? string.Join("\n", content.OfType<JsonObject>().Where(p => p["type"]?.ToString() is "input_text" or "output_text" or "text").Select(p => p["text"]?.ToString()))
                        : item["content"]?.ToString(),
                    "function_call" or "custom_tool_call" => $"{item["name"]}: {item["arguments"] ?? item["input"]}",
                    "function_call_output" or "custom_tool_call_output" => item["output"]?.ToString(),
                    "tool_search_output" => item["tools"]?.ToJsonString(),
                    "web_search_call" => item["action"]?.ToJsonString(),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(text)) parts.Add($"[{item["role"]?.ToString() ?? type}] {Truncate(text, perItem)}");
            }
        var result = string.Join("\n", parts);
        return result.Length <= maximum ? result : "[Earlier conversation omitted]\n" + result[^maximum..];
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "\n[truncated]";

    public static string? BuildLocalCompactResponse(string requestBody)
    {
        var root = JsonNode.Parse(requestBody)!.AsObject();
        var transcript = CompactTranscript(root, 80_000, 8_000);
        if (string.IsNullOrWhiteSpace(transcript)) return null;
        var summary = "No model-generated summary was available. The following is an excerpt of the original conversation; earlier or long content may have been omitted.\n" + transcript;
        var response = new JsonObject { ["id"] = $"resp_{Guid.NewGuid():N}", ["object"] = "response", ["status"] = "completed",
            ["output"] = new JsonArray(new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] =
                new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = summary }) }) };
        return BuildCompactResponse(response.ToJsonString());
    }

    public static string CompactResponseAsSse(string responseBody)
    {
        var response = JsonNode.Parse(responseBody)!.AsObject();
        var item = response["output"]![0]!.DeepClone();
        var created = new JsonObject { ["id"] = response["id"]?.DeepClone(), ["object"] = "response", ["status"] = "in_progress", ["output"] = new JsonArray() };
        var events = new[]
        {
            new JsonObject { ["type"] = "response.created", ["response"] = created },
            new JsonObject { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = item.DeepClone() },
            new JsonObject { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = item },
            new JsonObject { ["type"] = "response.completed", ["response"] = response }
        };
        return string.Concat(events.Select(e => $"event: {e["type"]}\ndata: {e.ToJsonString()}\n\n"));
    }

    public static string? BuildCompactResponse(string responseBody)
    {
        var root = JsonNode.Parse(responseBody) as JsonObject;
        if (root?["status"]?.ToString() != "completed" || root["output"] is not JsonArray output) return null;
        var text = string.Join("\n", output.OfType<JsonObject>()
            .Where(i => i["type"]?.ToString() == "message")
            .SelectMany(i => (i["content"] as JsonArray ?? []).OfType<JsonObject>())
            .Where(i => i["type"]?.ToString() == "output_text")
            .Select(i => i["text"]?.ToString()).Where(t => !string.IsNullOrWhiteSpace(t)));
        if (string.IsNullOrWhiteSpace(text)) return null;
        root["output"] = new JsonArray(new JsonObject { ["type"] = "compaction", ["id"] = $"cmp_{Guid.NewGuid():N}",
            ["encrypted_content"] = CompactPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) });
        return root.ToJsonString();
    }
    private static string FlatToolName(string? ns, string name) =>
        string.IsNullOrEmpty(ns) || ns == "functions" ? name : $"{ns}__{name}";

    private static int NextResponsesOutputIndex(ChatToResponsesStreamState state)
    {
        var indices = state.ChatToolCallOutputIndices.Values.Concat(state.ToolCallOutputIndices.Values)
            .Concat(state.CompletedItems.Keys);
        if (state.MessageAdded && !string.IsNullOrEmpty(state.MessageId)) indices = indices.Append(state.MessageOutputIndex);
        if (state.ReasoningItemAdded) indices = indices.Append(state.ReasoningItemOutputIndex);
        return Math.Max(state.ReservedOutputIndexes, indices.DefaultIfEmpty(-1).Max() + 1);
    }

    private static JsonObject CustomToolSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["input"] = new JsonObject { ["type"] = "string" } },
        ["required"] = new JsonArray("input")
    };

    private static IEnumerable<(JsonObject Tool, string? Namespace)> ResponsesTools(JsonObject root)
    {
        var arrays = new List<JsonArray>();
        if (root["tools"] is JsonArray top) arrays.Add(top);
        if (root["input"] is JsonArray input)
            foreach (var item in input.OfType<JsonObject>())
                if (item["type"]?.ToString() is "additional_tools" or "tool_search_output" && item["tools"] is JsonArray extra)
                    arrays.Add(extra);
        foreach (var array in arrays)
            foreach (var entry in FlattenResponsesTools(array, null)) yield return entry;
    }

    private static IEnumerable<(JsonObject Tool, string? Namespace)> FlattenResponsesTools(JsonArray tools, string? ns)
    {
        foreach (var tool in tools.OfType<JsonObject>())
        {
            if (tool["type"]?.ToString() == "namespace" && tool["tools"] is JsonArray children)
            {
                foreach (var child in FlattenResponsesTools(children, tool["name"]?.ToString())) yield return child;
            }
            else yield return (tool, ns);
        }
    }

    public static void ConfigureResponsesTools(ChatToResponsesStreamState state, string requestBody)
    {
        if (JsonNode.Parse(requestBody) is not JsonObject root) return;
        foreach (var (tool, ns) in ResponsesTools(root))
        {
            var name = tool["name"]?.ToString();
            var type = tool["type"]?.ToString() ?? "function";
            if (type == "tool_search" && tool["execution"]?.ToString() != "server")
                state.ToolDefinitions["tool_search"] = new("tool_search", null, false, true);
            else if (!string.IsNullOrWhiteSpace(name) && type is "custom" or "function")
                state.ToolDefinitions[FlatToolName(ns, name)] = new(name, ns == "functions" ? null : ns, type == "custom");
        }
    }

    private static JsonObject ResponsesCall(string id, string callId, string name, string arguments,
        IReadOnlyDictionary<string, ResponsesToolDefinition> definitions)
    {
        var item = new JsonObject { ["id"] = id, ["status"] = "completed", ["call_id"] = callId };
        if (definitions.TryGetValue(name, out var definition))
        {
            if (definition.Search)
            {
                item["type"] = "tool_search_call";
                item["execution"] = "client";
                try { item["arguments"] = JsonNode.Parse(arguments) ?? new JsonObject(); }
                catch { item["arguments"] = new JsonObject(); }
                return item;
            }
            item["name"] = definition.Name;
            if (definition.Namespace is not null) item["namespace"] = definition.Namespace;
            if (definition.Custom)
            {
                item["type"] = "custom_tool_call";
                try { item["input"] = (JsonNode.Parse(arguments) as JsonObject)?["input"]?.ToString() ?? arguments; }
                catch { item["input"] = arguments; }
                return item;
            }
        }
        else item["name"] = name;
        item["type"] = "function_call";
        item["arguments"] = arguments;
        return item;
    }

    private static string BuildToolArgumentsDone(JsonObject item, int outputIndex)
    {
        if (item["type"]?.ToString() == "tool_search_call") return string.Empty;
        var custom = item["type"]?.ToString() == "custom_tool_call";
        var type = custom ? "response.custom_tool_call_input.done" : "response.function_call_arguments.done";
        var evt = new JsonObject
        {
            ["type"] = type, ["item_id"] = item["id"]?.DeepClone(), ["output_index"] = outputIndex,
            [custom ? "input" : "arguments"] = item[custom ? "input" : "arguments"]?.DeepClone()
        };
        return $"event: {type}\ndata: {evt.ToJsonString()}\n\n";
    }
}
