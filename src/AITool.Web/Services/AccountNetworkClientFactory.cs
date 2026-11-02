using System.Net;
using AITool.Application.Networking;

namespace AITool.Web.Services;

/// <summary>
/// Creates operation-owned clients which resolve policy before every send. Request credentials belong
/// on individual messages, never on shared default headers. Dispose each response/stream normally.
/// </summary>
public sealed class AccountNetworkClientFactory(
    IAccountNetworkPolicyResolver resolver,
    INetworkTransport transport)
{
    public HttpClient CreateForAccount(AccountIdentity account, string purpose)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(account.ProviderKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        return Create(ct => resolver.ResolveForAccountAsync(account, ct),
            AccountScope(account), purpose);
    }

    public HttpClient CreateForOperation(NetworkOperationContext operation, string purpose)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.IsolationKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        // ResolveAsync preserves a pending login's pinned selection/profile generation. Account-bound
        // operations use the latest account policy rather than freezing a possibly stale selection.
        return operation.Account is { } account
            ? CreateForAccount(account, purpose)
            : Create(ct => resolver.ResolveAsync(operation, ct), operation.IsolationKey, purpose);
    }

    private static string AccountScope(AccountIdentity account) => $"{account.ProviderKey}:{account.AccountId:D}";

    private HttpClient Create(Func<CancellationToken, Task<ResolvedNetworkConfiguration>> resolve,
        string scope, string purpose) => new(new PolicyHandler(resolve, transport, scope, purpose))
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private sealed class PolicyHandler(
        Func<CancellationToken, Task<ResolvedNetworkConfiguration>> resolve,
        INetworkTransport transport,
        string scope,
        string purpose) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri is not { IsAbsoluteUri: true } uri ||
                uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new NetworkPolicyException("invalid_network_request", "A valid HTTP or HTTPS request is required.");
            }

            // HttpClient marks messages as sent. The inner HttpClient must receive a fresh message;
            // independently buffer content so ownership never transfers from the caller's message.
            var clone = await CloneAsync(request, cancellationToken);
            INetworkTransportLease? lease = null;
            HttpResponseMessage? response = null;
            try
            {
                var configuration = await resolve(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                lease = await transport.AcquireAsync(configuration, scope, purpose, cancellationToken);
                response = await lease.Client.SendAsync(clone, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var lifetime = new ResponseLifetime(lease, clone);
                response.Content = new LeasedContent(response.Content, lifetime);
                response.RequestMessage = request;
                lease = null; // ownership transferred to the response content, never released on headers
                return response;
            }
            catch
            {
                try { response?.Dispose(); }
                finally
                {
                    try { clone.Dispose(); }
                    finally { if (lease is not null) await lease.DisposeAsync(); }
                }
                throw;
            }
        }

        private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
                VersionPolicy = request.VersionPolicy
            };
            try
            {
                foreach (var header in request.Headers)
                    clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
                foreach (var option in request.Options)
                    clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
                if (request.Content is { } content)
                {
                    clone.Content = new ByteArrayContent(await content.ReadAsByteArrayAsync(ct));
                    clone.Content.Headers.Clear();
                    foreach (var header in content.Headers)
                        clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                return clone;
            }
            catch
            {
                clone.Dispose();
                throw;
            }
        }
    }

    private sealed class ResponseLifetime(INetworkTransportLease lease, HttpRequestMessage request)
    {
        private INetworkTransportLease? _lease = lease;

        public async ValueTask ReleaseAsync()
        {
            var owned = Interlocked.Exchange(ref _lease, null);
            if (owned is null) return;
            try { request.Dispose(); }
            finally { await owned.DisposeAsync(); }
        }

        public void Release() => ReleaseAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class LeasedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly ResponseLifetime _lifetime;

        public LeasedContent(HttpContent inner, ResponseLifetime lifetime)
        {
            _inner = inner;
            _lifetime = lifetime;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength.HasValue;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken)
        {
            try { await _inner.CopyToAsync(stream, context, cancellationToken); }
            finally { await _lifetime.ReleaseAsync(); }
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            CreateContentReadStreamAsync(CancellationToken.None);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            try { return new LeasedStream(await _inner.ReadAsStreamAsync(cancellationToken), _lifetime); }
            catch
            {
                await _lifetime.ReleaseAsync();
                throw;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing) { base.Dispose(false); return; }
            try { _inner.Dispose(); }
            finally
            {
                try { base.Dispose(true); }
                finally { _lifetime.Release(); }
            }
        }
    }

    private sealed class LeasedStream(Stream inner, ResponseLifetime lifetime) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            try
            {
                var read = inner.Read(buffer);
                if (read == 0 && buffer.Length != 0) lifetime.Release();
                return read;
            }
            catch { lifetime.Release(); throw; }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                var read = await inner.ReadAsync(buffer, cancellationToken);
                if (read == 0 && buffer.Length != 0) await lifetime.ReleaseAsync();
                return read;
            }
            catch { await lifetime.ReleaseAsync(); throw; }
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing) { base.Dispose(false); return; }
            try { inner.Dispose(); }
            finally
            {
                try { base.Dispose(true); }
                finally { lifetime.Release(); }
            }
        }

        public override async ValueTask DisposeAsync()
        {
            try { await inner.DisposeAsync(); }
            finally { await lifetime.ReleaseAsync(); }
            GC.SuppressFinalize(this);
        }
    }
}
