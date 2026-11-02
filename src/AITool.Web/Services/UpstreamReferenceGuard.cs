using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITool.Application.Proxy;
using AITool.Domain.Proxy;

namespace AITool.Web.Services;

/// <summary>Parses only supported reference-bearing protocol fields; never stores a transcript.</summary>
public sealed class UpstreamReferenceGuard(IUpstreamReferenceOwnerStore store) : IUpstreamReferenceGuard
{
    private const int MaxIdentifierBytes = 8192;
    private static readonly UTF8Encoding ExactUtf8 = new(false, true);

    public async Task<UpstreamReferenceRestriction?> ResolveRequestAsync(string clientScope, string originalRawJson,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        UpstreamReferenceOwnerStore.ValidateScope(clientScope);
        using var doc = Parse(originalRawJson);
        var root = doc.RootElement;
        var references = new HashSet<UpstreamReferenceLookup>();
        if (root.TryGetProperty("previous_response_id", out var previous) && previous.ValueKind != JsonValueKind.Null)
            Add(references, UpstreamReferenceKind.Response, previous);
        if (root.TryGetProperty("conversation", out var conversation) && conversation.ValueKind != JsonValueKind.Null)
        {
            if (conversation.ValueKind == JsonValueKind.Object
                && conversation.EnumerateObject().Any(p => p.Name != "id")) throw Invalid();
            AddConversation(references, conversation);
        }
        if (root.TryGetProperty("item_reference", out _)) throw Invalid();
        if (root.TryGetProperty("input", out var input))
        {
            if (input.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in input.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    if (item.ValueKind != JsonValueKind.Object) throw Invalid();
                    var hasType = item.TryGetProperty("type", out var type);
                    var hasId = item.TryGetProperty("id", out var id);
                    var isReference = (hasType && type.ValueKind == JsonValueKind.String
                            && type.GetString() == "item_reference")
                        || (!hasType && hasId);
                    if (isReference)
                    {
                        // Responses ItemReference.type is optional. An untyped object with an id
                        // must never bypass ownership checks as an ordinary input message.
                        if (item.EnumerateObject().Any(p => p.Name is not ("type" or "id"))
                            || !hasId) throw Invalid();
                        Add(references, UpstreamReferenceKind.Item, id);
                    }
                    else
                    {
                        if (hasId && (!hasType || type.ValueKind != JsonValueKind.String
                            || !IsExplicitInlineItem(item, type.GetString()))) throw Invalid();
                        RejectNestedReferences(item);
                    }
                }
            }
            else if (input.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw Invalid();
        }
        return await store.ResolveAsync(clientScope, references.ToArray(), ct);
    }

