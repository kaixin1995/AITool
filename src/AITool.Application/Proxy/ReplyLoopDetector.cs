using System.Text;
using System.Text.Json;

namespace AITool.Application.Proxy;

/// <summary>Bounded sentence windows; reasoning and text are counted separately, tool arguments excluded.</summary>
public sealed class ReplyLoopDetector
{
    private sealed class Window
    {
        public readonly StringBuilder Pending = new();
        public readonly Queue<string> Units = new();
        public bool Append(string value)
        {
            foreach (var c in value)
            {
                if (Pending.Length < 4096) Pending.Append(c);
                if (c is not ('\n' or '.' or '!' or '?' or '。' or '！' or '？')) continue;
                var unit = Pending.ToString().Trim().ToLowerInvariant(); Pending.Clear();
                // Ignore code, numeric logs and punctuation-only units.
                if (!unit.Any(char.IsLetter) || unit.Any(char.IsDigit) || unit.Contains('{') || unit.Contains(';')) continue;
                Units.Enqueue(unit.Length > 256 ? unit[..256] : unit);
                if (Units.Count > 256) Units.Dequeue();
                if (Units.Count == 256 && Units.Distinct(StringComparer.Ordinal).Take(25).Count() <= 24) return true;
            }
            return false;
        }
    }
    private readonly Window _text = new(), _reasoning = new();
    public bool Observe(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var gemini = root.TryGetProperty("response", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object ? wrapped : root;
            if (gemini.TryGetProperty("candidates", out var candidates) && candidates.ValueKind == JsonValueKind.Array)
                foreach (var candidate in candidates.EnumerateArray())
                    if (candidate.TryGetProperty("content", out var content) && content.TryGetProperty("parts", out var parts))
                        foreach (var part in parts.EnumerateArray())
                            if (part.TryGetProperty("text", out var value) && value.ValueKind == JsonValueKind.String
                                && (part.TryGetProperty("thought", out var thought) && thought.ValueKind == JsonValueKind.True ? _reasoning : _text).Append(value.GetString()!)) return true;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                foreach (var choice in choices.EnumerateArray())
                    if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                        foreach (var name in new[] { "content", "reasoning_content", "reasoning", "thinking" })
                            if (delta.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                                && (name == "content" ? _text : _reasoning).Append(value.GetString()!)) return true;
            if (!root.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return false;
            var type = t.GetString();
            if (type is "response.output_text.delta" or "response.reasoning_summary_text.delta"
                && root.TryGetProperty("delta", out var text) && text.ValueKind == JsonValueKind.String)
                return (type == "response.output_text.delta" ? _text : _reasoning).Append(text.GetString()!);
            if (type == "content_block_delta" && root.TryGetProperty("delta", out var block)
                && block.TryGetProperty("type", out var bt))
            {
                var field = bt.GetString() == "thinking_delta" ? "thinking" : bt.GetString() == "text_delta" ? "text" : "";
                return field.Length > 0 && block.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                    && (field == "thinking" ? _reasoning : _text).Append(value.GetString()!);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        return false;
    }
}
