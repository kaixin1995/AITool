using AITool.Application.Networking;
using AITool.Domain.Proxy;

namespace AITool.Application.Proxy;

/// <summary>Digests only: raw upstream identifiers must never enter persistence or diagnostics.</summary>
public sealed record UpstreamReferenceLookup(UpstreamReferenceKind Kind, string LookupDigest);

/// <summary>Hard routing restriction, applied before candidate policy/ranking and every retry.</summary>
public sealed record UpstreamReferenceRestriction(AccountIdentity Owner, string UpstreamNamespace);

/// <summary>
/// Trusted forwarding context, not client JSON. Owner is the actual provider local row, or
/// AccountIdentity("site", credential-key Guid when available, otherwise site Guid).
/// ClientScope is an authenticated stable client-key identity, never a bearer secret.
/// </summary>
public sealed record UpstreamReferenceContext(
    string ClientScope,
    AccountIdentity Owner,
    string UpstreamNamespace,
    UpstreamReferenceRestriction? Restriction = null);

public interface IUpstreamReferenceOwnerStore
{
    Task<UpstreamReferenceRestriction?> ResolveAsync(string clientScope,
        IReadOnlyCollection<UpstreamReferenceLookup> references, CancellationToken ct = default);

    /// <summary>Atomic durable pre-exposure commit. Failure MUST prevent body/event exposure.</summary>
    Task CommitAsync(UpstreamReferenceContext context, IReadOnlyCollection<UpstreamReferenceLookup> references,
        bool native, CancellationToken ct = default);
}

public interface IUpstreamReferenceGuard
{
    /// <summary>Call on ORIGINAL JSON, before normalization/removal/rewriting of reference fields.</summary>
    Task<UpstreamReferenceRestriction?> ResolveRequestAsync(string clientScope, string originalRawJson,
        CancellationToken ct = default);

    /// <summary>
    /// Await before exposing each output body/event. JSON only; the caller owns SSE framing.
    /// native=false means locally generated/converted identifiers that cannot be replayed upstream.
    /// No transcript is stored. A restriction must be carried from request resolution into this context.
    /// </summary>
    Task CommitOutputAsync(UpstreamReferenceContext context, string bodyOrEventJson, bool native,
        CancellationToken ct = default);
}

/// <summary>Safe fixed messages only; never attach raw JSON, reference values, or DB inner exceptions.</summary>
public sealed class UpstreamReferenceException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public static class UpstreamReferenceNamespace
{
    /// <summary>Origin (including non-default port) plus protocol. No credentials, path, query or fragment.</summary>
    public static string Create(string origin, string protocol)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || (uri.Scheme != "https" && uri.Scheme != "http") || !string.IsNullOrEmpty(uri.UserInfo)
            || string.IsNullOrWhiteSpace(protocol) || protocol.Length > 64
            || protocol.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new UpstreamReferenceException("invalid_reference_context", "The upstream reference context is invalid.");
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + "|" + protocol.ToLowerInvariant();
    }
}
