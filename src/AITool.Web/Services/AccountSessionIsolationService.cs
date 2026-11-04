using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AITool.Application.Networking;
using AITool.Domain.OAuth;
using AITool.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AITool.Web.Services;

/// <summary>Original client inputs captured before any protocol conversion or emulation.</summary>
public sealed class AccountSessionIsolationContext
{
    internal AccountSessionIsolationContext(string clientScope, Dictionary<string, string> headers,
        string sessionSeed, string? conversationSeed, string? threadSeed)
    {
        ClientScope = clientScope;
        Headers = new ReadOnlyDictionary<string, string>(headers);
        SessionSeed = sessionSeed;
        ConversationSeed = conversationSeed;
        ThreadSeed = threadSeed;
    }

    public string ClientScope { get; }
    internal IReadOnlyDictionary<string, string> Headers { get; }
    internal string SessionSeed { get; }
    internal string? ConversationSeed { get; }
    internal string? ThreadSeed { get; }
}

public sealed record AccountSessionIsolationResult(string PreparedRequestBody, Dictionary<string, string> ForwardHeaders);

public sealed record DerivedReferenceOwnerIdentity(AccountIdentity Owner, string? KeyGeneration);

/// <summary>Account/client-scoped session fields. Identity/device headers are deliberately not rewritten.</summary>
public sealed class AccountSessionIsolationService(SessionIsolationDeriver deriver, IServiceScopeFactory? scopeFactory = null)
{
    private static readonly string[] SessionHeaders = ["Session-Id", "X-Session-Id", "X-Session-Affinity", "X-Conversation-Id", "Conversation_id", "Thread-Id", "X-Claude-Code-Session-Id", "X-Codex-Turn-Metadata"];

    // Explicitly non-credential fields may legitimately change per turn or emulation.
    // All other configured fields are conservatively treated as possible credentials.
    public static bool IsReferenceIdentityHeader(string name)
        => !string.Equals(name, "User-Agent", StringComparison.OrdinalIgnoreCase)
            && !SessionHeaders.Contains(name, StringComparer.OrdinalIgnoreCase);

    // HeaderProfile is merged with user overrides before this point, so trust only the
    // exact built-in request-ID templates, not a potentially credential-bearing name alone.
    public static bool IsReferenceIdentityHeader(string name, string value)
    {
        if (!IsReferenceIdentityHeader(name)) return false;
        if (string.Equals(name, "x-opencode-request", StringComparison.OrdinalIgnoreCase))
            return !string.Equals(value, "msg_${nanoid:12}", StringComparison.Ordinal);
        if (string.Equals(name, "X-Codex-Window-Id", StringComparison.OrdinalIgnoreCase))
            return !string.Equals(value, "${guid}:0", StringComparison.Ordinal);
        if (string.Equals(name, "X-Client-Request-Id", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "x-query-id", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "x-request-id", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "x-zcode-trace-id", StringComparison.OrdinalIgnoreCase))
            return !string.Equals(value, "${guid}", StringComparison.Ordinal);
        return true;
    }

