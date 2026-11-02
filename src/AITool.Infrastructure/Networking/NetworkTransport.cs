using System.Net;
using AITool.Application.Networking;
using AITool.Domain.Operations;

namespace AITool.Infrastructure.Networking;

/// <summary>
/// Singleton, bounded, account-isolated connection pool. No policy lookup or network I/O occurs
/// under its lock. Retired entries remain alive (and count toward capacity) until their last lease ends.
/// </summary>
public sealed class NetworkTransport : INetworkTransport, IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<PoolKey, Entry> _available = new();
    private readonly HashSet<Entry> _live = new();
    private readonly int _capacity;
    private readonly Action<SocketsHttpHandler>? _configureHandler;
    private long _clock;
    private bool _disposed;

    public NetworkTransport(int capacity = 64) : this(capacity, null) { }

    // Deliberately internal: tests can install loopback-only certificate trust or a controlled
    // default proxy without exposing a production certificate-validation bypass configuration.
    internal NetworkTransport(int capacity, Action<SocketsHttpHandler>? configureHandler)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _configureHandler = configureHandler;
    }

    public ValueTask<INetworkTransportLease> AcquireAsync(
        ResolvedNetworkConfiguration configuration,
        string isolationScope,
        string purpose,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(isolationScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        cancellationToken.ThrowIfCancellationRequested();
        Validate(configuration);

        var key = new PoolKey(isolationScope, purpose, configuration.Mode,
            configuration.ProxyProfileId, configuration.ProfileRevision);
        List<HttpClient> dispose = [];
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                if (_available.TryGetValue(key, out var existing))
                {
                    existing.References++;
                    existing.LastUsed = ++_clock;
                    return ValueTask.FromResult<INetworkTransportLease>(new Lease(this, existing));
                }

                // A different resolved configuration replaces, rather than reconfigures, the client.
                foreach (var old in _available.Values.Where(e =>
                    e.Key.Scope == isolationScope && e.Key.Purpose == purpose).ToArray())
                {
                    Retire(old, dispose);
                }

                while (_live.Count >= _capacity)
                {
                    var victim = _available.Values.Where(e => e.References == 0)
                        .MinBy(e => e.LastUsed);
                    if (victim is null)
                    {
                        throw new InvalidOperationException(
                            "Network transport capacity is exhausted by active request leases. " +
                            "Release completed request leases or increase the transport capacity.");
                    }
                    Retire(victim, dispose);
                }

                // Handler/client creation performs no I/O. All settings are assigned once, before
                // publication; per-request auth/cookies must never live in shared client state.
                var entry = new Entry(key, CreateClient(configuration))
                {
                    References = 1,
                    LastUsed = ++_clock
                };
                _live.Add(entry);
                _available.Add(key, entry);
                return ValueTask.FromResult<INetworkTransportLease>(new Lease(this, entry));
            }
        }
        finally
        {
            foreach (var client in dispose) client.Dispose();
        }
    }

    private static void Validate(ResolvedNetworkConfiguration configuration)
    {
        if (configuration.Mode is not (NetworkMode.Legacy or NetworkMode.Direct or NetworkMode.ProxyProfile))
            throw new ArgumentException("Unsupported network mode.", nameof(configuration));
        if (configuration.Mode != NetworkMode.ProxyProfile) return;
        if (configuration.ProxyProfileId is null || configuration.ProxyProfileId == Guid.Empty ||
            configuration.ProfileRevision < 1 || configuration.ProxyEndpoint is not { IsAbsoluteUri: true } endpoint ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || string.IsNullOrEmpty(endpoint.Host) ||
            endpoint.Port < 1 || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) ||
            endpoint.AbsolutePath is not ("" or "/") ||
            endpoint.Scheme is not ("http" or "https" or "socks4" or "socks4a" or "socks5"))
        {
            // Never include a proxy URL, username, password, or raw handler exception in errors.
            throw new ArgumentException("A valid resolved proxy profile is required.", nameof(configuration));
        }
    }

    private HttpClient CreateClient(ResolvedNetworkConfiguration configuration)
    {
        var handler = new SocketsHttpHandler();
        try
        {
            _configureHandler?.Invoke(handler);
            handler.UseCookies = false;
            // Redirects must be explicit new sends so policy revocation is checked on every hop.
            // Never silently carry a vendor request to a different authentication namespace.
            handler.AllowAutoRedirect = false;
            handler.Credentials = null;
            handler.DefaultProxyCredentials = null;
            handler.PooledConnectionLifetime = TimeSpan.FromMinutes(15);
            handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);
            handler.KeepAlivePingDelay = TimeSpan.FromSeconds(30);
            handler.KeepAlivePingTimeout = TimeSpan.FromSeconds(5);
            handler.UseProxy = configuration.Mode != NetworkMode.Direct;
            if (configuration.Mode == NetworkMode.ProxyProfile)
            {
                var proxy = new WebProxy(configuration.ProxyEndpoint!);
                if (configuration.Credentials is { } credentials)
                {
                    // Snapshot credentials: never retain a mutable resolver-owned credential object.
                    proxy.Credentials = new NetworkCredential(credentials.UserName, credentials.Password, credentials.Domain);
                }
                handler.Proxy = proxy;
            }
            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    private void Retire(Entry entry, List<HttpClient> dispose)
    {
        _available.Remove(entry.Key);
        entry.Retired = true;
        if (entry.References == 0)
        {
            _live.Remove(entry);
            dispose.Add(entry.Client);
        }
    }

    private void Release(Entry entry)
    {
        HttpClient? dispose = null;
        lock (_gate)
        {
            entry.References--;
            if (entry.References == 0 && entry.Retired)
            {
                _live.Remove(entry);
                dispose = entry.Client;
            }
        }
        dispose?.Dispose();
    }

    /// <summary>Stop acquisitions; active streams remain alive until their final lease is released.</summary>
    public void Dispose()
    {
        List<HttpClient> dispose = [];
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _available.Values.ToArray()) Retire(entry, dispose);
        }
        foreach (var client in dispose) client.Dispose();
    }

    private readonly record struct PoolKey(string Scope, string Purpose, NetworkMode Mode, Guid? ProfileId, long Revision);

    private sealed class Entry(PoolKey key, HttpClient client)
    {
        public PoolKey Key { get; } = key;
        public HttpClient Client { get; } = client;
        public int References { get; set; }
        public long LastUsed { get; set; }
        public bool Retired { get; set; }
    }

    private sealed class Lease(NetworkTransport owner, Entry entry) : INetworkTransportLease
    {
        private Entry? _entry = entry;
        public HttpClient Client => Volatile.Read(ref _entry)?.Client
            ?? throw new ObjectDisposedException(nameof(INetworkTransportLease));

        public ValueTask DisposeAsync()
        {
            var released = Interlocked.Exchange(ref _entry, null);
            if (released is not null) owner.Release(released);
            return ValueTask.CompletedTask;
        }
    }
}