    public async Task CommitOutputAsync(UpstreamReferenceContext context, string bodyOrEventJson, bool native,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        UpstreamReferenceOwnerStore.ValidateContext(context);
        using var doc = Parse(bodyOrEventJson);
        var root = doc.RootElement;
        var references = new HashSet<UpstreamReferenceLookup>();
        if (root.TryGetProperty("response", out var response))
        {
            if (response.ValueKind != JsonValueKind.Object) throw Invalid();
            CollectResponse(response, references);
        }
        else
        {
            // Only the protocol response root, never every arbitrary nested `id` field.
            var eventType = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;
            if (eventType is null || eventType == "response"
                || eventType is "response.created" or "response.in_progress" or "response.completed"
                    or "response.incomplete" or "response.failed"
                || (root.TryGetProperty("object", out var obj) && obj.ValueKind == JsonValueKind.String && obj.GetString() == "response"))
                // Legacy flat lifecycle events carry the response itself at the root. Only
                // these known lifecycle shapes qualify; delta/event correlation ids do not.
                CollectResponse(root, references);
            else if (eventType is "response.output_item.added" or "response.output_item.done")
            {
                if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) throw Invalid();
                if (item.TryGetProperty("id", out var id)) Add(references, UpstreamReferenceKind.Item, id);
            }
            if (eventType is not null && root.TryGetProperty("conversation", out var conversation)
                && conversation.ValueKind != JsonValueKind.Null)
                AddConversation(references, conversation);
        }
        await store.CommitAsync(context, references.ToArray(), native, ct);
    }

    private static void CollectResponse(JsonElement response, HashSet<UpstreamReferenceLookup> references)
    {
        if (response.TryGetProperty("id", out var id)) Add(references, UpstreamReferenceKind.Response, id);
        if (response.TryGetProperty("conversation", out var conversation) && conversation.ValueKind != JsonValueKind.Null)
            AddConversation(references, conversation);
        if (response.TryGetProperty("output", out var output))
        {
            if (output.ValueKind != JsonValueKind.Array) throw Invalid();
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw Invalid();
                if (item.TryGetProperty("id", out var itemId)) Add(references, UpstreamReferenceKind.Item, itemId);
            }
        }
    }

    private static void AddConversation(HashSet<UpstreamReferenceLookup> references, JsonElement conversation)
    {
        if (conversation.ValueKind == JsonValueKind.Object)
        {
            if (!conversation.TryGetProperty("id", out var id)) throw Invalid();
            Add(references, UpstreamReferenceKind.Conversation, id);
        }
        else Add(references, UpstreamReferenceKind.Conversation, conversation);
    }

    private static void Add(HashSet<UpstreamReferenceLookup> references, UpstreamReferenceKind kind, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        var id = value.GetString()!;
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxIdentifierBytes) throw Invalid();
        byte[] bytes;
        try { bytes = ExactUtf8.GetBytes(id); }
        catch (EncoderFallbackException) { throw Invalid(); }
        try
        {
            if (bytes.Length > MaxIdentifierBytes) throw Invalid();
            references.Add(new(kind, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()));
            if (references.Count > UpstreamReferenceOwnerStore.MaxReferences) throw Invalid();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool IsExplicitInlineItem(JsonElement item, string? type)
    {
        // An explicit type alone is not an escape hatch for an id-only reference. Admit
        // known inline history families only when their actual payload is present and valid.
        if (!HasString(item, "id") || !ValidOptionalStatus(item)) return false;
        return type switch
        {
            "message" => HasOnly(item, "type", "id", "role", "content", "status", "phase")
                && HasString(item, "role")
                && item.GetProperty("role").GetString() is "user" or "assistant" or "system" or "developer"
                && HasTextOrContent(item, "content")
                && OptionalString(item, "phase"),
            "function_call" => HasOnly(item, "type", "id", "call_id", "name", "arguments", "status")
                && HasString(item, "call_id") && HasString(item, "name") && HasString(item, "arguments", allowEmpty: true),
            "function_call_output" => HasOnly(item, "type", "id", "call_id", "output", "status")
                && HasString(item, "call_id") && HasTextOrContent(item, "output"),
            "custom_tool_call" => HasOnly(item, "type", "id", "call_id", "name", "input", "status")
                && HasString(item, "call_id") && HasString(item, "name") && HasString(item, "input", allowEmpty: true),
            "custom_tool_call_output" => HasOnly(item, "type", "id", "call_id", "output", "status")
                && HasString(item, "call_id") && HasTextOrContent(item, "output"),
            "reasoning" => HasOnly(item, "type", "id", "summary", "content", "encrypted_content", "status")
                && (item.TryGetProperty("summary", out _) || HasString(item, "encrypted_content"))
                && OptionalTypedTextArray(item, "summary", "summary_text")
                && OptionalTypedTextArray(item, "content", "reasoning_text")
                && OptionalString(item, "encrypted_content", nullable: true),
            "compaction" => HasOnly(item, "type", "id", "encrypted_content", "status")
                && HasString(item, "encrypted_content"),
            _ => false
        };
    }

    private static bool HasOnly(JsonElement item, params string[] fields) =>
        item.EnumerateObject().All(p => fields.Contains(p.Name, StringComparer.Ordinal));

    private static bool HasString(JsonElement item, string name, bool allowEmpty = false) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && (allowEmpty || !string.IsNullOrWhiteSpace(value.GetString()));

    private static bool OptionalString(JsonElement item, string name, bool nullable = false) =>
        !item.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.String
        || (nullable && value.ValueKind == JsonValueKind.Null);

    private static bool ValidOptionalStatus(JsonElement item) =>
        !item.TryGetProperty("status", out var status) || (status.ValueKind == JsonValueKind.String
            && status.GetString() is "in_progress" or "completed" or "incomplete" or "calling" or "failed");

    private static bool HasTextOrContent(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var content)) return false;
        if (content.ValueKind == JsonValueKind.String) return true;
        return content.ValueKind == JsonValueKind.Array && content.EnumerateArray().All(IsInlineContentPart);
    }

    private static bool IsInlineContentPart(JsonElement part)
    {
        if (part.ValueKind != JsonValueKind.Object || !HasString(part, "type")) return false;
        return part.GetProperty("type").GetString() switch
        {
            "input_text" or "output_text" => HasString(part, "text", allowEmpty: true),
            "refusal" => HasString(part, "refusal", allowEmpty: true),
            "input_image" => HasString(part, "image_url") || HasString(part, "file_id"),
            "input_file" => HasString(part, "file_data") || HasString(part, "file_id") || HasString(part, "file_url"),
            "input_audio" => part.TryGetProperty("input_audio", out var audio)
                && audio.ValueKind == JsonValueKind.Object && HasString(audio, "data") && HasString(audio, "format"),
            _ => false
        };
    }

    private static bool OptionalTypedTextArray(JsonElement item, string name, string expectedType)
    {
        if (!item.TryGetProperty(name, out var values)) return true;
        return values.ValueKind == JsonValueKind.Array && values.EnumerateArray().All(part =>
            part.ValueKind == JsonValueKind.Object && HasOnly(part, "type", "text")
            && HasString(part, "type") && part.GetProperty("type").GetString() == expectedType
            && HasString(part, "text", allowEmpty: true));
    }

    private static void RejectNestedReferences(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                && type.GetString() == "item_reference") throw Invalid();
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name is "item_reference" or "previous_response_id" or "conversation") throw Invalid();
                RejectNestedReferences(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectNestedReferences(item);
    }

    private static JsonDocument Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { throw Invalid(); }
        try
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw Invalid();
            RejectDuplicates(doc.RootElement);
            return doc;
        }
        catch { doc.Dispose(); throw; }
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid();
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicates(child);
    }

    private static UpstreamReferenceException Invalid() => new("invalid_reference_shape", "The upstream reference shape is invalid or unsupported.");
}