    public static AccountSessionIsolationContext Capture(string clientScope, IHeaderDictionary headers,
        string originalRequestBody, string? fallbackSeed = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientScope);
        var captured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in SessionHeaders)
            if (headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value.ToString()))
                captured[name] = value.ToString();
        var root = ParseObject(originalRequestBody);
        var inner = root?["request"] as JsonObject ?? root;
        var codexMetadata = ParseObject(Header(captured, "X-Codex-Turn-Metadata") ?? "{}");
        var anthropicUser = ParseObject(Text((root?["metadata"] as JsonObject)?["user_id"]) ?? "{}");
        var session = Text(root?["prompt_cache_key"]) ?? Header(captured, "Session-Id")
            ?? Header(captured, "X-Session-Id") ?? Header(captured, "X-Session-Affinity")
            ?? Header(captured, "X-Conversation-Id") ?? Header(captured, "Conversation_id")
            ?? Header(captured, "Thread-Id") ?? Header(captured, "X-Claude-Code-Session-Id")
            ?? Text(codexMetadata?["session_id"]) ?? Text(codexMetadata?["sessionId"])
            ?? Text(anthropicUser?["session_id"]) ?? Text(inner?["sessionId"])
            ?? Text((inner?["labels"] as JsonObject)?["trajectory_id"])
            ?? fallbackSeed ?? Guid.NewGuid().ToString("N");
        return new(clientScope, captured, session, Header(captured, "Conversation_id") ?? Header(captured, "X-Conversation-Id"), Header(captured, "Thread-Id"));
    }

    /// <summary>Ordinary API credentials are mutable in place, unlike managed account identities.
    /// Bind reference ownership to the current secret without persisting or exposing its fingerprint.</summary>
    public async Task<DerivedReferenceOwnerIdentity> DeriveReferenceOwnerAsync(AccountIdentity? managedAccount, Guid siteId,
        Guid? siteKeyId, string currentCredential, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? credentialHeaders = null)
    {
        if (managedAccount is not null) return new DerivedReferenceOwnerIdentity(managedAccount, null);
        var keyIdentity = siteKeyId?.ToString("D") ?? "legacy-site-key";
        static string Field(string value) => value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value;
        var seed = new StringBuilder(Field(keyIdentity)).Append(Field(currentCredential));
        // Any configured header may be an upstream credential or account selector, including
        // vendor-specific names. Bind all configuration except explicitly non-credential
        // fields; canonicalize names while preserving exact values and unambiguous framing.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in (credentialHeaders ?? new Dictionary<string, string>())
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!names.Add(name))
                throw new AITool.Application.Proxy.UpstreamReferenceException("invalid_reference_context", "Ambiguous configured headers cannot establish stable reference ownership.");
            if (!IsReferenceIdentityHeader(name, value)) continue;
            if (value.Contains("${", StringComparison.Ordinal))
                throw new AITool.Application.Proxy.UpstreamReferenceException("invalid_reference_context", "Dynamic configured headers cannot establish stable reference ownership.");
            seed.Append(Field(name.ToLowerInvariant())).Append(Field(value));
        }
        var derivation = await deriver.DeriveWithGenerationAsync("upstream-reference-owner-v1", "site", siteId,
            "credential-instance", seed.ToString(), cancellationToken);
        // Parse the canonical hexadecimal representation, independent of native Guid byte order.
        var owner = new AccountIdentity("site", Guid.ParseExact(derivation.Value[..32], "N"));
        return new DerivedReferenceOwnerIdentity(owner, derivation.KeyGeneration);
    }

    // Affinity is a routing hint only, scoped to the authenticated caller, never an ownership proof.
    public static string? ScopeAffinity(string clientScope, string? conversationKey)
        => conversationKey is null ? null : Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{clientScope.Length}:{clientScope}{conversationKey.Length}:{conversationKey}")));

    public async Task<AccountSessionIsolationResult> ApplyAsync(AccountSessionIsolationContext context,
        AccountIdentity? account, string protocolType, bool isAntigravity, string preparedRequestBody,
        IReadOnlyDictionary<string, string>? mergedHeaders, CancellationToken cancellationToken = default)
    {
        var headers = mergedHeaders is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(mergedHeaders, StringComparer.OrdinalIgnoreCase);
        if (account is null) return new(preparedRequestBody, headers);

        Task<string> Derive(string domain, string seed) => deriver.DeriveAsync(context.ClientScope,
            account.ProviderKey, account.AccountId, domain, seed, cancellationToken);
        var root = ParseObject(preparedRequestBody);
        var responses = string.Equals(protocolType, "Responses", StringComparison.OrdinalIgnoreCase);
        var openAi = responses || string.Equals(protocolType, "OpenAI", StringComparison.OrdinalIgnoreCase);
        var anthropic = string.Equals(protocolType, "Anthropic", StringComparison.OrdinalIgnoreCase);
        var gemini = string.Equals(protocolType, "Gemini", StringComparison.OrdinalIgnoreCase);

        // These aliases can survive site/emulation header merging for any protocol. Only
        // rewrite what will actually be forwarded; capture alone must not invent a header.
        // WorkBuddy's identically named X-Session-ID is a device fingerprint, not a
        // conversation ID. Header spelling/casing and User-Agent are not evidence of vendor.
        if (headers.ContainsKey("X-Session-Id") && !await IsWorkBuddyAsync(account, cancellationToken))
            headers["X-Session-Id"] = await Derive("session", context.SessionSeed);
        if (headers.ContainsKey("X-Session-Affinity"))
            headers["X-Session-Affinity"] = await Derive("session", context.SessionSeed);
        if (headers.ContainsKey("X-Conversation-Id"))
            headers["X-Conversation-Id"] = await Derive("conversation", context.ConversationSeed ?? context.SessionSeed);

        var session = await Derive("session", context.SessionSeed);
        // Responses/Codex deliberately generate Session-Id for prompt-cache agreement.
        // Other protocol paths only rewrite headers already present after merging.
        if (responses || headers.ContainsKey("Session-Id"))
            headers["Session-Id"] = session;
        if (headers.ContainsKey("X-Claude-Code-Session-Id"))
            headers["X-Claude-Code-Session-Id"] = session;
        foreach (var (name, domain, seed) in new[] {
            ("Conversation_id", "conversation", context.ConversationSeed),
            ("Thread-Id", "thread", context.ThreadSeed) })
        {
            if (headers.ContainsKey(name))
                headers[name] = await Derive(domain, seed ?? context.SessionSeed);
        }
        // Known structured session fields only. Device/window/fingerprint metadata remains intact.
        if (headers.TryGetValue("X-Codex-Turn-Metadata", out var codexMetadataHeader))
        {
            var structured = ParseObject(codexMetadataHeader);
            var originalMetadata = ParseObject(Header(context.Headers, "X-Codex-Turn-Metadata") ?? "{}");
            if (structured is null)
                headers.Remove("X-Codex-Turn-Metadata"); // Opaque metadata cannot safely be session-rewritten.
            else
            {
                foreach (var (name, domain) in new[] { ("session_id", "session"), ("sessionId", "session"),
                    ("conversation_id", "conversation"), ("thread_id", "thread") })
                    if (Text(structured[name]) is not null)
                        structured[name] = domain == "session" ? session
                            : await Derive(domain, Text(originalMetadata?[name]) ?? context.SessionSeed);
                headers["X-Codex-Turn-Metadata"] = structured.ToJsonString();
            }
        }
        if (openAi && root is not null && (responses || root.ContainsKey("prompt_cache_key")))
            root["prompt_cache_key"] = session;
        if (anthropic)
        {
            // Claude Code's structured user_id mixes a session with device identity. Only the
            // recognized session member is ours to rewrite; opaque IDs are not session seeds.
            if (root?["metadata"] is JsonObject metadata && Text(metadata["user_id"]) is { } userId
                && ParseObject(userId) is { } structuredUser && Text(structuredUser["session_id"]) is not null)
            {
                structuredUser["session_id"] = session;
                metadata["user_id"] = structuredUser.ToJsonString();
            }
        }
        if (gemini && isAntigravity && root is not null)
        {
            var inner = root["request"] as JsonObject ?? root;
            var geminiSession = await deriver.DerivePositiveInt63Async(context.ClientScope, account.ProviderKey,
                account.AccountId, "session", context.SessionSeed, cancellationToken);
            inner["sessionId"] = geminiSession;
            var labels = inner["labels"] as JsonObject;
            if (labels is null) inner["labels"] = labels = new JsonObject();
            labels["trajectory_id"] = geminiSession;
        }
        return new(root?.ToJsonString() ?? preparedRequestBody, headers);
    }

    private async Task<bool> IsWorkBuddyAsync(AccountIdentity account, CancellationToken cancellationToken)
    {
        if (!string.Equals(account.ProviderKey, "device-oauth", StringComparison.Ordinal)) return false;
        // A missing trusted vendor lookup must fail closed rather than guess from headers.
        if (scopeFactory is null)
            throw new InvalidOperationException("Device OAuth vendor verification is unavailable.");
        using var scope = scopeFactory.CreateScope();
        using var client = scope.ServiceProvider.GetRequiredService<AppDbContext>().Client.CopyNew();
        var row = await client.Queryable<DeviceOAuthAccount>()
            .Where(a => a.Id == account.AccountId && !a.IsDeleted)
            .Select(a => new DeviceVendorRow { VendorKey = a.VendorKey }).FirstAsync(cancellationToken);
        var vendor = row?.VendorKey;
        if (string.IsNullOrWhiteSpace(vendor))
            throw new InvalidOperationException("Device OAuth vendor verification is unavailable.");
        return string.Equals(vendor, "workbuddy-cn", StringComparison.OrdinalIgnoreCase)
            || string.Equals(vendor, "workbuddy-global", StringComparison.OrdinalIgnoreCase);
    }

    public sealed class DeviceVendorRow
    {
        public string VendorKey { get; set; } = string.Empty;
    }

    private static string? Header(IReadOnlyDictionary<string, string> headers, string name)
        => headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
    private static string? Text(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
    private static JsonObject? ParseObject(string body)
    {
        try { return JsonNode.Parse(body) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
