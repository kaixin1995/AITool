namespace AITool.Application.Networking;

/// <summary>
/// Acquires account-isolated clients for an already resolved network policy.
/// Policy validation/revocation belongs to the resolver and must run before each acquisition/send.
/// </summary>
public interface INetworkTransport
{
    /// <summary>
    /// Scope and purpose are opaque, case-sensitive identifiers. Hold the lease until the response
    /// body/stream has been consumed and disposed. Do not mutate the client's settings or default
    /// headers; attach authorization and other request state to each HttpRequestMessage instead.
    /// </summary>
    ValueTask<INetworkTransportLease> AcquireAsync(
        ResolvedNetworkConfiguration configuration,
        string isolationScope,
        string purpose,
        CancellationToken cancellationToken = default);
}

/// <summary>Keeps a client and its connection pool alive for an entire request, including streaming.</summary>
public interface INetworkTransportLease : IAsyncDisposable
{
    /// <summary>A transport-owned client; callers must neither dispose nor reconfigure it.</summary>
    HttpClient Client { get; }
}
